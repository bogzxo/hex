using System.Text;
using System.Text.Json.Nodes;

using Hex.Core.Git;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Sharing the code over Hex Link between two Hexes on this machine (docs/hex-link-plan.md, phase 4). A small git
// repository is made here (A) and cloned for B, so both have the same folder of the same repository (the same first
// commit), and B is started as the link part's peer with that clone as its workspace. A pairs with it as the link part
// does, then syncs the folder: what only one side had appears on the other, a save in A's editor lands on B's disk, a
// change on B's disk shows in A's open tab, both sides changing a file at once is a conflict with nothing overwritten
// and resolves, what git ignores stays put, and hex_link_sync flush answers only once B has the bytes. Then the rest of
// sharing: a file of a folder only B has opened here, edited and saved back, a stale save offered its ways out; the
// repository cloned and fetched over the link; and with A's rights on B dropped to View the sync goes pull-only and a
// patch offered to B is applied there. B is killed in the tidy whatever happened.
internal sealed partial class DawnScene
{
    // Folders the link-files part puts in the workspace as the links describe it, without opening them in the explorer
    private string[] linkTestExtraFolders = [];

    // The repository both sides have, by its folder's name (the name a sync matches by)
    private const string LINK_SYNC_FOLDER = "link-sync-work";

    private void TestLinkFiles(HexPointerScript test)
    {
        string home = LinkTestFolder("hex-link-files-a-", test);
        string peerHome = LinkTestFolder("hex-link-files-b-", test);
        string mine = Path.Combine(LinkTestFolder("hex-link-files-repo-", test), LINK_SYNC_FOLDER);
        string theirs = Path.Combine(peerHome, LINK_SYNC_FOLDER);
        string peerWork = Path.Combine(peerHome, LINK_PEER_FOLDER);
        LinkPairingAsk? asked = null;
        HexLinkPeer? peer = null;
        Task<string?>? started = null;
        Task<McpToolResult>? flushed = null;
        string? peerId = null;
        string? why = null;

        HexLinkSync? Sync() => peerId is null ? null : LinkSyncOf(peerId, LINK_SYNC_FOLDER);
        static string Read(string file) => ReadLinkTestFile(file);

        test.Run(() =>
        {
            linkTestPeerSaid.Clear();
            why = LinkTestRepository(mine, theirs);
            if (why is not null)
            {
                Console.WriteLine($"[Hex self-test] link-files: the repositories weren't made, {why}");
                return;
            }

            LinkTestSeedSymbol(theirs);
            StopLink();
            linkTestExtraFolders = [mine];
            if (!StartLink(port: 0, home: home, loopbackOnly: true))
                return;
            linkByTest = true;
            // Keep the file sync unbound while the caret fixture opens README on both sides.
            linkLivePaused.Add($"{LINK_SYNC_FOLDER}/README.md");
            LinkOpenPairing(TimeSpan.FromMinutes(3));
            LinkStartPeer(peerHome, $"127.0.0.1:{LinkPort}", new Dictionary<string, string> { [LINK_TEST_FOLDER] = theirs, [LINK_TEST_SECONDS] = "420" });
        });
        test.Check("link-files: the same repository here and in B's home, B started on its clone", () => why is null && linkTestPeer is not null);

        test.Check("link-files: A listens on IPv4 loopback without LAN discovery", () => LinkTestBoundOnlyToLoopback);

        // Paired and linked, as the link part does it
        test.WaitFor(() => LinkPairingPending is not null || linkTestPeer is { HasExited: true }, 90.0f);
        test.Run(() =>
        {
            asked = LinkPairingPending;
            asked?.Decide(HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit);
        });
        test.WaitFor(() => LinkPeers.Count == 1 && LinkPeers[0].Workspace.Folders.Any(folder => folder.Name == LINK_SYNC_FOLDER)
            && linkServices?.Workspace.Folders.Any(folder => folder.Name == LINK_SYNC_FOLDER) == true, 40.0f);
        test.Check("link-files: B is linked with its clone open, and this workspace has the folder of the same name", () =>
        {
            peer = LinkPeers.FirstOrDefault();
            peerId = peer?.Device.Id;
            return peer is { Granted: HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit } && peer.Workspace.Folders[0].Name == LINK_SYNC_FOLDER;
        });

        TestLinkSymbolNavigation(test, () => peer);

        // Sync on: both trees compared, what only one side had copied to the other, what git ignores left where it is
        test.Run(() =>
        {
            if (peerId is not null)
                started = LinkSyncStart(peerId, LINK_SYNC_FOLDER);
        });
        test.WaitFor(() => started is { IsCompleted: true } && Sync() is { State: HexLinkSyncState.Idle }, 40.0f);
        test.Check("link-files: the sync starts and the first comparison ends idle", () =>
            started is { IsCompletedSuccessfully: true, Result: null } && Sync() is { State: HexLinkSyncState.Idle } && LinkSyncs.Count == 1);
        test.Check("link-files: a file only here is on B's disk, and one only on B is here", () =>
            Read(Path.Combine(theirs, "only-a.txt")) == "Only on A.\n" && Read(Path.Combine(mine, "only-b.txt")) == "Only on B.\n");
        test.Check("link-files: a file git ignores (bin/) didn't travel", () =>
            File.Exists(Path.Combine(mine, "bin", "built.txt")) && !File.Exists(Path.Combine(theirs, "bin", "built.txt")));
        test.Check("link-files: the choice is kept for when B links again", () =>
            LinkSyncChosen(peerId ?? string.Empty).Contains(LINK_SYNC_FOLDER));

        // A save in A's editor arrives on B's disk
        string shared = Path.Combine(mine, "src", "shared.txt"), sharedThere = Path.Combine(theirs, "src", "shared.txt");
        HexDocument? tab = null;
        test.Run(() =>
        {
            Open(shared);
            tab = document;
            Rewrite(document, "Saved in A's editor.\n");
            long notesBeforeSave = saidTotal;
            Save();
            if (Read(shared) != "Saved in A's editor.\n")
            {
                string errors = string.Join(" | ", said.TakeLast((int)Math.Min(8, saidTotal - notesBeforeSave))
                    .Where(entry => entry.Error).Select(entry => entry.Text));
                Console.WriteLine($"[Hex self-test] link-files: first save pending, read-only {document.Editor.ReadOnly}, shared session {LinkLiveOf(document) is not null}, saved text matches disk {document.DiskText is { } disk && disk.ReplaceLineEndings("\n") == Read(shared)}, errors {errors}");
            }
        });
        test.WaitFor(() => Read(sharedThere) == "Saved in A's editor.\n", 15.0f);
        test.Run(() => Console.WriteLine($"[Hex self-test] link-files: saved here {Read(shared).ReplaceLineEndings("\\n")}, there {Read(sharedThere).ReplaceLineEndings("\\n")}, {Sync()?.Summary}, conflicts {string.Join(", ", LinkConflicts.Select(each => each.Path + " " + each.Kind))}"));
        test.Check("link-files: a save in A's editor is on B's disk within a few seconds", () => Read(sharedThere) == "Saved in A's editor.\n");

        // A change on B's disk arrives here, and the clean open tab shows it
        test.Run(() => File.WriteAllText(sharedThere, "Changed on B's disk.\n"));
        test.WaitFor(() => tab?.Editor.Text == "Changed on B's disk.\n", 15.0f);
        test.Check("link-files: a file changed on B's disk is here, and its clean open tab shows it", () =>
            Read(shared) == "Changed on B's disk.\n" && tab is not null && tab.Editor.Text == "Changed on B's disk.\n" && !HasChanges(tab));

        // Both sides change one file at once: nothing overwritten, theirs beside it, and the conflict resolves
        string both = Path.Combine(mine, "README.md"), bothThere = Path.Combine(theirs, "README.md");
        LinkConflictShown? conflict = null;
        test.Run(() =>
        {
            File.WriteAllText(both, "# Changed on A\n");
            File.WriteAllText(bothThere, "# Changed on B\n");
        });
        test.WaitFor(() => LinkConflicts.Count > 0, 20.0f);
        test.Check("link-files: a file both sides changed is a conflict, theirs kept beside it and nothing overwritten", () =>
        {
            conflict = LinkConflicts.FirstOrDefault();
            string? copy = conflict?.Copy is { } wire ? Path.Combine(mine, wire[(LINK_SYNC_FOLDER.Length + 1)..].Replace('/', Path.DirectorySeparatorChar)) : null;
            return conflict is { Path: $"{LINK_SYNC_FOLDER}/README.md", Kind: "changed on both" } && copy is not null && Read(copy) == "# Changed on B\n"
                && Read(both) == "# Changed on A\n" && Read(bothThere) == "# Changed on B\n" && Path.GetFileName(copy).Contains(".theirs-", StringComparison.Ordinal);
        });
        Task<string?>? resolved = null;
        test.Run(() =>
        {
            if (peerId is not null)
                resolved = LinkResolve(peerId, $"{LINK_SYNC_FOLDER}/README.md", HexLinkSyncKeep.Mine);
        });
        test.WaitFor(() => resolved is { IsCompleted: true }, 15.0f);
        test.Check("link-files: resolving it as mine puts mine on B, the copy gone and the conflict with it", () =>
            resolved is { IsCompletedSuccessfully: true, Result: null } && Read(bothThere) == "# Changed on A\n" && LinkConflicts.Count == 0
            && !Directory.EnumerateFiles(mine).Any(file => file.Contains(".theirs-", StringComparison.Ordinal)));

        // An ignored file made while the sync runs stays here, and hex_link_sync flush answers once B has the bytes
        string flushedHere = Path.Combine(mine, "src", "flushed.txt"), flushedThere = Path.Combine(theirs, "src", "flushed.txt");
        test.Run(() =>
        {
            Directory.CreateDirectory(Path.Combine(mine, "bin"));
            File.WriteAllText(Path.Combine(mine, "bin", "later.txt"), "Built later.\n");
            File.WriteAllText(flushedHere, "Flushed over.\n");
            flushed = McpTools.Call("hex_link_sync", new JsonObject { ["device"] = LINK_PEER_NAME, ["action"] = "flush", ["folder"] = LINK_SYNC_FOLDER }, CancellationToken.None);
        });
        test.WaitFor(() => flushed is { IsCompleted: true }, 60.0f);
        test.Check("link-files: hex_link_sync flush returns, and B has the bytes when it does", () =>
        {
            bool there = Read(flushedThere) == "Flushed over.\n";
            Console.WriteLine($"[Hex self-test] link-files: flush said {(flushed is { IsCompletedSuccessfully: true } ? flushed.Result.AllText() : flushed?.Exception?.Message)}");
            return flushed is { IsCompletedSuccessfully: true } done && !done.Result.IsError && there;
        });
        test.Check("link-files: a file under bin/ made while it runs stays here", () => !File.Exists(Path.Combine(theirs, "bin", "later.txt")));
        test.Run(() => Console.WriteLine($"[Hex self-test] link-files: {Sync()?.Summary}"));

        TestLinkRemoteFiles(test, () => peer, peerWork, mine);
        TestLinkRepository(test, () => peer, mine, theirs);

        // B's person in the synced README: drawn in A's tab of it in B's colour with B's name; then followed
        string readme = Path.Combine(mine, "README.md");
        test.Run(() =>
        {
            linkPresenceDrawnKey = string.Empty;
            _ = peer?.Write($"{LINK_PEER_FOLDER}/{LINK_FLAG_PRESENCE}", null, [1]);
        });
        test.WaitFor(() => peerId is not null && LinkPresence.TryGetValue(peerId, out LinkPresenceSeen? seen) && seen.Presence?.File == $"{LINK_SYNC_FOLDER}/README.md", 20.0f);
        test.Check("link-files: B advertises its file with either a plain caret or a shared session", () =>
            peerId is not null && LinkPresence.TryGetValue(peerId, out LinkPresenceSeen? seen)
            && seen.Presence is { Line: 0, Column: 2, SharedDocument: false } or { SharedDocument: true });
        bool DrawsBInReadme() => Context.Documents.Find(readme) is { } open && peerId is not null
            && open.Editor.RemoteCarets.Any(caret => caret is { Line: 0, Column: 2, Label: LINK_PEER_NAME } && caret.Id == peerId
                && caret.Colour == LinkDeviceColour(peerId));
        test.WaitFor(DrawsBInReadme, 8);
        test.Check("link-files: B's caret is drawn in A's open tab of the file, in B's colour with its name", DrawsBInReadme);
        test.WaitFor(() => Context.Documents.Find(readme)?.Editor.RemoteCarets.Any(remote => remote.Id == peerId && remote.PointerColumn == 4) == true, 5);
        test.Check("link-files: B's mouse pointer reaches A independently of its text caret", () =>
            Context.Documents.Find(readme)?.Editor.RemoteCarets.Any(remote => remote.Id == peerId
                && remote is { Line: 0, Column: 2, PointerLine: 0, PointerColumn: 4 }) == true);
        test.Run(() => _ = peer?.Write($"{LINK_PEER_FOLDER}/{LINK_FLAG_POINTER_AWAY}", null, [1]));
        test.WaitFor(() => Context.Documents.Find(readme)?.Editor.RemoteCarets.Any(remote =>
            remote.Id == peerId && remote.PointerLine is null) == true, 8);
        test.Check("link-files: leaving the document hides B's pointer and keeps its caret", () =>
            Context.Documents.Find(readme)?.Editor.RemoteCarets.Any(remote => remote.Id == peerId
                && remote is { Line: 0, Column: 2, PointerLine: null, PointerColumn: null }) == true);
        test.Run(() =>
        {
            if (Context.Documents.Find(readme) is { } open)
                CloseDocument(open);
            if (peerId is not null)
                LinkFollow(peerId);
        });
        bool FollowingBInReadme() => peerId is not null && LinkFollowing == peerId
            && document.Path is { } path && PlatformPaths.Same(path, readme) && document.Editor.Document.Caret == new UITextPosition(0, 2);
        test.WaitFor(FollowingBInReadme, 10.0f);
        test.Check("link-files: following B opens its file here with the caret on B's", FollowingBInReadme);
        test.Run(() => document.Editor.GoTo(0, 0));
        test.Settle(0.2f);
        test.Check("link-files: moving the caret here lets go of following", () => LinkFollowing is null);

        // A's rights on B down to View: the sync goes pull-only, what changes here stays here, what changes there comes
        string pulled = Path.Combine(mine, "src", "pulled.txt"), pulledThere = Path.Combine(theirs, "src", "pulled.txt");
        test.Run(() => _ = peer?.Write($"{LINK_PEER_FOLDER}/{LINK_FLAG_VIEW}", null, [1]));
        test.WaitFor(() => peer?.Granted == HexLinkRights.View, 20.0f);
        test.Run(() => File.WriteAllText(Path.Combine(mine, "src", "stays.txt"), "Stays on A.\n"));
        test.WaitFor(() => Sync() is { State: HexLinkSyncState.PullOnly }, 20.0f);
        test.Check("link-files: with rights dropped to View the sync goes pull-only and a change here doesn't go there", () =>
            Sync() is { State: HexLinkSyncState.PullOnly, PullOnly: true } && !File.Exists(Path.Combine(theirs, "src", "stays.txt")));
        test.Run(() => File.WriteAllText(pulledThere, "Pulled while pull-only.\n"));
        test.WaitFor(() => Read(pulled) == "Pulled while pull-only.\n", 15.0f);
        test.Check("link-files: pull-only, a change on B still comes here", () => Read(pulled) == "Pulled while pull-only.\n");

        TestLinkOfferPatch(test, () => peer, mine, theirs);

        string? diskBeforeDraft = null;
        test.Run(() => { Open(shared); diskBeforeDraft = Read(shared); Rewrite(document, "Unsaved on A.\n"); File.WriteAllText(sharedThere, "Incoming from B.\n"); });
        test.WaitFor(() => LinkConflicts.Any(item => item.Path == $"{LINK_SYNC_FOLDER}/src/shared.txt"), 15.0f);
        test.Check("link-files: an incoming write leaves the unsaved tab and saved file intact and shows a conflict", () =>
        {
            HexDocument? dirty = Context.Documents.Find(shared);
            LinkConflictShown? shown = LinkConflicts.FirstOrDefault(item => item.Path == $"{LINK_SYNC_FOLDER}/src/shared.txt");
            bool intact = dirty is not null && HasChanges(dirty) && dirty.Editor.Text == "Unsaved on A.\n" && Read(shared) == diskBeforeDraft;
            if (!intact || shown?.Copy is null)
                Console.WriteLine($"[Hex self-test] link-files: dirty tab {dirty?.Editor.Text.ReplaceLineEndings("\\n")}, disk {Read(shared).ReplaceLineEndings("\\n")}, conflict {shown?.Kind}, {Sync()?.Summary}");
            return intact && shown?.Copy is not null;
        });

        // Stopped by the person: the choice forgotten; B forgotten, it leaves
        test.Run(() =>
        {
            if (peerId is not null)
            {
                LinkSyncStop(peerId, LINK_SYNC_FOLDER);
                LinkForget(peerId);
            }
        });
        test.Check("link-files: stopping the sync forgets the choice", () => LinkSyncs.Count == 0 && !LinkSyncChosen(peerId ?? string.Empty).Contains(LINK_SYNC_FOLDER));
        test.WaitFor(() => linkTestPeer is null or { HasExited: true }, 60.0f);
        test.Run(() =>
        {
            linkTestExtraFolders = [];
            foreach (string folder in linkTestFolders)
                LinkTestWritable(folder);
        });
    }

