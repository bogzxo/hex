using Hex.Core;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

/// <summary>Empty directories travel both ways beside the files: made, removed and renamed on one side, followed on the other.</summary>
public sealed partial class HexLinkSyncTests
{
    private static HexLinkSync DirsSyncOf(LinkedPair pair) =>
        new(pair.PeerB, pair.A.Workspace.Folders[0], Path.Combine(pair.A.Root, "state"))
        {
            Quiet = TimeSpan.FromMilliseconds(50),
            Rescan = TimeSpan.FromSeconds(2),
            DirectoryInterval = TimeSpan.FromMilliseconds(100),
            DirectoryIdleInterval = TimeSpan.FromMilliseconds(100)
        };

    private static bool Dir(LinkServices side, string relative) => Directory.Exists(Path.Combine(side.Work, relative));

    private static void MakeDir(LinkServices side, string relative) => Directory.CreateDirectory(Path.Combine(side.Work, relative));

    [Fact]
    public async Task An_empty_directory_made_on_either_side_appears_on_the_other()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Put(pair.A, "keep.txt", "k");
        MakeDir(pair.A, "assets/empty");
        MakeDir(pair.B, "from/b");
        using HexLinkSync sync = DirsSyncOf(pair);

        await sync.Start();
        await Settled(sync);
        Assert.True(await Until(() => Dir(pair.B, "assets/empty") && Dir(pair.A, "from/b")), Said(sync));

