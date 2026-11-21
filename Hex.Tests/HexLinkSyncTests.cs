using System.Text;
using System.Text.Json.Nodes;

using Hex.Core;
using Hex.Core.Git;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

/// <summary>
/// The working-tree sync over two Hexes linked in memory, each with a temp folder for its workspace: the first
/// comparison both ways, live edits both ways without echoes, conflicts kept beside and resolved, deletes, what never
/// travels, bursts, two engines at once, rights changing, heads that differ, restarts from the state file, a link
/// dropping halfway and a peer that lies. The cases that need git pass over quietly where it isn't installed.
/// </summary>
public sealed class HexLinkSyncTests
{
    // Long enough for a slow machine running the whole suite, never what a test passes by
    private const int LONG_MS = 60_000;

    private static readonly HexLinkRights EDIT = HexLinkRights.View | HexLinkRights.Edit;

    private static bool NoGit => HexLinkGit.Why() is not null;

    // A's sync of its folder with B, its state in a folder beside the workspace
    private static HexLinkSync SyncOf(LinkedPair pair, HexLinkPeer? peerB = null) =>
        new(peerB ?? pair.PeerB, pair.A.Workspace.Folders[0], Path.Combine(pair.A.Root, "state"))
        {
            Quiet = TimeSpan.FromMilliseconds(50),
            Rescan = TimeSpan.FromSeconds(2)
        };

    // B's sync of its folder with A, for two engines at once
    private static HexLinkSync SyncOfB(LinkedPair pair) =>
        new(pair.PeerA, pair.B.Workspace.Folders[0], Path.Combine(pair.B.Root, "state"))
        {
            Quiet = TimeSpan.FromMilliseconds(50),
            Rescan = TimeSpan.FromSeconds(2)
        };