    // A file of a folder only B has opened here as B's, edited and saved back, then saved again after B changed it: a
    // conflict, nothing written over, and taking theirs; a file of the synced folder opens as the local one
    private void TestLinkRemoteFiles(HexPointerScript test, Func<HexLinkPeer?> peer, string peerWork, string mine)
    {
        string notes = Path.Combine(peerWork, "notes.txt");
        string wire = $"{LINK_PEER_FOLDER}/notes.txt";
        Task<string?>? opened = null;
        HexDocument? tab = null;
        var sent = new List<string?>();
        static string Read(string file) => ReadLinkTestFile(file);

        test.Run(() =>
        {
            File.WriteAllText(notes, "Notes on B.\n");
            LinkRemoteSent = (_, how) => sent.Add(how);
            if (peer() is { } other)
                opened = LinkOpenRemote(other.Device.Id, wire);
        });
        test.WaitFor(() => opened is { IsCompleted: true }, 20.0f);
        test.Check("link-files: a file of a folder only B has opens here as a tab of B's, under the link data folder", () =>
        {
            tab = document;
            Console.WriteLine($"[Hex self-test] link-files: remote tab {LinkRemoteTitle(document)} at {document.Path}, {opened?.Result}");
            return opened is { IsCompletedSuccessfully: true, Result: null } && document.Path is { } path && PlatformPaths.IsUnder(path, Path.Combine(LinkDataFolder, LINK_REMOTE_FOLDER))
                && document.Editor.Text == "Notes on B.\n" && LinkRemoteTitle(document) == $"notes.txt ({LINK_PEER_NAME})" && !document.Editor.ReadOnly;
        });

        test.Run(() =>
        {
            if (tab is not null && documents.Contains(tab))
            {
                Show(tab);
                Rewrite(tab, "Edited from A.\n");
                Save();
            }
        });
        test.WaitFor(() => sent.Count > 0, 15.0f);
        test.Check("link-files: saving the remote tab writes it on B's disk", () => sent.FirstOrDefault() is null && sent.Count == 1 && Read(notes) == "Edited from A.\n");

        // Changed here and then on B before it's saved: the save is a conflict and B's stays
        test.Run(() =>
        {
            if (tab is null || !documents.Contains(tab))
                return;
            Show(tab);
            Rewrite(tab, "A's second edit.\n");
            File.WriteAllText(notes, "Changed on B meanwhile.\n");
            Save();
        });
        test.WaitFor(() => sent.Count > 1, 15.0f);
        test.Check("link-files: a stale save is a conflict offered its ways out, nothing written over on B", () =>
            sent.Count == 2 && sent[1] == "conflict" && Read(notes) == "Changed on B meanwhile.\n");
        test.Run(() =>
        {
            if (tab?.Path is { } copy)
                LinkRemoteSettle(copy, "theirs");
        });
        test.WaitFor(() => tab?.Editor.Text == "Changed on B meanwhile.\n", 15.0f);
        test.Check("link-files: taking theirs makes the tab B's version", () => tab?.Editor.Text == "Changed on B meanwhile.\n" && !HasChanges(tab));

        // A file of the synced folder is this workspace's own
        test.Run(() =>
        {
            LinkRemoteSent = null;
            if (peer() is { } other)
                opened = LinkOpenRemote(other.Device.Id, $"{LINK_SYNC_FOLDER}/README.md");
        });
        test.WaitFor(() => opened is { IsCompleted: true }, 20.0f);
        test.Check("link-files: opening a file of the synced folder opens the local file", () =>
            opened is { IsCompletedSuccessfully: true, Result: null } && document.Path is { } path && PlatformPaths.Same(path, Path.Combine(mine, "README.md")));
    }

