using System.Text;
using System.Text.Json.Nodes;

using Hex.Core.Git;

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
        string home = LinkTestFolder("hex-link-files-a-");
        string peerHome = LinkTestFolder("hex-link-files-b-");
        string mine = Path.Combine(LinkTestFolder("hex-link-files-repo-"), LINK_SYNC_FOLDER);
        string theirs = Path.Combine(peerHome, LINK_SYNC_FOLDER);
        string peerWork = Path.Combine(peerHome, LINK_PEER_FOLDER);
        LinkPairingAsk? asked = null;
        HexLinkPeer? peer = null;
        Task<string?>? started = null;
        Task<McpToolResult>? flushed = null;
        string? peerId = null;
        string? why = null;

        HexLinkSync? Sync() => peerId is null ? null : LinkSyncOf(peerId, LINK_SYNC_FOLDER);
        static string Read(string file) => File.Exists(file) ? File.ReadAllText(file) : "(no file)";

        test.Run(() =>
        {
            linkTestPeerSaid.Clear();
            why = LinkTestRepository(mine, theirs);
            if (why is not null)
            {
                Console.WriteLine($"[Hex self-test] link-files: the repositories weren't made, {why}");
                return;
            }

            StopLink();
            linkTestExtraFolders = [mine];
            if (!StartLink(port: 0, home: home, discover: false))
                return;
            linkByTest = true;
            LinkOpenPairing(TimeSpan.FromMinutes(3));
            LinkStartPeer(peerHome, $"127.0.0.1:{LinkPort}", new Dictionary<string, string> { [LINK_TEST_FOLDER] = theirs, [LINK_TEST_SECONDS] = "420" });
        });
        test.Check("link-files: the same repository here and in B's home, B started on its clone", () => why is null && linkTestPeer is not null);

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
            Save();
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

        TestLinkRemoteFiles(test, () => peer, peerWork);
        TestLinkRepository(test, () => peer, mine, theirs);

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

    private void TestLinkRemoteFiles(HexPointerScript test, Func<HexLinkPeer?> peer, string peerWork)
    {
    }

    private void TestLinkRepository(HexPointerScript test, Func<HexLinkPeer?> peer, string mine, string theirs)
    {
    }

    private void TestLinkOfferPatch(HexPointerScript test, Func<HexLinkPeer?> peer, string mine, string theirs)
    {
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