        // Made later, while it runs
        MakeDir(pair.A, "later/one");
        MakeDir(pair.B, "later/two");
        Assert.True(await Until(() => Dir(pair.B, "later/one") && Dir(pair.A, "later/two")), Said(sync));
        Assert.True(sync.DirectoriesMade >= 4, Said(sync));
    }

    [Fact]
    public async Task An_empty_directory_deleted_on_either_side_is_deleted_on_the_other_and_stays_deleted()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        MakeDir(pair.A, "gone-from-a");
        MakeDir(pair.A, "gone-from-b");
        using HexLinkSync sync = DirsSyncOf(pair);
        await sync.Start();
        await Settled(sync);
        Assert.True(await Until(() => Dir(pair.B, "gone-from-a") && Dir(pair.B, "gone-from-b")), Said(sync));

        // Let the pass that agreed them on both sides finish before taking them away
        Assert.True(await Until(() => File.Exists(sync.StateFile + ".dirs") && File.ReadAllText(sync.StateFile + ".dirs").Contains("gone-from-b")), Said(sync));
        Directory.Delete(Path.Combine(pair.A.Work, "gone-from-a"));
        Directory.Delete(Path.Combine(pair.B.Work, "gone-from-b"));
        Assert.True(await Until(() => !Dir(pair.B, "gone-from-a") && !Dir(pair.A, "gone-from-b")), Said(sync));

        // And it isn't made again by the side that had it last
        await Task.Delay(600);
        Assert.False(Dir(pair.A, "gone-from-a") || Dir(pair.B, "gone-from-a") || Dir(pair.A, "gone-from-b") || Dir(pair.B, "gone-from-b"), Said(sync));
    }

    [Fact]
    public async Task A_renamed_empty_directory_moves_on_the_other_side()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        MakeDir(pair.A, "old/leaf");
        using HexLinkSync sync = DirsSyncOf(pair);
        await sync.Start();
        await Settled(sync);
        Assert.True(await Until(() => Dir(pair.B, "old/leaf")), Said(sync));
        Assert.True(await Until(() => File.Exists(sync.StateFile + ".dirs") && File.ReadAllText(sync.StateFile + ".dirs").Contains("old/leaf")), Said(sync));

        Directory.Move(Path.Combine(pair.A.Work, "old"), Path.Combine(pair.A.Work, "new"));
        Assert.True(await Until(() => Dir(pair.B, "new/leaf") && !Dir(pair.B, "old")), Said(sync));
    }

    [Fact]
    public async Task A_directory_the_ignore_rules_leave_out_stays_where_it_is()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Put(pair.A, ".hex-link-ignore", "cache/\n");
        Put(pair.B, ".hex-link-ignore", "cache/\n");
        MakeDir(pair.A, "cache/inner");
        MakeDir(pair.A, "visible");
        using HexLinkSync sync = DirsSyncOf(pair);
        await sync.Start();
        await Settled(sync);
        Assert.True(await Until(() => Dir(pair.B, "visible")), Said(sync));
        await Task.Delay(600);
        Assert.False(Dir(pair.B, "cache"), Said(sync));
    }

    [Fact]
    public async Task A_folder_with_files_deleted_on_one_side_isnt_brought_back_by_the_empty_directory_left_on_the_other()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Put(pair.A, "pkg/inner/a.txt", "a");
        using HexLinkSync sync = DirsSyncOf(pair);
        await sync.Start();
        await Settled(sync);
        Assert.True(await Until(() => Get(pair.B, "pkg/inner/a.txt") == "a"), Said(sync));

        Directory.Delete(Path.Combine(pair.A.Work, "pkg"), recursive: true);
        Assert.True(await Until(() => !Has(pair.B, "pkg/inner/a.txt")), Said(sync));
        Assert.True(await Until(() => !Dir(pair.B, "pkg")), Said(sync));
        await Task.Delay(600);
        Assert.False(Dir(pair.A, "pkg"), Said(sync));
    }

    [Fact]
    public void A_pass_makes_what_only_one_side_has_and_follows_what_was_agreed_gone()
    {
        static bool No(string path) => false;
        HexLinkDirPlan plan = HexLinkDirs.Plan(["a", "a/b", "new-here"], ["a", "a/b", "new-there"], [], [], No, No, canSend: true, canReceive: true);
        Assert.Equal(["new-here"], plan.MakeThere);
        Assert.Equal(["new-there"], plan.MakeHere);
        Assert.Empty(plan.RemoveHere);
        Assert.Empty(plan.RemoveThere);
        Assert.Equal(["a", "a/b", "new-here", "new-there"], plan.Agreed);

        // Agreed before, gone from there: a delete to take, deepest first
        plan = HexLinkDirs.Plan(["a", "a/b"], [], ["a", "a/b"], [], No, No, canSend: true, canReceive: true);
        Assert.Equal(["a/b", "a"], plan.RemoveHere);
        Assert.Empty(plan.MakeThere);

        // Gone from here, still there: a delete to send
        plan = HexLinkDirs.Plan([], ["a", "a/b"], ["a", "a/b"], [], No, No, canSend: true, canReceive: true);
        Assert.Equal(["a/b", "a"], plan.RemoveThere);
        Assert.Empty(plan.MakeHere);
    }

    [Fact]
    public void A_directory_files_were_taken_out_of_is_a_delete_and_a_directory_with_files_over_there_is_left_alone()
    {
        static bool No(string path) => false;
        // Left over where files went, the other side lacks it: not new
        HexLinkDirPlan plan = HexLinkDirs.Plan(["pkg"], [], [], ["pkg"], No, No, canSend: true, canReceive: true);
        Assert.Equal(["pkg"], plan.RemoveHere);
        Assert.Empty(plan.MakeThere);

        // The other side has it with files in: the file sync's
        plan = HexLinkDirs.Plan(["pkg"], [], ["pkg"], [], No, path => path == "pkg", canSend: true, canReceive: true);
        Assert.False(plan.Any);
        Assert.Empty(plan.Agreed);
    }

    [Fact]
    public void Without_the_right_to_send_or_to_receive_a_side_only_does_what_it_may()
    {
        static bool No(string path) => false;
        HexLinkDirPlan plan = HexLinkDirs.Plan(["mine"], ["theirs"], [], [], No, No, canSend: false, canReceive: true);
        Assert.Empty(plan.MakeThere);
        Assert.Equal(["theirs"], plan.MakeHere);

        plan = HexLinkDirs.Plan(["mine"], ["theirs"], [], [], No, No, canSend: true, canReceive: false);
        Assert.Equal(["mine"], plan.MakeThere);
        Assert.Empty(plan.MakeHere);
    }
}