    // B's repository cloned into a new folder over the link, then a commit made on B fetched into refs/remotes/hex-b
    private void TestLinkRepository(HexPointerScript test, Func<HexLinkPeer?> peer, string mine, string theirs)
    {
        string into = Path.Combine(LinkTestFolder("hex-link-clone-", test), "cloned");
        Task<string?>? cloned = null;
        Task<string>? fetched = null;
        string? theirHead = null;
        static string Head(string repo, string what = "HEAD") => GitProcess.Run(repo, ["rev-parse", what]) is { Ok: true } said ? said.Output.Trim() : "(none)";

        test.Run(() =>
        {
            if (peer() is { } other)
                cloned = LinkClone(other.Device.Id, LINK_SYNC_FOLDER, into);
        });
        test.WaitFor(() => cloned is { IsCompleted: true }, 60.0f);
        test.Check("link-files: a clone from B over the link has B's HEAD and no remote", () =>
        {
            Console.WriteLine($"[Hex self-test] link-files: clone said {(cloned is { IsCompletedSuccessfully: true } ? cloned.Result : cloned?.Exception?.Message)}");
            return cloned is { IsCompletedSuccessfully: true, Result: null } && Head(into) == Head(theirs) && Head(into).Length == 40
                && GitProcess.Run(into, ["remote"]) is { Ok: true } remotes && remotes.Output.Trim().Length == 0;
        });

        test.Run(() =>
        {
            GitProcess.Run(theirs, ["add", "-A"]);
            GitProcess.Run(theirs, ["-c", "user.name=Hex B", "-c", "user.email=b@localhost", "commit", "-q", "-m", "On B"]);
            theirHead = Head(theirs);
            if (peer() is { } other)
                fetched = LinkFetch(other.Device.Id, mine, LINK_SYNC_FOLDER);
        });
        test.WaitFor(() => fetched is { IsCompleted: true }, 60.0f);
        test.Check("link-files: a fetch from B brings its new commit into refs/remotes/hex-b", () =>
        {
            Console.WriteLine($"[Hex self-test] link-files: fetch said {(fetched is { IsCompletedSuccessfully: true } ? fetched.Result : fetched?.Exception?.Message)}");
            return fetched is { IsCompletedSuccessfully: true } && theirHead is { Length: 40 } && Head(mine, "refs/remotes/hex-b/main") == theirHead;
        });

        // This side's index brought to B's commit (the files are the same, the sync made them so), for the patch later
        test.Run(() => GitProcess.Run(mine, ["reset", "-q", "refs/remotes/hex-b/main"]));
    }

