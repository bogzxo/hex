using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Hex.Core.Git;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Another machine's files opened here (docs/hex-link-plan.md, 5.3). A file of a linked device's workspace is read over
// the link into a copy under the link data folder, remote/<device>/<folder>/<path>, and opened as a tab whose title says
// whose it is. Saving the tab sends it back with the hash it had when it was read, so the other machine refuses it when
// the file moved there meanwhile, and the person gets the three ways out: look at the difference, take theirs (what
// was typed stays in undo), or write over it. While the tab is open the file is watched there: an unchanged tab takes
// what it became, a changed one says so. Without Edit granted there the tab is read only and says why.
//
// The copies are files of nobody's workspace: only the files opened are there, never a folder of the other machine's,
// so no tasks, settings or extensions of it are ever read. A file of a folder that's synced with that device is the
// local file already, and opening it opens that.
internal sealed partial class DawnScene
{
    // The folder under the link data folder the copies go in
    private const string LINK_REMOTE_FOLDER = "remote";

    // The open remote tabs by the full path of their copy
    private Dictionary<string, LinkRemoteTab>? remoteTabs;
    private Dictionary<string, LinkRemoteTab> linkRemote => LazyInitializer.EnsureInitialized(ref remoteTabs, static () => new(PlatformPaths.Comparer));
    private bool linkRemoteHooked;

    /// <summary>A tab of another machine's file: whose, its wire path, the hash it had there when it was last read or written, and whether it may be saved back.</summary>
    private sealed class LinkRemoteTab(string deviceId, string deviceName, string wire, string copy)
    {
        public string DeviceId { get; } = deviceId;
        public string DeviceName { get; set; } = deviceName;
        public string Wire { get; } = wire;
        public string Copy { get; } = copy;
        public string? Base { get; set; }
        public bool CanEdit { get; set; }
        public bool Sending { get; set; }
        public string? MovedThere { get; set; }
        public UINotification? Card { get; set; }
    }

    /// <summary>The remote tabs open, for the Link window: the device, the wire path, and whether it moved there since it was read.</summary>
    internal IReadOnlyList<LinkRemoteShown> LinkRemoteFiles =>
        [.. linkRemote.Values.Select(tab => new LinkRemoteShown(tab.DeviceId, tab.DeviceName, tab.Wire, tab.Copy, tab.CanEdit, tab.MovedThere is not null))];

    // Where a device's copies go
    private string LinkRemoteRoot(string deviceId) => Path.Combine(linkData, LINK_REMOTE_FOLDER, HexLinkGit.Slug(deviceId.Length > 16 ? deviceId[..16] : deviceId));

    /// <summary>
    /// Where the copy of a device's file goes here, the wire path through the same jail the other machine's requests go
    /// through, inside the device's copies. Throws a HexLinkException when the path isn't one.
    /// </summary>
    internal string LinkRemoteCopyOf(string deviceId, string wire) => LinkRemoteCopyIn(LinkRemoteRoot(deviceId), wire);

