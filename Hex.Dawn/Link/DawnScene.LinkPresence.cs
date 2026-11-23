using System.Numerics;

using Horizon.UI.Components;

namespace Hex.Dawn;

// Where each person is (docs/hex-link-plan.md, 5.3). This machine's caret, the file as folder-name/relative/path with
// the line, column and selection, is told to every linked device that lets this one see there, when it moves and at
// most twice a second (HexLinkPeer coalesces it). Nothing is sent while it stays put and nothing at all while no device
// is linked. The others' (the wiring keeps the latest of each in LinkPresence) are drawn in a file this machine has open
// too, mapped by the workspace-relative path: their caret a mark in the device's colour, their selection lit faintly,
// and their name beside the caret. Follow opens what a device's person has open and keeps the caret on theirs
// until the person here types or moves it.
internal sealed partial class DawnScene
{
    // What was last sent, so a tick that moved nothing sends nothing
    private string linkPresenceSent = string.Empty;
    private long linkPresenceSampled;

    // What was last drawn, so marks are only made again when something of them moved
    private string linkPresenceDrawnKey = string.Empty;

    // The marks drawn into each tab, so they're taken out again without touching anybody else's
    private HashSet<HexDocument> linkPresenceDrawn { get => LazyInitializer.EnsureInitialized(ref field, static () => []); }

    // The device followed, where the caret was put for it and the version of the text then, to tell the person moving off
    private string? linkFollowing;
    private HexDocument? linkFollowDocument;
    private UITextPosition linkFollowCaret;
    private int linkFollowVersion;
    private string linkFollowSeen = string.Empty;

    // The colours devices are drawn in, one picked by a device's id
    private static readonly Vector4[] LINK_PRESENCE_COLOURS =
    [
        new(0.93f, 0.55f, 0.20f, 1.0f), new(0.35f, 0.70f, 0.95f, 1.0f), new(0.75f, 0.45f, 0.95f, 1.0f),
        new(0.35f, 0.85f, 0.55f, 1.0f), new(0.95f, 0.40f, 0.55f, 1.0f), new(0.95f, 0.85f, 0.30f, 1.0f)
    ];