    // While pull-only, a change here stays here: offered to B as a patch, B applies it
    private void TestLinkOfferPatch(HexPointerScript test, Func<HexLinkPeer?> peer, string mine, string theirs)
    {
        Task<string>? answered = null;
        string shared = Path.Combine(mine, "src", "shared.txt"), sharedThere = Path.Combine(theirs, "src", "shared.txt");
        test.Run(() =>
        {
            File.WriteAllText(shared, "Offered from A.\n");
            if (peer() is { } other)
                answered = LinkOfferChanges(other.Device.Id, mine, "From A");
        });
        test.WaitFor(() => answered is { IsCompleted: true }, 60.0f);
        test.Check("link-files: changes offered to B arrive as a patch and B applies it", () =>
        {
            Console.WriteLine($"[Hex self-test] link-files: the offer was {(answered is { IsCompletedSuccessfully: true } ? answered.Result : answered?.Exception?.Message)}");
            return answered is { IsCompletedSuccessfully: true, Result: "applied" } && File.ReadAllText(sharedThere) == "Offered from A.\n" && LinkTestPeerSaid("patch \"From A\" applied");
        });
    }

    /// <summary>
    /// The part's repository made at <paramref name="mine"/> (a commit of a few files and a .gitignore that leaves out
    /// bin/ and the flag files), cloned to <paramref name="theirs"/>, then a file only on each side and a built file
    /// here git ignores. Null when it's all there, why when git said no.
    /// </summary>
    private static string? LinkTestRepository(string mine, string theirs)
    {
        if (HexLinkGit.Why() is { } cannot)
            return cannot;
        Directory.CreateDirectory(Path.Combine(mine, "src"));
        File.WriteAllText(Path.Combine(mine, "README.md"), "# Link sync\n");
        File.WriteAllText(Path.Combine(mine, "src", "shared.txt"), "Shared from the start.\n");
        File.WriteAllText(Path.Combine(mine, ".gitignore"), "bin/\n*.flag\n");
        // Line ends as they're written on both sides, as two machines that don't turn them would have them (the files
        // here are written with \n, a clone that turned them into \r\n would differ in every byte)
        foreach (string[] step in (string[][])[["init", "-q", "-b", "main"], ["config", "core.autocrlf", "false"], ["add", "-A"], ["-c", "user.name=Hex", "-c", "user.email=hex@localhost", "commit", "-q", "-m", "First"]])
        {
            if (GitProcess.Run(mine, step) is { Ok: false } failed)
                return $"git {step[0]} failed, {failed.Why}";
        }

        if (GitProcess.Run(Path.GetDirectoryName(theirs)!, ["clone", "-q", "-c", "core.autocrlf=false", mine, theirs]) is { Ok: false } notCloned)
            return $"git clone failed, {notCloned.Why}";

        File.WriteAllText(Path.Combine(mine, "only-a.txt"), "Only on A.\n");
        File.WriteAllText(Path.Combine(theirs, "only-b.txt"), "Only on B.\n");
        Directory.CreateDirectory(Path.Combine(mine, "bin"));
        File.WriteAllText(Path.Combine(mine, "bin", "built.txt"), "Built here.\n");
        return null;
    }

    // Link's rename can still hold a handle with delete access as the new file becomes visible.
    private static string ReadLinkTestFile(string file)
    {
        if (!File.Exists(file))
            return "(no file)";
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Git's objects are read-only, which keeps the tidy from deleting them on Windows
    private static void LinkTestWritable(string folder)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
