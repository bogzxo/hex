using System.Text;
using System.Text.Json.Nodes;
using Hex.Core.Git;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private const string CROSS_FILES_NOTE = "src/note.txt", CROSS_FILES_SYMBOL = "src/Navigation.cs";
    private const string CROSS_FILES_PATCH = "patch.txt", CROSS_FILES_PATCH_TITLE = "Paired files fixture";
    private const string CROSS_FILES_START = "original café\n", CROSS_FILES_SAVED = "guest save 漢\n";
    private const string CROSS_FILES_HOST = "listener's new commit 🧪\n", CROSS_FILES_DRAFT = "guest stale draft 🚀\n";
    private const string CROSS_FILES_PATCH_START = "patch base\n", CROSS_FILES_PATCHED = "patch applied on listener\n";

    private static void PrepareLinkCrossFiles(string work)
    {
        Directory.CreateDirectory(Path.Combine(work, "src"));
        File.WriteAllText(Path.Combine(work, CROSS_FILES_NOTE), CROSS_FILES_START);
        File.WriteAllText(Path.Combine(work, CROSS_FILES_SYMBOL), "// fixture\n\npublic class CrossFilesNavigation { }\n");
        File.WriteAllText(Path.Combine(work, "Decoy.cs"), "namespace Unrelated;\npublic class CrossFilesNavigation { }\n");
        File.WriteAllText(Path.Combine(work, CROSS_FILES_PATCH), CROSS_FILES_PATCH_START);
        File.WriteAllText(Path.Combine(work, ".gitattributes"), "* -text\n");
        File.WriteAllText(Path.Combine(work, ".gitignore"), "*.flag\n");
        RunLinkCrossFilesGit(work, "init", "-q", "-b", "main", "--object-format=sha1");
        RunLinkCrossFilesGit(work, "add", "--", ".");
        CommitLinkCrossFiles(work, "Original paired files fixture");
    }

    private static string RunLinkCrossFilesGit(string work, params string[] args)
    {
        var result = GitProcess.Run(work, args);
        return result.Ok ? result.Output.Trim() : throw new IOException($"Git {args[0]} failed in paired files: {result.Why}");
    }

    private static void CommitLinkCrossFiles(string work, string message) =>
        RunLinkCrossFilesGit(work, "-c", "user.name=Hex Link QA", "-c", "user.email=link-qa@example.invalid",
            "-c", "commit.gpgSign=false", "-c", "core.hooksPath=.git/no-hooks", "commit", "-qm", message);

    private void TestLinkCrossFiles(HexPointerScript test, Func<HexLinkPeer?> paired, bool guest, string work)
    {
        const string dirty = "files-dirty.flag", settled = "files-settled.flag";
        static string Wire(string name) => $"{LINK_CROSS_FOLDER}/{name}";
        string Full(string name) => Path.Combine(work, name);
        static bool BytesAre(byte[] bytes, string text) => bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(text));
        bool OnDisk(string name, string text) => File.Exists(Full(name)) && BytesAre(File.ReadAllBytes(Full(name)), text);

        void Await(string label, Func<bool> condition, float seconds = 30)
        {
            test.WaitFor(condition, seconds);
            test.Check("link-cross-files: " + label, condition);
        }

        if (!guest)
        {
            Task<string>? applied = null;
            Await("editor save arrived on the listener's disk with exact UTF-8 bytes", () => OnDisk(CROSS_FILES_NOTE, CROSS_FILES_SAVED));
            test.WaitFor(() => File.Exists(Full(dirty)), 30);
            test.Run(() =>
            {
                if (!File.Exists(Full(dirty)))
                    return;
                File.WriteAllText(Full(CROSS_FILES_NOTE), CROSS_FILES_HOST);
                RunLinkCrossFilesGit(work, "add", "--", CROSS_FILES_NOTE);
                CommitLinkCrossFiles(work, "Listener's new commit");
            });
            Await("stale guest save did not overwrite the listener's new commit", () =>
                File.Exists(Full(settled)) && OnDisk(CROSS_FILES_NOTE, CROSS_FILES_HOST));
            test.Run(() =>
            {
                if (paired() is { } peer)
                    LinkSetRights(peer.Device.Id, HexLinkRights.View);
            });
            test.WaitFor(() => LinkPatchesOffered.Any(offer => offer.Title == CROSS_FILES_PATCH_TITLE), 40);
            test.Run(() =>
            {
                var offer = LinkPatchesOffered.FirstOrDefault(offer => offer.Title == CROSS_FILES_PATCH_TITLE);
                if (offer is not null && paired()?.Device.Id == offer.Device.Id)
                    applied = Task.Run(() => LinkDecidePatch(offer, "applied"));
            });
            Await("View-only patch arrived and was applied by the listener to its disposable repository", () =>
                applied is { IsCompletedSuccessfully: true, Result: "applied" } && OnDisk(CROSS_FILES_PATCH, CROSS_FILES_PATCHED));
            Await("listener audited denied write and kept authoritative bytes", () =>
                paired() is { } peer && LinkAuditLatest(100).Any(entry => entry.Event == "refused" &&
                    entry.Device == peer.Device.Id && entry.Detail == "fs/write, it needs edit") &&
                OnDisk(CROSS_FILES_NOTE, CROSS_FILES_HOST));
            test.WaitFor(() => LinkPeers.Count == 0, 40);
            return;
        }

        string clone = Path.Combine(LinkTestFolder("hex-cross-files-", test), "clone");
        Task<bool>? listed = null, symbols = null;
        Task<HexLinkFile>? read = null;
        Task<string?>? opened = null, cloned = null;
        Task<string>? fetched = null, offered = null, denied = null;
        Task? checkpoint = null;
        HexDocument? tab = null;
        var saves = new List<string?>();
        string? repository = null, originalHead = null;

        test.Run(() =>
        {
            if (paired() is { } peer)
                listed = ListWorkspace(peer);
        });
        Await("stock fs/list returned the advertised folder, its source directory and both fixtures", () =>
            listed is { IsCompletedSuccessfully: true, Result: true });
        test.Run(() => read = paired()?.Read(Wire(CROSS_FILES_NOTE)));
        Await("stock fs/read returned original exact bytes and their hash", () =>
            read is { IsCompletedSuccessfully: true } && BytesAre(read.Result.Bytes, CROSS_FILES_START) &&
            read.Result.Hash == HexLinkFiles.Hash(read.Result.Bytes));

        test.Run(() =>
        {
            if (paired() is { } peer)
                symbols = ScopedSymbol(peer);
        });
        Await("structured scoped symbol query excluded the decoy and identified the declaration line", () =>
            symbols is { IsCompletedSuccessfully: true, Result: true }, 45);
        test.Run(() =>
        {
            if (paired() is { } peer && symbols is { IsCompletedSuccessfully: true, Result: true })
                opened = LinkOpenRemote(peer.Device.Id, Wire(CROSS_FILES_SYMBOL), line: 2);
        });
        Await("symbol opens a remote tab at its actual declaration", () =>
            opened is { IsCompletedSuccessfully: true, Result: null } && paired() is { } peer &&
            document.Path is { } file && PlatformPaths.Same(file, LinkRemoteCopyOf(peer.Device.Id, Wire(CROSS_FILES_SYMBOL))) &&
            document.Editor.Document.Caret == new UITextPosition(2, 0));

        test.WaitFor(() => paired()?.Workspace.Repositories.Any() == true, 30);
        test.Run(() =>
        {
            if (paired() is not { } peer || peer.Workspace.Repositories.SingleOrDefault() is not { } info)
                return;
            repository = info.Name;
            originalHead = info.Head;
            cloned = LinkClone(peer.Device.Id, repository, clone);
        });
        Await("Git handoff clone has the advertised HEAD, exact fixture and no configured remote", () =>
            cloned is { IsCompletedSuccessfully: true, Result: null } && originalHead is { Length: 40 } &&
            RunLinkCrossFilesGit(clone, "rev-parse", "HEAD") == originalHead &&
            RunLinkCrossFilesGit(clone, "remote").Length == 0 &&
            BytesAre(File.ReadAllBytes(Path.Combine(clone, CROSS_FILES_NOTE)), CROSS_FILES_START), 55);

        test.Run(() =>
        {
            LinkRemoteSent = (wire, result) =>
            {
                if (wire == Wire(CROSS_FILES_NOTE))
                    saves.Add(result);
            };
            opened = paired() is { } peer ? LinkOpenRemote(peer.Device.Id, Wire(CROSS_FILES_NOTE)) : null;
        });
        test.WaitFor(() => opened?.IsCompleted == true, 30);
        test.Run(() =>
        {
            if (opened is not { IsCompletedSuccessfully: true, Result: null } || document.Editor.Text != CROSS_FILES_START ||
                document.Editor.ReadOnly || paired() is not { } peer ||
                document.Path is not { } copy || !PlatformPaths.Same(copy, LinkRemoteCopyOf(peer.Device.Id, Wire(CROSS_FILES_NOTE))))
                return;
            tab = document;
            Rewrite(tab, CROSS_FILES_SAVED);
            Save();
        });
        test.WaitFor(() => saves.Count > 0, 30);
        test.Run(() => read = paired()?.Read(Wire(CROSS_FILES_NOTE)));
        Await("remote editor tab saved exact bytes, confirmed by reading from the listener", () =>
            saves.Count == 1 && saves[0] is null && read is { IsCompletedSuccessfully: true } && BytesAre(read.Result.Bytes, CROSS_FILES_SAVED));

        test.Run(() =>
        {
            if (tab is not null && paired() is { } peer)
            {
                Rewrite(tab, CROSS_FILES_DRAFT);
                checkpoint = peer.Write(Wire(dirty), null, "dirty"u8.ToArray());
                read = ReadHostChange(peer);
            }
        });
        Await("listener replacement reached the wire while the local draft stayed in the tab", () =>
            checkpoint is { IsCompletedSuccessfully: true } && read is { IsCompletedSuccessfully: true } &&
            BytesAre(read.Result.Bytes, CROSS_FILES_HOST) && tab?.Editor.Text == CROSS_FILES_DRAFT);
        test.Run(() => { if (tab is not null) Save(); });
        Await("real remote stale save returned conflict and preserved the guest draft", () =>
            saves.Count == 2 && saves[1] == "conflict" && tab?.Editor.Text == CROSS_FILES_DRAFT);
        test.Run(() =>
        {
            if (tab?.Path is { } copy)
                LinkRemoteSettle(copy, "theirs");
        });
        Await("taking theirs loads the listener's bytes and clears the editor's change state", () =>
            tab?.Editor.Text == CROSS_FILES_HOST && !HasChanges(tab));
        test.Run(() =>
        {
            if (paired() is { } peer)
                checkpoint = peer.Write(Wire(settled), null, "settled"u8.ToArray());
        });
        Await("Edit downgrade makes the remote tab read-only while keeping symbol search available", () =>
            checkpoint is { IsCompletedSuccessfully: true } && paired() is { Granted: HexLinkRights.View } peer &&
            LinkHasRemoteSymbols(peer) && tab?.Editor.ReadOnly == true);
        test.Run(() =>
        {
            if (paired() is { } peer && repository is not null && cloned is { IsCompletedSuccessfully: true, Result: null })
                fetched = LinkFetch(peer.Device.Id, clone, repository);
        });
        Await("View-only fetch advances only the peer tracking ref and delivers the new commit", () =>
        {
            if (fetched is not { IsCompletedSuccessfully: true } || paired() is not { } peer)
                return false;
            string tracking = $"refs/remotes/{HexLinkGit.Slug(peer.Device.Name)}/main";
            return RunLinkCrossFilesGit(clone, "rev-parse", tracking) != originalHead &&
                RunLinkCrossFilesGit(clone, "show", $"{tracking}:{CROSS_FILES_NOTE}") == CROSS_FILES_HOST.TrimEnd() &&
                RunLinkCrossFilesGit(clone, "rev-parse", "HEAD") == originalHead;
        }, 55);

        test.Run(() =>
        {
            if (fetched is { IsCompletedSuccessfully: true } && paired() is { } peer)
            {
                File.WriteAllText(Path.Combine(clone, CROSS_FILES_PATCH), CROSS_FILES_PATCHED);
                offered = LinkOfferChanges(peer.Device.Id, clone, CROSS_FILES_PATCH_TITLE);
            }
        });
        test.WaitFor(() => offered?.IsCompleted == true, 55);
        test.Run(() => read = paired()?.Read(Wire(CROSS_FILES_PATCH)));
        Await("listener's explicit patch approval returned applied and changed the remote bytes", () =>
            offered is { IsCompletedSuccessfully: true, Result: "applied" } &&
            read is { IsCompletedSuccessfully: true } && BytesAre(read.Result.Bytes, CROSS_FILES_PATCHED));

        test.Run(() =>
        {
            if (paired() is { } peer)
            {
                read = peer.Read(Wire(CROSS_FILES_NOTE));
                denied = peer.Write(Wire(CROSS_FILES_NOTE), HexLinkFiles.Hash(Encoding.UTF8.GetBytes(CROSS_FILES_HOST)), "denied"u8.ToArray());
            }
        });
        Await("View-only peer can still read exact bytes but cannot write with a valid base", () =>
            read is { IsCompletedSuccessfully: true } && BytesAre(read.Result.Bytes, CROSS_FILES_HOST) &&
            denied?.Exception?.GetBaseException() is HexLinkException { Code: HexLinkErrors.NOT_ALLOWED });
        test.Run(() =>
        {
            LinkRemoteSent = null;
            LinkTestWritable(clone);
        });

        static async Task<bool> ListWorkspace(HexLinkPeer peer)
        {
            var root = await peer.List(string.Empty).ConfigureAwait(false);
            var workspace = await peer.List(LINK_CROSS_FOLDER).ConfigureAwait(false);
            var source = await peer.List(Wire("src")).ConfigureAwait(false);
            return root.Any(entry => entry.Folder && entry.Name == LINK_CROSS_FOLDER) &&
                workspace.Any(entry => entry.Folder && entry.Name == "src") &&
                source.Any(entry => !entry.Folder && entry.Name == "note.txt") &&
                source.Any(entry => !entry.Folder && entry.Name == "Navigation.cs");
        }

        static async Task<bool> ScopedSymbol(HexLinkPeer peer)
        {
            var tools = await peer.ListTools().ConfigureAwait(false);
            if (tools.FirstOrDefault(tool => tool.Name == "hex_symbols")?.InputSchema["properties"]?["link_locations"] is null)
                return false;
            var result = await peer.Call("hex_symbols", new JsonObject
            {
                ["query"] = "CrossFilesNavigation", ["path"] = "src/", ["most"] = 10, ["link_locations"] = true
            }).ConfigureAwait(false);
            string text = result.AllText();
            Console.WriteLine($"[Hex self-test] link-cross-files: scoped symbols {text}");
            return !result.IsError && JsonNode.Parse(text) is JsonArray { Count: 1 } rows &&
                rows[0]?["wire"]?.GetValue<string>() == Wire(CROSS_FILES_SYMBOL) &&
                rows[0]?["line"]?.GetValue<int>() == 2;
        }

        static async Task<HexLinkFile> ReadHostChange(HexLinkPeer peer)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            while (true)
            {
                var file = await peer.Read(Wire(CROSS_FILES_NOTE), timeout.Token).ConfigureAwait(false);
                if (BytesAre(file.Bytes, CROSS_FILES_HOST))
                    return file;
                await Task.Delay(200, timeout.Token).ConfigureAwait(false);
            }
        }
    }
}