    // The colours devices picked for themselves, by id, this one's included; one that picked none is drawn in its id's
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Vector4> linkPickedColours = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A #rrggbb colour as a colour, null when it isn't one.</summary>
    internal static Vector4? LinkColourOf(string? said) =>
        said is { Length: 7 } && said[0] == '#' && uint.TryParse(said.AsSpan(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint rgb)
            ? new Vector4(((rgb >> 16) & 0xFF) / 255.0f, ((rgb >> 8) & 0xFF) / 255.0f, (rgb & 0xFF) / 255.0f, 1.0f) : null;

    /// <summary>Takes the colour a device picked for itself, or lets go of it for null, so it's drawn in it everywhere.</summary>
    internal static void LinkPickColour(string deviceId, string? said)
    {
        if (deviceId.Length == 0)
            return;
        if (LinkColourOf(said) is { } colour)
            linkPickedColours[deviceId] = colour;
        else
            linkPickedColours.TryRemove(deviceId, out _);
    }

    /// <summary>
    /// The colour a device is drawn in: the one it picked for itself (only it can, in its own Devices), otherwise the same
    /// one every time from its id. Its caret, the dot beside the files it's editing, its badges and its Link Chat lines.
    /// </summary>
    internal static Vector4 LinkDeviceColour(string deviceId)
    {
        if (linkPickedColours.TryGetValue(deviceId, out Vector4 picked))
            return picked;
        uint hash = 2166136261;
        foreach (char c in deviceId)
            hash = (hash ^ c) * 16777619;
        return LINK_PRESENCE_COLOURS[hash % (uint)LINK_PRESENCE_COLOURS.Length];
    }

    /// <summary>The device whose person is followed, null when none is.</summary>
    internal string? LinkFollowing => linkFollowing;

    /// <summary>
    /// Follows a device's person: what they have open opened here (the local file when the folder is synced or the same
    /// folder is open here, otherwise their file over the link) and the caret kept on theirs until the person here types
    /// or moves it. Following the one followed already stops.
    /// </summary>
    internal void LinkFollow(string deviceId, string? shownName = null)
    {
        if (linkFollowing == deviceId)
        {
            LinkUnfollow(said: true);
            return;
        }

        linkFollowing = deviceId;
        linkFollowDocument = null;
        linkFollowSeen = string.Empty;
        string name = shownName ?? LinkPeers.FirstOrDefault(peer => peer.Device.Id == deviceId)?.Device.Name ?? deviceId;
        string status = shownName is not null ? "link.devices_session_following"
            : linkPresence.TryGetValue(deviceId, out LinkPresenceSeen? seen) && seen.Presence is not null ? "link.following" : "link.following_nowhere";
        Say(Said(status, ("device", name)));
        linkDirty = true;
    }

    /// <summary>
    /// Jumps once to where a device's person is (the palette's Spy): their file opened here the way Follow opens it and
    /// the caret put on theirs, then left alone, nothing followed after. Stops following anybody else first.
    /// </summary>
    internal void LinkSpy(string deviceId)
    {
        string name = LinkPeers.FirstOrDefault(peer => peer.Device.Id == deviceId)?.Device.Name ?? deviceId;
        if (!linkPresence.TryGetValue(deviceId, out LinkPresenceSeen? seen) || seen.Presence?.File is null)
        {
            Say(Said("link.spy_nowhere", ("device", name)));
            return;
        }

        linkFollowing = deviceId;
        linkFollowDocument = null;
        linkFollowSeen = string.Empty;
        linkSpying = true;
        linkDirty = true;
        LinkFollowUp();
    }

    // Whether the one followed is only being jumped to once (Spy), let go of the moment the caret is on theirs
    private bool linkSpying;

    // A Spy's jump done: the caret is on theirs, so it's let go of with a word rather than followed
    private bool LinkSpyLanded()
    {
        if (!linkSpying)
            return false;
        string name = LinkPeers.FirstOrDefault(peer => peer.Device.Id == linkFollowing)?.Device.Name ?? linkFollowing ?? string.Empty;
        linkSpying = false;
        linkFollowing = null;
        linkFollowDocument = null;
        linkDirty = true;
        Say(Said("link.spied", ("device", name)));
        return true;
    }

    /// <summary>Stops following.</summary>
    internal void LinkUnfollow(bool said = false)
    {
        linkSpying = false;
        if (linkFollowing is null)
            return;
        if (said)
            Say(Said("link.unfollowed"));
        linkFollowing = null;
        linkFollowDocument = null;
        linkDirty = true;
    }

    /// <summary>
    /// Each tick while a device is linked: this machine's caret told when it moved, the others' drawn when theirs did,
    /// and the one followed kept up with. Next to nothing when nothing moved.
    /// </summary>
    private void UpdateLinkPresence()
    {
        IReadOnlyList<HexLinkPeer> peers = LinkPeers;
        if (peers.Count == 0)
        {
            if (linkPresenceDrawn.Count > 0)
                LinkPresenceClear();
            linkPresenceSent = string.Empty;
            return;
        }

        LinkPresenceSend(peers);
        LinkPresenceDraw();
        LinkFollowUp();
    }

    // This machine's caret, when it moved, to the devices that may see here
    private void LinkPresenceSend(IReadOnlyList<HexLinkPeer> peers)
    {
        long now = Environment.TickCount64;
        if (linkPresenceSent.Length != 0 && now - linkPresenceSampled < 250)
            return;
        linkPresenceSampled = now;
        HexLinkPresence? mine = LinkPresenceHere();
        if (mine is not null && !mine.SharedDocument && document.Editor.PointerTextPosition is { } pointer)
            mine = mine with { PointerLine = pointer.Line, PointerColumn = pointer.Column };
        string key = mine is null ? "-" : $"{mine.File}:{mine.Line}:{mine.Column}:{mine.ToLine}:{mine.ToColumn}:{mine.PointerLine}:{mine.PointerColumn}:{mine.SharedDocument}";
        if (key == linkPresenceSent)
            return;
        linkPresenceSent = key;
        foreach (HexLinkPeer peer in peers)
        {
            if (HexLinkAllowed.Has(peer.Granted, HexLinkRights.View))
                peer.SetPresence(mine);
        }
    }

    /// <summary>Where the caret is here as the link says it, null when the tab on screen isn't a file of the workspace (or another machine's).</summary>
    internal HexLinkPresence? LinkPresenceHere()
    {
        if (document is not { IsText: true, Path: { } path, DiffOf: null } || LinkWireOf(path) is not { } wire)
            return null;
        if (LinkLiveOf(document) is { Offline: false, Draft: null })
            return new HexLinkPresence(wire, 0, 0, SharedDocument: true);
        UITextPosition caret = document.Editor.Document.Caret, anchor = document.Editor.Document.Anchor;
        return caret == anchor
            ? new HexLinkPresence(wire, caret.Line, caret.Column, null, null)
            : new HexLinkPresence(wire, caret.Line, caret.Column, anchor.Line, anchor.Column);
    }

    /// <summary>A file here as a wire path, folder-name/relative/path: of a workspace folder, or the file of a remote tab; null otherwise.</summary>
    internal string? LinkWireOf(string path)
    {
        foreach (var live in linkLiveDocuments.Values)
            if (live.Tab.Path is { } livePath && PlatformPaths.Same(livePath, path))
                return live.Wire;
        if (linkDocument is { } shared && shared.Tab.Path is { } sharedPath && PlatformPaths.Same(sharedPath, path))
            return shared.Base.Path;
        if (linkRemote.TryGetValue(path, out LinkRemoteTab? remote))
            return remote.Wire;
        foreach (HexLinkFolder folder in linkServices?.Workspace.Folders ?? [])
        {
            if (PlatformPaths.IsUnder(path, folder.Path))
                return folder.Name + "/" + Path.GetRelativePath(folder.Path, path).Replace(Path.DirectorySeparatorChar, '/');
        }

        return null;
    }

    /// <summary>Where a device's wire path is open here, the local file of a workspace folder of that name or the copy of a remote tab; null when it isn't.</summary>
    private HexDocument? LinkDocumentOf(string deviceId, string wire)
    {
        if (linkDocument is { } shared && shared.Base.Path == wire && (shared.Peer is null || shared.Peer.Device.Id == deviceId))
            return shared.Tab;
        foreach (LinkRemoteTab tab in linkRemote.Values)
        {
            if (tab.DeviceId == deviceId && tab.Wire == wire && Context.Documents.Find(tab.Copy) is { } copy)
                return copy;
        }

        return LinkLocalOf(wire) is { } local ? Context.Documents.Find(local) : null;
    }

    // A wire path as a file of this workspace, by the folder's name; null when there's no such folder or it isn't a path
    private string? LinkLocalOf(string wire)
    {
        try
        {
            return linkServices?.Workspace.Folders is { Count: > 0 } folders ? HexLinkFiles.Resolve(folders, wire) : null;
        }
        catch (HexLinkException)
        {
            return null;
        }
    }

    // The others' carets drawn into the tabs that have their files, when something of them moved
    private void LinkPresenceDraw()
    {
        string key = string.Join("|", linkPresence.Values.Select(seen => $"{seen.Device.Id}:{seen.Presence?.File}:{seen.Presence?.Line}:{seen.Presence?.Column}:{seen.Presence?.ToLine}:{seen.Presence?.ToColumn}:{seen.Presence?.PointerLine}:{seen.Presence?.PointerColumn}:{seen.Presence?.SharedDocument}:{seen.Device.Name}"))
            + "#" + documents.Count;
        if (key == linkPresenceDrawnKey && linkPresenceDrawn.All(documents.Contains))
            return;
        linkPresenceDrawnKey = key;
        LinkPresenceClear();

        var carets = new Dictionary<HexDocument, List<UICodeRemoteCaret>>();
        foreach (LinkPresenceSeen seen in linkPresence.Values)
        {
            if (seen.Presence is not { File: not null, SharedDocument: false } where ||
                LinkDocumentOf(seen.Device.Id, where.File) is not { IsText: true } open)
                continue;
            if (LinkLiveOf(open) is { Offline: false, Draft: null })
                continue;
            if (!carets.TryGetValue(open, out var participants))
                carets[open] = participants = [];
            participants.Add(LinkPresenceCaret(seen.Device, where));
        }

        foreach (var (open, participants) in carets)
        {
            open.Editor.RemoteCarets = participants;
            linkPresenceDrawn.Add(open);
        }
    }

    private static UICodeRemoteCaret LinkPresenceCaret(HexLinkDevice device, HexLinkPresence where) =>
        new(device.Id, device.Name, where.Line, where.Column, where.ToLine ?? where.Line, where.ToColumn ?? where.Column,
            LinkDeviceColour(device.Id), where.PointerLine, where.PointerColumn);

    private IReadOnlyList<UICodeRemoteCaret> LinkCaretsForShared(LinkLiveDocument shared, IReadOnlyList<UICodeRemoteCaret> scoped)
    {
        List<UICodeRemoteCaret>? combined = null;
        foreach (var seen in linkPresence.Values)
        {
            if (seen.Presence is not { File: { } file, SharedDocument: false } where ||
                !PlatformPaths.Comparer.Equals(file, shared.Wire) || scoped.Any(caret => caret.Id == seen.Device.Id))
                continue;
            combined ??= [.. scoped];
            combined.Add(LinkPresenceCaret(seen.Device, where));
        }
        return combined ?? scoped;
    }

    private void LinkPresenceClear()
    {
        foreach (HexDocument open in linkPresenceDrawn)
            if (LinkLiveOf(open) is not { Offline: false, Draft: null })
                open.Editor.RemoteCarets = [];
        linkPresenceDrawn.Clear();
    }

    // The person followed kept up with: their file opened, the caret put on theirs; let go of when the person here
    // types or moves the caret themselves
    private void LinkFollowUp()
    {
        if (linkFollowing is not { } id)
            return;
        if (linkFollowDocument is { } followed && documents.Contains(followed) && document == followed
            && (followed.Editor.Document.Version != linkFollowVersion || followed.Editor.Document.Caret != linkFollowCaret))
        {
            LinkUnfollow(said: true);
            return;
        }

        linkPresence.TryGetValue(id, out LinkPresenceSeen? seen);
        var where = seen?.Presence;
        foreach (var shared in linkLiveDocuments.Values)
        {
            if (shared is { Offline: true } or { Draft: not null } ||
                where?.File is { } activeFile && !PlatformPaths.Comparer.Equals(activeFile, shared.Wire))
                continue;
            var caret = shared.Tab.Editor.RemoteCarets.FirstOrDefault(remote => remote.Id == id);
            if (caret.Id is null)
                continue;

            string liveKey = $"room:{shared.Wire}:{caret.Line}:{caret.Column}";
            if (liveKey == linkFollowSeen)
                return;
            linkFollowSeen = liveKey;
            if (document != shared.Tab)
                Show(shared.Tab);
            shared.Tab.Editor.GoTo(caret.Line, caret.Column);
            linkFollowDocument = shared.Tab;
            linkFollowCaret = shared.Tab.Editor.Document.Caret;
            linkFollowVersion = shared.Tab.Editor.Document.Version;
            LinkSpyLanded();
            return;
        }

        if (where?.File is not { } file)
            return;
        string key = where.SharedDocument ? $"opening-room:{file}" : $"{file}:{where.Line}:{where.Column}";
        if (key == linkFollowSeen)
            return;
        linkFollowSeen = key;

        HexDocument? open = LinkDocumentOf(id, where.File);
        if (open is null && LinkLocalOf(where.File) is { } local && File.Exists(local) && Open(local))
            open = document;
        if (open is null)
        {
            // Not here, their file over the link; the caret's put on theirs the next time it moves, or now when it's open by then
            linkFollowSeen = string.Empty;
            if (link?.PeerOf(id) is not null)
            {
                string wire = where.File;
                linkFollowSeen = "opening:" + key;
                _ = Task.Run(async () =>
                {
                    if (await LinkOpenRemote(id, wire) is { } why)
                        PostLink(() => Say(why, error: true));
                    PostLink(() => linkFollowSeen = string.Empty);
                });
            }

            return;
        }

        if (document != open)
            Show(open);
        if (!where.SharedDocument)
            open.Editor.GoTo(where.Line, where.Column);
        linkFollowDocument = open;
        linkFollowCaret = open.Editor.Document.Caret;
        linkFollowVersion = open.Editor.Document.Version;
        // A shared document's caret is found in its room on the next tick, so a Spy waits for that one
        if (!where.SharedDocument)
            LinkSpyLanded();
    }

    // A device linked: it's told where this machine's person is at once, the next tick
    private void LinkPresenceJoined(HexLinkPeer peer)
    {
        linkPresenceSent = string.Empty;
        linkPresenceDrawnKey = string.Empty;
    }

    // Link going down: the marks taken out, following stopped
    private void LinkPresenceStopAll()
    {
        LinkPresenceClear();
        linkPresenceDrawnKey = linkPresenceSent = string.Empty;
        linkSpying = false;
        linkFollowing = null;
        linkFollowDocument = null;
    }
}