    /// <summary>Where the copy of a file goes in a device's copies folder: a folder per workspace folder of the device's (by a safe name and a hash of it), the rest of the path through the jail.</summary>
    internal static string LinkRemoteCopyIn(string deviceRoot, string wire)
    {
        int slash = wire.IndexOf('/');
        if (slash <= 0)
            throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, $"{wire} isn't a file of a workspace folder.");
        string folder = wire[..slash];
        string root = Path.Combine(deviceRoot, HexLinkGit.Slug(folder) + "-" + HexLinkFiles.Hash(System.Text.Encoding.UTF8.GetBytes(folder))[..6]);
        Directory.CreateDirectory(root);
        return HexLinkFiles.Resolve([new HexLinkFolder(folder, root)], wire);
    }

    /// <summary>
    /// A file of a linked device's workspace opened here by its wire path (folder/relative/path): the local file when
    /// that folder is synced with it, otherwise read into its copy and opened as a remote tab, watched there while it's
    /// open. Null when it's open, why when it isn't. From any thread.
    /// </summary>
    internal async Task<string?> LinkOpenRemote(string deviceId, string wire, int? line = null)
    {
        if (link?.PeerOf(deviceId) is not { } peer)
            return Said("link.sync_not_linked", ("device", deviceId));
        if (!HexLinkAllowed.Has(peer.Granted, HexLinkRights.View))
            return HexLinkException.NotAllowed("view").Message;
        string folder = wire.IndexOf('/') is > 0 and var slash ? wire[..slash] : wire;

        // A folder kept the same on both is the same file here
        if (LinkSyncOf(deviceId, folder) is { State: HexLinkSyncState.Idle or HexLinkSyncState.Syncing or HexLinkSyncState.PullOnly } sync &&
            LinkSyncedLocalFile(sync.LocalFolder, linkServices?.Workspace.Folders ?? [], wire) is { } local)
        {
            return await OnEditorForLink(() =>
            {
                if (!Open(local))
                    return Said("link.remote_not_opened", ("path", wire));
                LinkGoToRemoteLine(line);
                return null;
            });
        }

        try
        {
            string copy = LinkRemoteCopyOf(deviceId, wire);
            HexLinkFile file = await peer.Read(wire);
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                HexLinkFiles.WriteAtomic(copy, file.Bytes);
            });
            string? watchedHash = null;
            try
            {
                watchedHash = await peer.Watch(wire);
            }
            catch (HexLinkException)
            {
                // Too many watched there, it's opened unwatched
            }

            return await OnEditorForLink(() =>
            {
                string? error = LinkShowRemote(peer, wire, copy, watchedHash ?? file.Hash, file.Text);
                if (error is null)
                    LinkGoToRemoteLine(line);
                return error;
            });
        }
        catch (HexLinkException e)
        {
            return e.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    private void LinkGoToRemoteLine(int? line)
    {
        if (line is not { } at || !document.IsText)
            return;
        document.Editor.GoTo(at, 0);
        document.Editor.Focus();
    }

    // The copy opened as a tab of the device's, on the editor's thread; null when it's open
    private string? LinkShowRemote(HexLinkPeer peer, string wire, string copy, string hash, string? text)
    {
        LinkHookRemote();
        HexDocument? open = Context.Documents.Find(copy);
        if (open is not null && text is not null && HasChanges(open))
        {
            // Open with changes already, they stay; the file there is what's said to have moved
            Show(open);
        }
        else if (open is not null && text is not null)
        {
            TakeFromDisk(open, File.ReadAllText(copy));
            Show(open);
        }
        else if (!Open(copy))
        {
            return Said("link.remote_not_opened", ("path", wire));
        }

        HexDocument shown = document;
        bool canEdit = HexLinkAllowed.Has(peer.Granted, HexLinkRights.Edit);
        if (!linkRemote.TryGetValue(copy, out LinkRemoteTab? tab))
            linkRemote[copy] = tab = new LinkRemoteTab(peer.Device.Id, peer.Device.Name, wire, copy);
        tab.DeviceName = peer.Device.Name;
        tab.Base = hash;
        tab.CanEdit = canEdit;
        tab.MovedThere = null;
        if (shown.IsText)
            shown.Editor.ReadOnly = !canEdit;
        tabsDirty = true;
        Say(canEdit
            ? Said("link.remote_opened", ("path", wire), ("device", peer.Device.Name))
            : Said("link.remote_read_only", ("path", wire), ("device", peer.Device.Name)));
        linkDirty = true;
        return null;
    }

    // Saving and closing remote tabs heard, once
    private void LinkHookRemote()
    {
        if (linkRemoteHooked)
            return;
        linkRemoteHooked = true;
        documentSaved += LinkRemoteSaved;
        documentClosed += LinkRemoteClosed;
    }

    /// <summary>The title of a tab that's another machine's file, its name and whose it is; null for any other tab.</summary>
    internal string? LinkRemoteTitle(HexDocument shown) =>
        shown.Path is { } path && remoteTabs?.TryGetValue(path, out LinkRemoteTab? tab) == true ? $"{Path.GetFileName(path)} ({tab.DeviceName})" : null;

    /// <summary>The tooltip of a remote tab, where the file is on which machine; null for any other tab.</summary>
    internal string? LinkRemoteTooltip(HexDocument shown) =>
        shown.Path is { } path && remoteTabs?.TryGetValue(path, out LinkRemoteTab? tab) == true
            ? Said(tab.CanEdit ? "link.remote_tip" : "link.remote_tip_read_only", ("path", tab.Wire), ("device", tab.DeviceName))
            : null;

    // A remote tab saved: the copy sent back with the hash it had there
    private void LinkRemoteSaved(HexDocument saved)
    {
        if (saved.Path is not { } path || remoteTabs?.TryGetValue(path, out LinkRemoteTab? tab) != true)
            return;
        if (!tab.CanEdit)
        {
            Say(Said("link.remote_not_allowed", ("device", tab.DeviceName)), error: true);
            return;
        }

        LinkSendRemote(tab, tab.Base);
    }

    // The copy as it is on the disk sent over the device's file, if it's still at the base given
    private void LinkSendRemote(LinkRemoteTab tab, string? baseHash)
    {
        if (link?.PeerOf(tab.DeviceId) is not { } peer)
        {
            Say(Said("link.remote_not_sent", ("path", tab.Wire), ("device", tab.DeviceName), ("why", Said("link.sync_not_linked", ("device", tab.DeviceName)))), error: true);
            return;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(tab.Copy);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say(e.Message, error: true);
            return;
        }

        tab.Sending = true;
        tab.Card?.Close();
        _ = Task.Run(async () =>
        {
            try
            {
                string hash = await peer.Write(tab.Wire, baseHash, bytes);
                PostLink(() =>
                {
                    tab.Sending = false;
                    tab.Base = hash;
                    tab.MovedThere = null;
                    Say(Said("link.remote_saved", ("path", tab.Wire), ("device", tab.DeviceName)));
                    LinkRemoteSent?.Invoke(tab.Wire, null);
                });
            }
            catch (HexLinkException e) when (e.Code == HexLinkErrors.CONFLICT)
            {
                string? now = HexLinkPeer.ConflictHash(e);
                PostLink(() =>
                {
                    tab.Sending = false;
                    LinkRemoteConflict(tab, now);
                    LinkRemoteSent?.Invoke(tab.Wire, "conflict");
                });
            }
            catch (Exception e) when (e is HexLinkException or IOException)
            {
                PostLink(() =>
                {
                    tab.Sending = false;
                    string why = e is HexLinkException { Code: HexLinkErrors.NOT_ALLOWED } ? Said("link.remote_not_allowed", ("device", tab.DeviceName)) : e.Message;
                    Say(Said("link.remote_not_sent", ("path", tab.Wire), ("device", tab.DeviceName), ("why", why)), error: true);
                    LinkRemoteSent?.Invoke(tab.Wire, why);
                });
            }
        });
    }

    /// <summary>A remote tab's save answered, its wire path and null when it went, "conflict" or why when it didn't; for a self test to see.</summary>
    internal Action<string, string?>? LinkRemoteSent { get; set; }

    // The file moved there since it was read: the three ways out, on a card that stays
    private void LinkRemoteConflict(LinkRemoteTab tab, string? now)
    {
        tab.Card?.Close();
        tab.Card = toasts.Show(Said("link.remote_conflict", ("path", tab.Wire), ("device", tab.DeviceName)), "warning", null, 0.0f,
            (Said("link.sync_open_difference"), () => LinkRemoteDifference(tab)),
            (Said("link.remote_take_theirs"), () =>
            {
                tab.Card?.Close();
                LinkRemoteTakeTheirs(tab);
            }),
            (Said("link.remote_overwrite"), () =>
            {
                tab.Card?.Close();
                LinkSendRemote(tab, now);
            }));
    }

    /// <summary>A remote tab's conflict settled the way its card's buttons do, for the Link window and a self test: "theirs", "overwrite" or "difference".</summary>
    internal bool LinkRemoteSettle(string copy, string how, string? now = null)
    {
        if (!linkRemote.TryGetValue(copy, out LinkRemoteTab? tab))
            return false;
        switch (how)
        {
            case "theirs":
                tab.Card?.Close();
                LinkRemoteTakeTheirs(tab);
                return true;
            case "overwrite":
                tab.Card?.Close();
                _ = Task.Run(async () =>
                {
                    // The hash it has there now, asked again so the overwrite is of what's there
                    string? there = now;
                    if (there is null && link?.PeerOf(tab.DeviceId) is { } peer)
                        there = (await peer.Read(tab.Wire)).Hash;
                    PostLink(() => LinkSendRemote(tab, there));
                });
                return true;
            case "difference":
                LinkRemoteDifference(tab);
                return true;
            default:
                return false;
        }
    }

    // What the device has now fetched beside the copy and opened against it in Hex's diff, theirs taken out and mine in
    private void LinkRemoteDifference(LinkRemoteTab tab)
    {
        if (link?.PeerOf(tab.DeviceId) is not { } peer)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                HexLinkFile theirs = await peer.Read(tab.Wire);
                PostLink(() =>
                {
                    string mine = Context.Documents.Find(tab.Copy) is { } open ? open.Editor.Text : File.ReadAllText(tab.Copy);
                    OpenDiff(tab.Copy, "link-remote", Said("link.sync_difference_title", ("name", Path.GetFileName(tab.Copy)), ("device", tab.DeviceName)),
                        GitDiffText.Of(theirs.Text ?? string.Empty, mine), -1);
                });
            }
            catch (HexLinkException e)
            {
                PostLink(() => Say(e.Message, error: true));
            }
        });
    }

    // What the device has now read into the copy and the tab, what was typed a step back in its undo
    private void LinkRemoteTakeTheirs(LinkRemoteTab tab)
    {
        if (link?.PeerOf(tab.DeviceId) is not { } peer)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                HexLinkFile theirs = await peer.Read(tab.Wire);
                await Task.Run(() => HexLinkFiles.WriteAtomic(tab.Copy, theirs.Bytes));
                PostLink(() =>
                {
                    tab.Base = theirs.Hash;
                    tab.MovedThere = null;
                    if (Context.Documents.Find(tab.Copy) is { } open && theirs.Text is { } text)
                        TakeFromDisk(open, File.ReadAllText(tab.Copy));
                    Say(Said("link.remote_took_theirs", ("path", tab.Wire), ("device", tab.DeviceName)));
                    linkDirty = true;
                });
            }
            catch (HexLinkException e)
            {
                PostLink(() => Say(e.Message, error: true));
            }
        });
    }

    // A watched file moved on the device: an unchanged tab takes it, a changed one says so
    private void LinkRemoteMoved(HexLinkPeer peer, string wire, string? hash)
    {
        LinkRemoteTab? tab = linkRemote.Values.FirstOrDefault(each => each.DeviceId == peer.Device.Id && each.Wire == wire);
        if (tab is null || tab.Sending || hash == tab.Base)
            return;
        if (hash is null)
        {
            tab.MovedThere = "gone";
            Say(Said("link.remote_gone", ("path", wire), ("device", tab.DeviceName)));
            linkDirty = true;
            return;
        }

        if (Context.Documents.Find(tab.Copy) is { } open && HasChanges(open))
        {
            tab.MovedThere = hash;
            Say(Said("link.remote_moved_changed", ("path", wire), ("device", tab.DeviceName)));
            linkDirty = true;
            return;
        }

        LinkRemoteTakeTheirs(tab);
    }

    // A remote tab closed: no longer watched there, its copy left for next time
    private void LinkRemoteClosed(HexDocument closed)
    {
        if (closed.Path is not { } path || remoteTabs?.Remove(path, out LinkRemoteTab? tab) != true)
            return;
        tab.Card?.Close();
        if (link?.PeerOf(tab.DeviceId) is { } peer && !linkRemote.Values.Any(each => each.DeviceId == tab.DeviceId && each.Wire == tab.Wire))
            _ = Task.Run(async () =>
            {
                try
                {
                    await peer.Unwatch(tab.Wire);
                }
                catch (HexLinkException)
                {
                }
            });
        linkDirty = true;
    }

    // A device linked: its watched files heard from, its remote tabs watched again and caught up
    private void LinkFilesJoined(HexLinkPeer peer)
    {
        peer.FileChanged = (wire, hash) => PostLink(() => LinkRemoteMoved(peer, wire, hash));
        foreach (LinkRemoteTab tab in linkRemote.Values.Where(each => each.DeviceId == peer.Device.Id).ToList())
        {
            tab.CanEdit = HexLinkAllowed.Has(peer.Granted, HexLinkRights.Edit);
            if (Context.Documents.Find(tab.Copy) is { IsText: true } open)
                open.Editor.ReadOnly = !tab.CanEdit;
            _ = Task.Run(async () =>
            {
                try
                {
                    string? now = await peer.Watch(tab.Wire);
                    PostLink(() => LinkRemoteMoved(peer, tab.Wire, now));
                }
                catch (HexLinkException)
                {
                }
            });
        }
    }

    // Link going down: the remote tabs stay open, their copies on the disk, unwatched until their device links again
    private void LinkFilesStopAll()
    {
        foreach (LinkRemoteTab tab in linkRemote.Values)
        {
            tab.Card?.Close();
            tab.Card = null;
        }
    }

    /* Picking a file of another machine's */

    // A device's workspace browsed a folder at a time, or searched, the file picked opened
    internal static string? LinkSyncedLocalFile(HexLinkFolder synced, IReadOnlyList<HexLinkFolder> current, string wire)
    {
        // Root labels can be reassigned when same-basename workspaces are opened or reordered.
        if (!current.Any(folder => PlatformPaths.Same(folder.Path, synced.Path)))
            return null;

        try
        {
            string local = HexLinkFiles.Resolve([synced], wire);
            return File.Exists(local) ? local : null;
        }
        catch (HexLinkException)
        {
            return null;
        }
    }

    internal static bool LinkHasRemoteSymbols(HexLinkPeer peer) =>
        HexLinkAllowed.Has(peer.Granted, HexLinkRights.View) && peer.Tools.Any(tool => tool.Name == "hex_symbols");

    private void LinkOpenRemoteCommand(HexLinkPeer peer) => LinkBrowseRemote(peer, string.Empty);

    private void LinkBrowseRemote(HexLinkPeer peer, string path) => _ = Task.Run(async () =>
    {
        try
        {
            IReadOnlyList<HexLinkEntry> entries = await peer.List(path);
            // A newly paired peer can be browsed before its background tool discovery finishes.
            if (path.Length == 0 && peer.Tools.Count == 0)
                await peer.ListTools();
            PostLink(() =>
            {
                var choices = new List<(string Text, Action Picked)>();
                if (path.Length == 0 && peer.Tools.Any(tool => tool.Name == "hex_files"))
                    choices.Add((Said("link.remote_search"), () => LinkSearchRemote(peer)));
                if (path.Length == 0 && LinkHasRemoteSymbols(peer))
                    choices.Add((Said("link.remote_go_to_symbol"), () => LinkSearchRemoteSymbols(peer)));
                if (path.Length > 0)
                    choices.Add(("..", () => LinkBrowseRemote(peer, path.Contains('/') ? path[..path.LastIndexOf('/')] : string.Empty)));
                foreach (HexLinkEntry entry in entries)
                {
                    string wire = path.Length == 0 ? entry.Name : $"{path}/{entry.Name}";
                    choices.Add(entry.Folder
                        ? ($"{entry.Name}/", () => LinkBrowseRemote(peer, wire))
                        : (entry.Name, () => LinkOpenRemoteSaying(peer.Device.Id, wire)));
                }

                ChooseFrom(Said("link.remote_title", ("device", peer.Device.Name)), path.Length == 0 ? Said("link.remote_text") : path, choices);
            });
        }
        catch (HexLinkException e)
        {
            PostLink(() => Say(e.Message, error: true));
        }
    });

    // The device's files found by part of their name (its hex_files), the one picked opened
    private void LinkSearchRemote(HexLinkPeer peer) => AskText(Said("link.remote_search_title", ("device", peer.Device.Name)), Said("link.remote_search_text"), string.Empty, Said("link.remote_search_button"), typed =>
    {
        _ = Task.Run(async () =>
        {
            try
            {
                McpToolResult found = await peer.Call("hex_files", new JsonObject { ["query"] = typed, ["most"] = 40 });
                List<string> wires = LinkFilesFound(peer, found.AllText());
                PostLink(() => ChooseFrom(Said("link.remote_title", ("device", peer.Device.Name)), typed,
                    [.. wires.Select(wire => (wire, (Action)(() => LinkOpenRemoteSaying(peer.Device.Id, wire))))]));
            }
            catch (HexLinkException e)
            {
                PostLink(() => Say(e.Message, error: true));
            }
        });
    });

    /// <summary>
    /// The files a device's hex_files answer names, as wire paths: a path that starts with one of its folders' names is
    /// one already, any other is taken as its first folder's.
    /// </summary>
    internal static List<string> LinkFilesFound(HexLinkPeer peer, string said) => LinkFilesFound([.. peer.Workspace.Folders.Select(folder => folder.Name)], said);

    internal static List<string> LinkFilesFound(IReadOnlyList<string> folders, string said)
    {
        var wires = new List<string>();
        foreach (string line in said.Split('\n'))
        {
            Match file = Regex.Match(line.TrimEnd('\r'), @"^(\S.*?)\s{2,}\d+ lines?$");
            if (!file.Success)
                continue;
            string path = file.Groups[1].Value.Replace('\\', '/');
            string first = path.Contains('/') ? path[..path.IndexOf('/')] : path;
            if (folders.Count > 1 && folders.Contains(first))
                wires.Add(path);
            else if (folders.Count > 0)
                wires.Add($"{folders[0]}/{path}");
        }

        return wires;
    }

    private sealed record LinkSymbol(string Label, string Wire, int Line);

    // Display paths can coincide when workspace roots have the same basename. Route from the indexed
    // file on the host, where Link's unique folder names and the actual roots are both available.
    private HexAnswer LinkSymbolLocations(HexCodeIndex index, JsonObject args) =>
        LinkSymbolLocations(index.VisibleSnapshot(), linkServices?.Workspace.Folders ?? [], args);

    internal static HexAnswer LinkSymbolLocations(HexIndexSnapshot index, IReadOnlyList<HexLinkFolder> folders, JsonObject args)
    {
        var locations = new JsonArray();
        string query = args.String("query") ?? string.Empty;
        if (query.Trim().Length == 0)
            return HexAnswer.Wrong("Say a name, or part of one, in query.");
        int most = Math.Clamp(args.Integer("most") ?? 25, 1, 500);
        foreach (var (file, symbol, _) in HexCodeIndex.FindSymbols(index, query, args.String("kind"), args.String("path")))
        {
            var folder = folders.FirstOrDefault(root => PlatformPaths.IsUnder(file.Path, root.Path));
            if (folder is null)
                continue;
            string relative = Path.GetRelativePath(folder.Path, file.Path).Replace(Path.DirectorySeparatorChar, '/');
            string wire = folder.Name + "/" + relative;
            try
            {
                HexLinkFiles.Resolve(folders, wire);
            }
            catch (HexLinkException)
            {
                continue;
            }
            locations.Add((JsonNode)new JsonObject { ["wire"] = wire, ["line"] = symbol.Line, ["label"] = symbol.Kind + " " + symbol.Full });
            if (locations.Count >= most)
                break;
        }
        return new HexAnswer(locations.ToJsonString(), 0);
    }

    private static async Task<List<LinkSymbol>> LinkFindRemoteSymbols(HexLinkPeer peer, string query)
    {
        bool structured = peer.Tools.FirstOrDefault(tool => tool.Name == "hex_symbols")?.InputSchema["properties"]?["link_locations"] is not null;
        var args = new JsonObject { ["query"] = query, ["most"] = 40 };
        if (structured)
            args["link_locations"] = true;
        McpToolResult found = await peer.Call("hex_symbols", args);
        if (found.IsError)
            throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, found.AllText());

        var symbols = new List<LinkSymbol>();
        string[] folders = [.. peer.Workspace.Folders.Select(folder => folder.Name)];
        if (structured)
        {
            JsonArray locations;
            try
            {
                locations = JsonNode.Parse(found.AllText()) as JsonArray ?? throw HexLinkException.Invalid("Expected an array of symbol locations.");
            }
            catch (System.Text.Json.JsonException)
            {
                throw HexLinkException.Invalid("Invalid symbol locations.");
            }
            foreach (JsonObject item in locations.Take(40).OfType<JsonObject>())
            {
                string? wire = HexLinkData.String(item, "wire", HexLinkFiles.MOST_PATH + 1);
                string? label = HexLinkData.String(item, "label", 512);
                if (wire is null || wire.Length > HexLinkFiles.MOST_PATH || label is null || HexLinkData.Int(item, "line") is not int line || line < 0 || line == int.MaxValue)
                    continue;
                int slash = wire.IndexOf('/');
                if (slash > 0 && folders.Contains(wire[..slash]))
                    symbols.Add(new(label, wire, line));
            }
            return symbols;
        }

        foreach (string row in found.AllText().Split('\n'))
        {
            Match match = Regex.Match(row.TrimEnd('\r'), @"^([^:/\\][^:]*):([0-9]+) (\S+ .+?)(?:  .*)?$");
            if (!match.Success || !int.TryParse(match.Groups[2].Value, out int line) || line < 1)
                continue;
            string path = match.Groups[1].Value.Replace('\\', '/');
            int slash = path.IndexOf('/');
            if (folders.Length == 1)
                symbols.Add(new(match.Groups[3].Value, folders[0] + "/" + path, line - 1));
            else if (slash > 0 && folders.Contains(path[..slash]) && !folders.Contains(path[..slash] + "-2", StringComparer.OrdinalIgnoreCase))
                symbols.Add(new(match.Groups[3].Value, path, line - 1));
        }
        return symbols;
    }

    private void LinkSearchRemoteSymbols(HexLinkPeer peer)
    {
        string title = Said("link.remote_symbol_title", ("device", peer.Device.Name));
        AskText(title, Said("link.remote_symbol_text"), string.Empty, Said("link.remote_search_button"), typed =>
        {
            string query = typed.Trim();
            if (query.Length == 0)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    List<LinkSymbol> symbols = await LinkFindRemoteSymbols(peer, query);
                    List<(string Text, Action Picked)> choices = [.. symbols.Select(symbol =>
                        ($"{symbol.Label}   {symbol.Wire}:{symbol.Line + 1}", (Action)(() => LinkOpenRemoteSaying(peer.Device.Id, symbol.Wire, symbol.Line))))];

                    PostLink(() =>
                    {
                        if (choices.Count == 0)
                            Say(Said("link.remote_symbol_empty", ("query", query)));
                        else
                            ChooseFrom(title, query, choices);
                    });
                }
                catch (HexLinkException e)
                {
                    PostLink(() => Say(e.Message, error: true));
                }
            });
        });
    }

    private void LinkOpenRemoteSaying(string deviceId, string wire, int? line = null) => _ = Task.Run(async () =>
    {
        if (await LinkOpenRemote(deviceId, wire, line) is { } why)
            PostLink(() => Say(why, error: true));
    });
}

/// <summary>A remote tab as the Link window lists it: whose, its wire path, its copy here, whether it may be saved back and whether it moved there since.</summary>
internal sealed record LinkRemoteShown(string DeviceId, string DeviceName, string Wire, string Copy, bool CanEdit, bool MovedThere);