    private static void Put(LinkServices side, string relative, string text)
    {
        string full = Path.Combine(side.Work, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static string? Get(LinkServices side, string relative)
    {
        try
        {
            string full = Path.Combine(side.Work, relative);
            return File.Exists(full) ? File.ReadAllText(full) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool Has(LinkServices side, string relative) => File.Exists(Path.Combine(side.Work, relative));

    // Waits until something is so without holding a thread, false when it never was
    private static async Task<bool> Until(Func<bool> so, int ms = LONG_MS)
    {
        long until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            if (so())
                return true;
            await Task.Delay(20);
        }

        return so();
    }

    private static async Task Settled(HexLinkSync sync)
    {
        await sync.Flush().WaitAsync(TimeSpan.FromMilliseconds(LONG_MS));
        Assert.True(await Until(() => sync.State is HexLinkSyncState.Idle or HexLinkSyncState.PullOnly), sync.Summary);
    }

    private static void Git(string folder, params string[] arguments)
    {
        GitResult done = GitProcess.Run(folder, arguments);
        Assert.True(done.Ok, $"git {string.Join(' ', arguments)}: {done.Error}");
    }

    // A repository with a commit in A's folder, cloned into B's, both at the same commit
    private static void Repositories(LinkedPair pair, string ignore = "")
    {
        string repo = pair.A.Work;
        Git(repo, "init", "-q", "-b", "main");
        Git(repo, "config", "user.email", "test@example.com");
        Git(repo, "config", "user.name", "Test");
        Git(repo, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(repo, "readme.md"), "one\n");
        if (ignore.Length > 0)
            File.WriteAllText(Path.Combine(repo, ".gitignore"), ignore);
        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "first");
        Git(pair.B.Root, "clone", "-q", "-c", "core.autocrlf=false", repo, pair.B.Work);
        Git(pair.B.Work, "config", "user.email", "test@example.com");
        Git(pair.B.Work, "config", "user.name", "Test");
    }

    [Fact]
    public async Task The_first_comparison_copies_both_ways()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Put(pair.A, "a.txt", "from A");
        Put(pair.A, "sub/deep/x.txt", "deep A");
        Put(pair.B, "b.txt", "from B");
        using HexLinkSync sync = SyncOf(pair);

        await sync.Start();
        Assert.Contains(sync.State, new[] { HexLinkSyncState.Syncing, HexLinkSyncState.Idle });
        Assert.True(await Until(() => Get(pair.B, "a.txt") == "from A" && Get(pair.B, "sub/deep/x.txt") == "deep A" && Get(pair.A, "b.txt") == "from B"), sync.Summary);
        await Settled(sync);
        Assert.Equal(2, sync.Sent);
        Assert.Equal(1, sync.Received);
        Assert.Empty(sync.Conflicts);
        Assert.Equal(HexLinkSyncState.Idle, sync.State);
        Assert.Contains("in sync", sync.Summary);
    }

    [Fact]
    public async Task Identical_trees_transfer_nothing()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        foreach (LinkServices side in new[] { pair.A, pair.B })
        {
            Put(side, "one.txt", "same");
            Put(side, "two/three.txt", "also same");
        }

        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);
        Assert.Equal(0, sync.Sent);
        Assert.Equal(0, sync.Received);
        Assert.Empty(sync.Conflicts);
        Assert.Empty(sync.Skipped);
    }

    [Fact]
    public async Task An_edit_arrives_on_the_other_side_both_ways_and_a_pulled_write_doesnt_echo()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Put(pair.A, "code.cs", "class A {}");
        Put(pair.B, "code.cs", "class A {}");
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);

        Put(pair.A, "code.cs", "class A { int here; }");
        Assert.True(await Until(() => Get(pair.B, "code.cs") == "class A { int here; }"), sync.Summary);
        await Settled(sync);
        Assert.Equal(1, sync.Sent);

        Put(pair.B, "code.cs", "class A { int there; }");
        Put(pair.B, "new/made-there.cs", "new");
        Assert.True(await Until(() => Get(pair.A, "code.cs") == "class A { int there; }" && Get(pair.A, "new/made-there.cs") == "new"), sync.Summary);
        Assert.True(await Until(() => sync.Received == 2), sync.Summary);

        // This side's tree looked over whole: the files written from there are at their bases, nothing goes back
        await Settled(sync);
        await Settled(sync);
        Assert.Equal(1, sync.Sent);
        Assert.Equal(2, sync.Received);
        Assert.Empty(sync.Conflicts);
    }

    [Fact]
    public async Task Both_changed_keeps_the_others_version_beside_overwrites_neither_and_resolves_both_ways()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        foreach (LinkServices side in new[] { pair.A, pair.B })
        {
            Put(side, "f.txt", "base");
            Put(side, "g.txt", "base g");
            Put(side, ".editorconfig", "base e");
        }

        HexLinkSync first = SyncOf(pair);
        await first.Start();
        await Settled(first);
        first.Stop();

        Put(pair.A, "f.txt", "mine");
        Put(pair.B, "f.txt", "theirs");
        Put(pair.A, "g.txt", "mine g");
        Put(pair.B, "g.txt", "theirs g");
        Put(pair.A, ".editorconfig", "mine e");
        Put(pair.B, ".editorconfig", "theirs e");
        // A copy already there from before is never written over
        Put(pair.A, "g.theirs-laptop.txt", "an older copy");

        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        Assert.True(await Until(() => sync.Conflicts.Count == 3), sync.Summary);
        await Settled(sync);
        Assert.Equal("mine", Get(pair.A, "f.txt"));
        Assert.Equal("theirs", Get(pair.B, "f.txt"));
        Assert.Equal("theirs", Get(pair.A, "f.theirs-laptop.txt"));
        Assert.Equal("an older copy", Get(pair.A, "g.theirs-laptop.txt"));
        Assert.Equal("theirs g", Get(pair.A, "g.theirs-laptop-2.txt"));
        Assert.Equal("theirs e", Get(pair.A, ".editorconfig.theirs-laptop"));
        HexLinkSyncConflict f = sync.Conflicts.Single(each => each.Path == "work/f.txt");
        Assert.Equal("changed on both", f.Kind);
        Assert.Equal("work/f.theirs-laptop.txt", f.Copy);
        // The copies never travel
        Assert.False(Has(pair.B, "f.theirs-laptop.txt"));
        Assert.False(Has(pair.B, "g.theirs-laptop.txt"));

        Assert.Null(await sync.Resolve("work/f.txt", HexLinkSyncKeep.Mine));
        Assert.Equal("mine", Get(pair.B, "f.txt"));
        Assert.False(Has(pair.A, "f.theirs-laptop.txt"));

        Assert.Null(await sync.Resolve("work/g.txt", HexLinkSyncKeep.Theirs));
        Assert.Equal("theirs g", Get(pair.A, "g.txt"));
        Assert.False(Has(pair.A, "g.theirs-laptop-2.txt"));
        Assert.Equal("an older copy", Get(pair.A, "g.theirs-laptop.txt"));
        Assert.NotNull(await sync.Resolve("work/g.txt", HexLinkSyncKeep.Mine));

        Assert.Single(sync.Conflicts);
        await Settled(sync);
        // Settled files move again as usual
        Put(pair.A, "f.txt", "after");
        Assert.True(await Until(() => Get(pair.B, "f.txt") == "after"), sync.Summary);
        Assert.Equal("theirs e", Get(pair.B, ".editorconfig"));
    }

    [Fact]
    public async Task Both_sides_saving_one_file_at_once_while_it_runs_is_a_conflict_whichever_is_heard_first()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        foreach (LinkServices side in new[] { pair.A, pair.B })
            Put(side, "race.txt", "base");
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);

        Put(pair.B, "race.txt", "saved there");
        Put(pair.A, "race.txt", "saved here");
        Assert.True(await Until(() => sync.Conflicts.Count == 1), sync.Summary);
        await Settled(sync);
        Assert.Equal("saved here", Get(pair.A, "race.txt"));
        Assert.Equal("saved there", Get(pair.B, "race.txt"));
        Assert.Equal("saved there", Get(pair.A, "race.theirs-laptop.txt"));
        Assert.Equal(0, sync.Sent);
    }

    [Fact]
    public async Task A_delete_is_carried_both_ways()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        foreach (LinkServices side in new[] { pair.A, pair.B })
        {
            Put(side, "gone-here.txt", "x");
            Put(side, "gone-there.txt", "y");
            Put(side, "while-off.txt", "z");
        }

        HexLinkSync first = SyncOf(pair);
        await first.Start();
        await Settled(first);
        first.Stop();
        // Deleted while the sync was off, carried over when it starts again from its bases
        File.Delete(Path.Combine(pair.B.Work, "while-off.txt"));

        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        Assert.True(await Until(() => !Has(pair.A, "while-off.txt")), sync.Summary);

        File.Delete(Path.Combine(pair.A.Work, "gone-here.txt"));
        Assert.True(await Until(() => !Has(pair.B, "gone-here.txt")), sync.Summary);
        File.Delete(Path.Combine(pair.B.Work, "gone-there.txt"));
        Assert.True(await Until(() => !Has(pair.A, "gone-there.txt")), sync.Summary);
        await Settled(sync);
        Assert.Empty(sync.Conflicts);
    }

    [Fact]
    public async Task A_delete_against_a_change_is_a_conflict_and_the_changed_one_is_kept()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        foreach (LinkServices side in new[] { pair.A, pair.B })
        {
            Put(side, "d.txt", "base");
            Put(side, "e.txt", "base");
        }

        HexLinkSync first = SyncOf(pair);
        await first.Start();
        await Settled(first);
        first.Stop();

        File.Delete(Path.Combine(pair.A.Work, "d.txt"));
        Put(pair.B, "d.txt", "changed there");
        Put(pair.A, "e.txt", "changed here");
        File.Delete(Path.Combine(pair.B.Work, "e.txt"));

        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        Assert.True(await Until(() => sync.Conflicts.Count == 2), sync.Summary);
        await Settled(sync);
        Assert.Equal("changed there", Get(pair.B, "d.txt"));
        Assert.False(Has(pair.A, "d.txt"));
        Assert.Equal("changed here", Get(pair.A, "e.txt"));
        Assert.False(Has(pair.B, "e.txt"));
        Assert.Equal("deleted here, changed there", sync.Conflicts.Single(each => each.Path == "work/d.txt").Kind);
        Assert.Equal("changed here, deleted there", sync.Conflicts.Single(each => each.Path == "work/e.txt").Kind);
        Assert.All(sync.Conflicts, each => Assert.Null(each.Copy));

        Assert.Null(await sync.Resolve("work/d.txt", HexLinkSyncKeep.Theirs));
        Assert.Equal("changed there", Get(pair.A, "d.txt"));
        Assert.Null(await sync.Resolve("work/e.txt", HexLinkSyncKeep.Mine));
        Assert.Equal("changed here", Get(pair.B, "e.txt"));
        Assert.Empty(sync.Conflicts);
    }

    [Fact]
    public async Task What_git_ignores_never_travels_either_way()
    {
        if (NoGit)
            return;
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Repositories(pair, "bin/\nobj/\n");
        Put(pair.A, "bin/Debug/app.dll", "built here");
        Put(pair.A, "obj/cache.json", "{}");
        Put(pair.B, "bin/Debug/app.dll", "built there");
        Put(pair.B, "obj/other.json", "{}");
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);

        Put(pair.A, "bin/Release/more.dll", "more");
        Put(pair.A, "src/marker.txt", "marker");
        Assert.True(await Until(() => Get(pair.B, "src/marker.txt") == "marker"), sync.Summary);
        await Settled(sync);
        Assert.Equal("built there", Get(pair.B, "bin/Debug/app.dll"));
        Assert.Equal("built here", Get(pair.A, "bin/Debug/app.dll"));
        Assert.False(Has(pair.B, "obj/cache.json"));
        Assert.False(Has(pair.A, "obj/other.json"));
        Assert.False(Has(pair.B, "bin/Release/more.dll"));
        Assert.Empty(sync.Conflicts);
    }

    [Fact]
    public async Task A_file_over_the_cap_is_skipped_and_listed_with_why()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        File.WriteAllBytes(Path.Combine(pair.A.Work, "big.bin"), new byte[HexLinkFiles.MOST_READ + 1]);
        Put(pair.A, "small.txt", "small");
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        Assert.True(await Until(() => Get(pair.B, "small.txt") == "small"), sync.Summary);
        await Settled(sync);
        HexLinkSyncSkipped big = Assert.Single(sync.Skipped);
        Assert.Equal("work/big.bin", big.Path);
        Assert.Contains("MB", big.Why);
        Assert.False(Has(pair.B, "big.bin"));
        Assert.Contains("1 skipped", sync.Summary);
    }

    [Fact]
    public async Task A_burst_of_300_files_converges()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);

        for (int i = 0; i < 300; i++)
            Put(pair.A, $"gen/{i % 10}/file{i}.txt", $"content {i}");
        // Some written twice, coalesced into the last
        for (int i = 0; i < 300; i += 7)
            Put(pair.A, $"gen/{i % 10}/file{i}.txt", $"again {i}");

        await sync.Flush().WaitAsync(TimeSpan.FromMilliseconds(LONG_MS * 2));
        for (int i = 0; i < 300; i++)
            Assert.Equal(i % 7 == 0 ? $"again {i}" : $"content {i}", Get(pair.B, $"gen/{i % 10}/file{i}.txt"));
        Assert.Empty(sync.Conflicts);
        Assert.InRange(sync.Sent, 300, 343);
    }

    [Fact]
    public async Task Both_sides_running_a_sync_converge_and_stop()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        for (int i = 0; i < 20; i++)
        {
            Put(pair.A, $"a/{i}.txt", $"A {i}");
            Put(pair.B, $"b/{i}.txt", $"B {i}");
        }

        using HexLinkSync onA = SyncOf(pair);
        using HexLinkSync onB = SyncOfB(pair);
        await Task.WhenAll(onA.Start(), onB.Start());
        Assert.True(await Until(() => Enumerable.Range(0, 20).All(i => Get(pair.B, $"a/{i}.txt") == $"A {i}" && Get(pair.A, $"b/{i}.txt") == $"B {i}")),
            onA.Summary + " / " + onB.Summary);

        Put(pair.A, "a/0.txt", "A edited");
        Put(pair.B, "b/0.txt", "B edited");
        Assert.True(await Until(() => Get(pair.B, "a/0.txt") == "A edited" && Get(pair.A, "b/0.txt") == "B edited"), onA.Summary + " / " + onB.Summary);

        await Task.WhenAll(Settled(onA), Settled(onB));
        int moved = onA.Sent + onA.Received + onB.Sent + onB.Received;
        // Each file went over once, by one engine or the other, or by both when they crossed
        Assert.InRange(moved, 42, 84);
        // A while with nothing changing: nothing moves, nobody sends back what they were sent
        await Task.Delay(1500);
        await Task.WhenAll(Settled(onA), Settled(onB));
        Assert.Equal(moved, onA.Sent + onA.Received + onB.Sent + onB.Received);
        Assert.Empty(onA.Conflicts);
        Assert.Empty(onB.Conflicts);
    }

    [Fact]
    public async Task Without_Edit_it_runs_pull_only()
    {
        using var pair = await LinkedPair.Up(EDIT, HexLinkRights.View);
        Put(pair.A, "mine.txt", "stays here");
        Put(pair.B, "theirs.txt", "comes here");
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        Assert.Equal(HexLinkSyncState.PullOnly, sync.State);
        Assert.True(sync.PullOnly);
        Assert.Contains("Edit", sync.Why);
        Assert.True(await Until(() => Get(pair.A, "theirs.txt") == "comes here"), sync.Summary);
        Put(pair.B, "theirs.txt", "changed there");
        Assert.True(await Until(() => Get(pair.A, "theirs.txt") == "changed there"), sync.Summary);
        await Settled(sync);
        Assert.False(Has(pair.B, "mine.txt"));
        Assert.Equal(0, sync.Sent);
        Assert.Equal(HexLinkSyncState.PullOnly, sync.State);
    }

    [Fact]
    public async Task Edit_taken_away_mid_sync_turns_it_pull_only()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        Put(pair.A, "x.txt", "one");
        Assert.True(await Until(() => Get(pair.B, "x.txt") == "one"), sync.Summary);

        pair.AOnB = HexLinkRights.View;
        pair.LinkB.RightsMoved(DeviceA.Id);
        Assert.True(await Until(() => !HexLinkAllowed.Has(pair.PeerB.Granted, HexLinkRights.Edit)));
        Put(pair.A, "x.txt", "two");
        Assert.True(await Until(() => sync.State == HexLinkSyncState.PullOnly), sync.Summary);
        Assert.Equal("one", Get(pair.B, "x.txt"));

        // Changes there still come
        Put(pair.B, "y.txt", "from there");
        Assert.True(await Until(() => Get(pair.A, "y.txt") == "from there"), sync.Summary);
    }

    [Fact]
    public async Task Edit_refused_by_the_other_side_before_it_says_so_turns_it_pull_only_too()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);
        // Taken away without telling, the next write is refused
        pair.AOnB = HexLinkRights.View;
        Put(pair.A, "z.txt", "refused");
        Assert.True(await Until(() => sync.State == HexLinkSyncState.PullOnly), sync.Summary);
        Assert.False(Has(pair.B, "z.txt"));
    }

    [Fact]
    public async Task Different_commits_checked_out_need_git_first_and_nothing_is_copied()
    {
        if (NoGit)
            return;
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Repositories(pair);
        Put(pair.A, "later.txt", "committed later");
        Git(pair.A.Work, "add", ".");
        Git(pair.A.Work, "commit", "-q", "-m", "second");
        Put(pair.A, "loose.txt", "not committed");

        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        Assert.Equal(HexLinkSyncState.NeedsGit, sync.State);
        Assert.NotNull(sync.LocalHead);
        Assert.NotNull(sync.RemoteHead);
        Assert.NotEqual(sync.LocalHead, sync.RemoteHead);
        Assert.Contains("git", sync.Why);
        await Task.Delay(300);
        Assert.False(Has(pair.B, "later.txt"));
        Assert.False(Has(pair.B, "loose.txt"));
        Assert.Equal(0, sync.Sent + sync.Received);

        // Told to go on anyway, it syncs the working trees as they are
        await sync.Start(force: true);
        Assert.True(await Until(() => Get(pair.B, "loose.txt") == "not committed" && Get(pair.B, "later.txt") == "committed later"), sync.Summary);
    }

    [Fact]
    public async Task The_manifest_says_the_repositorys_first_commit_the_same_in_a_clone()
    {
        if (NoGit)
            return;
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Repositories(pair);
        string first = GitProcess.Run(pair.A.Work, ["rev-parse", "HEAD"]).Output.Trim();
        Put(pair.A, "second.txt", "2");
        Git(pair.A.Work, "add", ".");
        Git(pair.A.Work, "commit", "-q", "-m", "second");

        HexLinkManifest there = await pair.PeerB.Manifest("work");
        HexLinkManifest here = new HexLinkTree(pair.A.Workspace.Folders[0]).Manifest();
        Assert.Equal(first, there.Root);
        Assert.Equal(first, here.Root);
        Assert.NotEqual(here.Head, there.Head);
        // Through the wire and back
        Assert.Equal(first, HexLinkTree.Of(HexLinkTree.Json(here)).Root);
        Assert.Null(HexLinkTree.Of(new JsonObject { ["folder"] = "work", ["root"] = "not a commit", ["entries"] = new JsonArray() }).Root);
    }

    [Fact]
    public async Task Two_different_repositories_with_one_folder_name_are_refused()
    {
        if (NoGit)
            return;
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        foreach (LinkServices side in new[] { pair.A, pair.B })
        {
            Git(side.Work, "init", "-q", "-b", "main");
            Git(side.Work, "config", "user.email", "test@example.com");
            Git(side.Work, "config", "user.name", "Test");
            Put(side, "readme.md", side == pair.A ? "A" : "B");
            Git(side.Work, "add", ".");
            Git(side.Work, "commit", "-q", "-m", "first");
        }

        using HexLinkSync sync = SyncOf(pair);
        await sync.Start(force: true);
        Assert.Equal(HexLinkSyncState.Failed, sync.State);
        Assert.Contains("different repositories", sync.Why);
        Assert.Equal("A", Get(pair.A, "readme.md"));
        Assert.Equal("B", Get(pair.B, "readme.md"));
    }

    [Fact]
    public async Task Started_again_from_its_state_file_it_transfers_nothing()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Put(pair.A, "one.txt", "1");
        Put(pair.B, "two.txt", "2");
        HexLinkSync first = SyncOf(pair);
        await first.Start();
        await Settled(first);
        Assert.Equal(1, first.Sent);
        first.Stop();
        Assert.Equal(HexLinkSyncState.Off, first.State);
        Assert.True(File.Exists(first.StateFile));

        using HexLinkSync again = SyncOf(pair);
        Assert.Equal(first.StateFile, again.StateFile);
        await again.Start();
        await Settled(again);
        Assert.Equal(0, again.Sent);
        Assert.Equal(0, again.Received);
    }

    [Fact]
    public async Task A_link_dropping_halfway_through_a_burst_stops_it_cleanly_and_a_new_link_finishes()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        HexLinkSync first = SyncOf(pair);
        await first.Start();
        await Settled(first);
        for (int i = 0; i < 200; i++)
            Put(pair.A, $"burst/file{i}.txt", $"burst {i}");
        Assert.True(await Until(() => first.Sent >= 10), first.Summary);

        pair.LinkA.Remove(DeviceB.Id, "pulled out");
        Assert.True(await Until(() => first.State == HexLinkSyncState.Off), first.Summary);
        Assert.Contains("closed", first.Why);
        Assert.True(await Until(() => pair.LinkA.PeerOf(DeviceB.Id) is null && pair.LinkB.PeerOf(DeviceA.Id) is null));

        var (a, b) = Pair();
        Task<HexLinkPeer> peerB = pair.LinkA.Add(new HexLinkConnection(a, DeviceB, "memory", true, () => pair.BOnA));
        Task<HexLinkPeer> peerA = pair.LinkB.Add(new HexLinkConnection(b, DeviceA, "memory", false, () => pair.AOnB));
        HexLinkPeer newPeerB = await peerB.WaitAsync(TimeSpan.FromMilliseconds(WAIT_MS));
        await peerA.WaitAsync(TimeSpan.FromMilliseconds(WAIT_MS));

        using HexLinkSync again = SyncOf(pair, newPeerB);
        await again.Start();
        await again.Flush().WaitAsync(TimeSpan.FromMilliseconds(LONG_MS * 2));
        for (int i = 0; i < 200; i++)
            Assert.Equal($"burst {i}", Get(pair.B, $"burst/file{i}.txt"));
        Assert.Empty(again.Conflicts);
        // Writes that landed there while their answers were lost on the way back aren't counted, nothing goes twice
        Assert.InRange(first.Sent + again.Sent, 190, 210);
    }

    [Fact]
    public async Task Flush_returns_only_once_the_other_side_has_the_bytes()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);
        for (int round = 0; round < 3; round++)
        {
            Put(pair.A, "build-me.cs", $"round {round}");
            Put(pair.A, $"extra/{round}.cs", "x");
            await sync.Flush().WaitAsync(TimeSpan.FromMilliseconds(LONG_MS));
            Assert.Equal($"round {round}", Get(pair.B, "build-me.cs"));
            Assert.Equal("x", Get(pair.B, $"extra/{round}.cs"));
        }
    }

    [Fact]
    public async Task Flush_and_resolve_say_so_when_it_isnt_running()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        using HexLinkSync sync = SyncOf(pair);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sync.Flush());
        Assert.NotNull(await sync.Resolve("work/x.txt", HexLinkSyncKeep.Mine));
        Assert.Contains("off", sync.Summary);
    }

    [Fact]
    public async Task A_folder_the_other_side_hasnt_got_fails_with_why()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        using HexLinkSync sync = new(pair.PeerB, new HexLinkFolder("elsewhere", pair.A.Work), Path.Combine(pair.A.Root, "state"));
        await sync.Start();
        Assert.Equal(HexLinkSyncState.Failed, sync.State);
        Assert.Contains("elsewhere", sync.Why);
    }

    [Fact]
    public async Task A_hostile_peer_can_never_make_this_side_write_outside_the_folder_into_git_or_over_the_state()
    {
        var (sessionA, sessionB) = Sessions(() => EDIT, () => EDIT);
        string root = Path.Combine(Path.GetTempPath(), "hexlink-tests", Guid.NewGuid().ToString("N"));
        string work = Path.Combine(root, "work");
        Directory.CreateDirectory(Path.Combine(work, ".git"));
        File.WriteAllText(Path.Combine(work, ".git", "config"), "real config");
        try
        {
            byte[] evil = Encoding.UTF8.GetBytes("evil");
            string evilHash = HexLinkFiles.Hash(evil);
            string stateName = HexLinkSync.StateName(DeviceB.Id, "work");
            string[] lies =
            [
                "work/../escaped.txt", "work/sub/../../escaped2.txt", "/etc/passwd", "work//double.txt", "work/.git/config", "work/.GIT/hooks/pre-commit",
                "work/" + stateName, "work/C:/x.txt", "work/a\\..\\..\\b.txt", "work/con.txt", "work/fine.txt", "work/ok/nested.txt", "work/x.theirs-laptop.txt",
                "other/elsewhere.txt", "work/\u0001.txt"
            ];
            var reads = new List<string>();
            sessionB.Requested = (method, parameters, _) =>
            {
                JsonNode? answer = method switch
                {
                    "link/hello" => new JsonObject
                    {
                        ["protocol"] = HexLinkHost.PROTOCOL,
                        ["device"] = HexLinkData.DeviceJson(DeviceB),
                        ["granted"] = new JsonArray("view", "edit"),
                        ["workspace"] = new JsonObject { ["name"] = "Lies", ["folders"] = new JsonArray(new JsonObject { ["name"] = "work" }) }
                    },
                    "sync/watch" or "sync/manifest" => new JsonObject
                    {
                        ["folder"] = "work",
                        ["entries"] = new JsonArray([.. lies.Select(path => (JsonNode)new JsonObject { ["path"] = path, ["hash"] = evilHash, ["size"] = evil.Length })])
                    },
                    "fs/read" => Read(parameters),
                    "fs/write" or "fs/delete" => new JsonObject { ["hash"] = evilHash },
                    _ => new JsonObject()
                };
                return Task.FromResult<JsonNode?>(answer);

                JsonNode Read(JsonObject asked)
                {
                    string path = HexLinkData.String(asked, "path", 4000) ?? string.Empty;
                    lock (reads)
                        reads.Add(path);
                    return new JsonObject { ["path"] = path, ["hash"] = evilHash, ["base64"] = Convert.ToBase64String(evil), ["encoding"] = "binary", ["size"] = evil.Length };
                }
            };

            var peer = new HexLinkPeer(sessionA, DeviceA);
            sessionA.Told = peer.Told;
            sessionA.Start();
            sessionB.Start();
            await peer.Hello();

            // The state kept inside the folder itself, which a peer naming it can't write over
            using var sync = new HexLinkSync(peer, new HexLinkFolder("work", work), work) { Quiet = TimeSpan.FromMilliseconds(50) };
            await sync.Start();
            await sync.Flush().WaitAsync(TimeSpan.FromMilliseconds(LONG_MS));

            Assert.Equal("evil", File.ReadAllText(Path.Combine(work, "fine.txt")));
            Assert.Equal("evil", File.ReadAllText(Path.Combine(work, "ok", "nested.txt")));
            Assert.Equal("real config", File.ReadAllText(Path.Combine(work, ".git", "config")));
            Assert.False(File.Exists(Path.Combine(work, ".git", "hooks", "pre-commit")));
            Assert.False(File.Exists(Path.Combine(root, "escaped.txt")));
            Assert.False(File.Exists(Path.Combine(root, "escaped2.txt")));
            Assert.False(File.Exists(Path.Combine(work, "x.theirs-laptop.txt")));
            Assert.False(File.Exists(Path.Combine(work, "double.txt")));
            string[] entries = [.. Directory.EnumerateFileSystemEntries(work).Select(full => Path.GetFileName(full)).Order(StringComparer.Ordinal)];
            Assert.Equal(new[] { ".git", "fine.txt", "ok", stateName }.Order(StringComparer.Ordinal), entries);
            Assert.DoesNotContain("evil", File.ReadAllText(sync.StateFile));
            Assert.True(JsonNode.Parse(File.ReadAllText(sync.StateFile)) is JsonObject);
            // Only what may be written was ever asked for
            lock (reads)
                Assert.All(reads, path => Assert.Contains(path, new[] { "work/fine.txt", "work/ok/nested.txt" }));
            Assert.Contains(sync.Skipped, each => each.Path == "work/../escaped.txt");
            Assert.Contains(sync.Skipped, each => each.Path == "work/.git/config");
            Assert.DoesNotContain(sync.Skipped, each => each.Path == "work/" + stateName);
        }
        finally
        {
            sessionA.Close("done");
            try { Directory.Delete(root, true); } catch (Exception) { }
        }
    }
}
