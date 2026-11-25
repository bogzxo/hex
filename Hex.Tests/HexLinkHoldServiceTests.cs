using System.Text;
using Hex.Core;
using Hex.Core.Merge;

namespace Hex.Tests;

public sealed class HexLinkHoldServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"hex-hold-service-{Guid.NewGuid():N}");
    private readonly HexLinkFolder folder;
    private readonly HexLinkHoldStore store;
    private readonly HexLinkHoldService service;
    private const string A = "work/a.txt", B = "work/b.txt", C = "work/c.txt";
    private const string Basis = "a\nb\nc\nd\ne\n", Mine = "A\nb\nc\nd\ne\n", Theirs = "a\nb\nc\nd\nE\n";
    private const string Combined = "A\nb\nc\nd\nE\n";

    public HexLinkHoldServiceTests()
    {
        folder = new("work", Directory.CreateDirectory(Path.Combine(root, "work")).FullName);
        store = new(folder, Path.Combine(root, "sync"));
        service = new(folder, store);
    }

    [Fact]
    public async Task A_hold_copies_the_agreed_basis_when_the_working_file_has_changed()
    {
        Put(A, Mine);
        var withBasis = new HexLinkHoldService(folder, store)
        {
            AgreedBasis = (_, _) => Task.FromResult<HexLinkHoldBasis?>(new(Bytes(Basis), HexLinkHoldBaseSource.Agreed)),
        };
        var held = await withBasis.Hold(A);
        Assert.Equal(HexLinkHoldBaseSource.Agreed, held.BaseSource);
        Assert.Equal(Bytes(Basis), store.Read(A)!.BaseBytes);

        Put(B, "working fallback");
        Assert.Equal(HexLinkHoldBaseSource.Local, (await service.Hold(B)).BaseSource);
        Assert.Equal(Bytes("working fallback"), store.Read(B)!.BaseBytes);
    }

    [Fact]
    public async Task Plan_send_mine_changes_nothing_and_submit_releases_it()
    {
        PutHeld(A, Basis, Mine);
        var plan = Assert.Single(await service.Plan());
        Assert.Equal(HexLinkHoldOutcome.SendMine, plan.Outcome);
        Assert.Equal(Mine, Get(A));
        Assert.True(store.IsHeld(A));
        Assert.Equal(A, Assert.Single((await service.Submit()).Paths));
        Assert.False(store.IsHeld(A));
    }

    [Fact]
    public async Task Plan_takes_theirs_when_mine_is_the_base()
    {
        PutHeld(A, Basis, Basis, Theirs);
        Assert.Equal(HexLinkHoldOutcome.TakeTheirs, Assert.Single(await service.Plan()).Outcome);
        await service.Submit();
        Assert.Equal(Theirs, Get(A));
    }

    [Fact]
    public async Task Plan_merges_disjoint_changes()
    {
        PutHeld(A, Basis, Mine, Theirs);
        var plan = Assert.Single(await service.Plan());
        Assert.Equal(HexLinkHoldOutcome.MergedClean, plan.Outcome);
        Assert.Equal(Bytes(Combined), plan.Result);
        Assert.Equal(Mine, Get(A));
        await service.Submit();
        Assert.Equal(Combined, Get(A));
    }

    [Fact]
    public async Task Plan_produces_one_merge_session_for_overlapping_changes()
    {
        PutHeld(A, "base\n", "mine\n", "theirs\n");
        var plan = Assert.Single(await service.Plan());
        Assert.Equal(HexLinkHoldOutcome.NeedsEditor, plan.Outcome);
        Assert.Equal(1, plan.Session!.Model.ConflictCount);
        await Assert.ThrowsAsync<HexLinkException>(() => plan.Session.Complete(Bytes("stale preview")));
        Assert.Equal("mine\n", Get(A));
        Assert.True(store.IsHeld(A));
    }

    [Theory]
    [InlineData("mine", "mine\n")]
    [InlineData("theirs", "theirs\n")]
    [InlineData("merged", "mine and theirs\n")]
    public async Task An_agent_reads_the_waiting_conflict_as_text_and_settles_it_without_a_merge_editor(string resolution, string expected)
    {
        PutHeld(A, "base\n", "mine\n", "theirs\n");
        Assert.Empty(service.PendingConflicts());
        Assert.Single((await service.Update()).NeedsMerge);

        var waiting = Assert.Single(service.PendingConflicts([A]));
        var region = Assert.Single(waiting.Session!.Model.Conflicts);
        Assert.Equal(["mine"], region.MineLines);
        Assert.Equal(["theirs"], region.TheirsLines);
        Assert.Equal(["base"], region.BaseLines);
        var shown = Hex.Dawn.DawnScene.LinkHoldConflictJson(waiting);
        Assert.Equal("mine", shown["regions"]![0]!["mine"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(1, shown["regions"]![0]!["mine"]![0]!["line"]!.GetValue<int>());

        var done = await service.ResolveMerge(A, resolution, resolution == "merged" ? "mine and theirs\n" : null);
        Assert.Equal(A, Assert.Single(done.Paths));
        Assert.Equal(expected, Get(A));
        Assert.Empty(service.PendingConflicts());
        await Assert.ThrowsAsync<HexLinkException>(() => service.ResolveMerge(A, "mine", null));
    }

    [Fact]
    public async Task A_submit_after_a_resolved_merge_reports_the_paths_that_merge_sent()
    {
        PutHeld(A, "base\n", "mine\n", "theirs\n");
        Assert.Single((await service.Submit()).NeedsMerge);
        var resolved = await service.ResolveMerge(A, "mine", null);
        Assert.Equal(A, Assert.Single(resolved.Paths));
        Assert.False(store.IsHeld(A));

        Assert.Equal(A, Assert.Single((await service.Submit()).Paths));
        Assert.Empty((await service.Submit()).Paths);
    }

    [Fact]
    public void A_relative_path_is_placed_in_the_folder_that_has_the_file_never_the_first()
    {
        string first = Directory.CreateDirectory(Path.Combine(root, "first")).FullName, second = Directory.CreateDirectory(Path.Combine(root, "second", "UI")).FullName;
        File.WriteAllText(Path.Combine(second, "Hud.cs"), "x");
        File.WriteAllText(Path.Combine(first, "Both.cs"), "x");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(second)!, "Both.cs"), "x");
        var workspace = new HexLinkWorkspace("Test", [new("first", first), new("second", Path.GetDirectoryName(second)!)], []);

        Assert.Equal("second", Hex.Dawn.DawnScene.LinkHoldFolderFor(workspace, _ => null, ["UI/Hud.cs"], out _)!.Name);
        Assert.Equal("second", Hex.Dawn.DawnScene.LinkHoldFolderFor(workspace, _ => null, ["second/UI/Hud.cs"], out _)!.Name);
        Assert.Equal("second", Hex.Dawn.DawnScene.LinkHoldFolderFor(workspace, _ => null, [Path.Combine(second, "Hud.cs")], out _)!.Name);

        Assert.Null(Hex.Dawn.DawnScene.LinkHoldFolderFor(workspace, _ => null, ["Missing.cs"], out string? missing));
        Assert.Contains("none of the workspace's folders", missing);
        Assert.Null(Hex.Dawn.DawnScene.LinkHoldFolderFor(workspace, _ => null, ["Both.cs"], out string? ambiguous));
        Assert.Contains("first, second", ambiguous);
        Assert.Null(Hex.Dawn.DawnScene.LinkHoldFolderFor(workspace, _ => null, ["UI/Hud.cs", "first/Both.cs"], out string? apart));
        Assert.Contains("different folders", apart);
    }

    [Fact]
    public async Task Plan_distinguishes_deletion_from_empty_bytes_and_accepts_deletion_as_a_side()
    {
        PutHeld(A, "base\n", "mine\n");
        store.SetIncomingDeleted(A, "Peer");
        var conflict = Assert.Single((await service.Submit()).NeedsMerge);
        Assert.Equal(HexLinkHoldOutcome.DeleteConflict, conflict.Outcome);
        Assert.True(conflict.Session!.SideChoices!.TheirsDeleted);
        await conflict.Session.SideChoices.Complete(MergeSide.Theirs);
        Assert.False(File.Exists(Full(A)));
        Assert.False(store.IsHeld(A));
    }

    [Fact]
    public async Task Plan_binary_changes_choose_a_side_without_text_merging()
    {
        byte[] basis = [0, 1], mine = [0, 2], theirs = [0, 3];
        store.Hold(A, basis, HexLinkHoldBaseSource.Agreed);
        File.WriteAllBytes(Full(A), mine);
        store.SetIncoming(A, theirs, "Peer");
        var conflict = Assert.Single((await service.Submit()).NeedsMerge);
        Assert.Equal(HexLinkHoldOutcome.Binary, conflict.Outcome);
        Assert.True(conflict.Session!.IsBinary);
        await conflict.Session.SideChoices!.Complete(MergeSide.Theirs);
        Assert.Equal(theirs, File.ReadAllBytes(Full(A)));
        Assert.False(store.IsHeld(A));
    }

    [Fact]
    public async Task A_multi_file_submit_releases_after_the_last_conflict_is_resolved()
    {
        PutHeld(A, Basis, Mine, Theirs);
        PutHeld(B, "base\n", "first mine\n", "first theirs\n");
        PutHeld(C, "base\n", "second mine\n", "second theirs\n");
        var pending = await service.Submit();
        Assert.Empty(pending.Paths);
        Assert.Equal(2, pending.NeedsMerge.Count);
        Assert.Equal(3, store.Count);
        Assert.Equal(Mine, Get(A));

        var first = await service.CompleteMerge(B, Bytes("first resolved\n"));
        Assert.Single(first.NeedsMerge);
        Assert.Equal("first mine\n", Get(B));
        Assert.Equal(3, store.Count);
        int events = 0;
        store.Released += (files, _) =>
        {
            Assert.Equal(3, files.Count);
            events++;
        };
        await pending.NeedsMerge.Single(file => file.Path == C).Session!.Complete(Bytes("second resolved\n"));
        Assert.Equal(1, events);
        Assert.Empty(store.List());
        Assert.Equal(Combined, Get(A));
        Assert.Equal("first resolved\n", Get(B));
        Assert.Equal("second resolved\n", Get(C));
    }

    [Fact]
    public async Task Partial_submit_releases_clean_files_while_conflicts_stay_held()
    {
        PutHeld(A, Basis, Mine, Theirs);
        PutHeld(B, "base\n", "mine\n", "theirs\n");
        var result = await service.Submit(partial: true);
        Assert.Equal(A, Assert.Single(result.Paths));
        Assert.Equal(B, Assert.Single(result.NeedsMerge).Path);
        Assert.False(store.IsHeld(A));
        Assert.True(store.IsHeld(B));
        Assert.Equal(Combined, Get(A));
    }

    [Fact]
    public async Task Cancelling_A_merge_leaves_the_batch_held_and_working_files_unchanged()
    {
        PutHeld(A, Basis, Mine, Theirs);
        PutHeld(B, "base\n", "mine\n", "theirs\n");
        var pending = await service.Submit();
        var session = Assert.Single(pending.NeedsMerge).Session!;
        session.Cancel();
        await Assert.ThrowsAsync<HexLinkException>(() => session.Complete(Bytes("cancelled")));
        Assert.Equal(2, store.Count);
        Assert.Equal(Mine, Get(A));
        Assert.Equal("mine\n", Get(B));
    }

    [Fact]
    public async Task Failed_gate_sees_the_candidate_and_restores_every_working_file()
    {
        PutHeld(A, Basis, Mine, Theirs);
        PutHeld(B, "base\n", "base\n", "incoming\n");
        var checkedService = CheckedService("cat a.txt; cat b.txt; exit 17");
        var result = await checkedService.Submit();
        Assert.False(result.Passed);
        Assert.Equal(17, result.Gate!.ExitCode);
        Assert.Contains("E", result.Gate.Output);
        Assert.Contains("incoming", result.Gate.Output);
        Assert.Empty(result.Paths);
        Assert.Equal(2, store.Count);
        Assert.Equal(Mine, Get(A));
        Assert.Equal("base\n", Get(B));
    }

    [Fact]
    public async Task Submit_anyway_reuses_every_exact_resolution_after_the_gate_failed()
    {
        PutHeld(A, Basis, Mine, Theirs);
        PutHeld(B, "base\n", "local first\n", "incoming first\n");
        PutHeld(C, "base\n", "local last\n", "incoming last\n");
        var pending = await CheckedService("cat b.txt; cat c.txt; exit 19").Submit();
        var first = pending.NeedsMerge.Single(file => file.Path == B).Session!;
        var last = pending.NeedsMerge.Single(file => file.Path == C).Session!;
        await first.Complete(Bytes("typed first\r\n"));
        byte[] typed = Bytes("typed last without a newline");
        MergeCheckFailed failed = await Assert.ThrowsAsync<MergeCheckFailed>(() => last.Complete(typed));

        Assert.Contains("typed first", failed.Message);
        Assert.Contains("typed last without a newline", failed.Message);
        Assert.Equal(3, store.Count);
        Assert.Equal(Mine, Get(A));
        Assert.Equal("local first\n", Get(B));
        Assert.Equal("local last\n", Get(C));

        await failed.OverrideCheck();

        Assert.Empty(store.List());
        Assert.Equal(Combined, Get(A));
        Assert.Equal(Bytes("typed first\r\n"), File.ReadAllBytes(Full(B)));
        Assert.Equal(typed, File.ReadAllBytes(Full(C)));
    }

    [Fact]
    public async Task Submit_anyway_skips_the_configured_check()
    {
        PutHeld(A, Basis, Mine, Theirs);
        var result = await CheckedService("exit 19").Submit(check: false);
        Assert.Null(result.Gate);
        Assert.Equal(A, Assert.Single(result.Paths));
        Assert.Empty(store.List());
        Assert.Equal(Combined, Get(A));
    }

    [Fact]
    public async Task Update_stays_held_sets_the_new_base_and_submit_sends_local_text()
    {
        PutHeld(A, Basis, Mine, Theirs);
        var result = await service.Update([A]);
        Assert.Equal(A, Assert.Single(result.Paths));
        Assert.True(store.IsHeld(A));
        Assert.False(store.Get(A)!.HasIncoming);
        Assert.Equal(Bytes(Theirs), store.Read(A)!.BaseBytes);
        Assert.Equal(Combined, Get(A));
        Assert.Equal(HexLinkHoldOutcome.SendMine, Assert.Single(await service.Plan()).Outcome);
        await service.Submit();
        Assert.Empty(store.List());
        Assert.Equal(Combined, Get(A));
    }

    [Fact]
    public async Task Update_conflict_completion_preserves_the_hold()
    {
        PutHeld(A, "base\n", "mine\n", "theirs\n");
        var result = await service.Update();
        await Assert.Single(result.NeedsMerge).Session!.Complete(Bytes("resolved\n"));
        Assert.Equal("resolved\n", Get(A));
        Assert.Equal(Bytes("theirs\n"), store.Read(A)!.BaseBytes);
        Assert.False(store.Get(A)!.HasIncoming);
    }

    [Fact]
    public async Task Submit_merges_only_the_latest_of_two_incoming_versions()
    {
        PutHeld(A, Basis, Mine, "a\nb\nc\nd\nold incoming\n");
        store.SetIncoming(A, Bytes(Theirs), "Other peer");
        await service.Submit();
        Assert.Equal(Combined, Get(A));
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task Discard_restores_incoming_or_base_and_backs_up_the_private_versions()
    {
        PutHeld(A, Basis, Mine, Theirs);
        PutHeld(B, "base\n", "local only\n");
        PutHeld(C, "base\n", "edited before deletion\n");
        store.SetIncomingDeleted(C, "Peer");
        var result = await service.Discard();
        Assert.Equal(3, result.Paths.Count);
        Assert.Equal(Theirs, Get(A));
        Assert.Equal("base\n", Get(B));
        Assert.False(File.Exists(Full(C)));
        Assert.Empty(store.List());
        Assert.Equal(Mine, File.ReadAllText(Path.Combine(result.DiscardedDirectory!, "a.txt")));
        Assert.Equal("local only\n", File.ReadAllText(Path.Combine(result.DiscardedDirectory!, "b.txt")));
        Assert.Equal("edited before deletion\n", File.ReadAllText(Path.Combine(result.DiscardedDirectory!, "c.txt")));
    }

    [Fact]
    public async Task Work_edited_after_submit_plans_a_conflict_is_preserved()
    {
        PutHeld(A, "base\n", "mine\n", "theirs\n");
        var result = await service.Submit();
        Put(A, "saved after the plan\n");
        var stale = await Assert.ThrowsAsync<HexLinkException>(() =>
            Assert.Single(result.NeedsMerge).Session!.Complete(Bytes("resolved before the save\n")));
        Assert.Equal(HexLinkErrors.CONFLICT, stale.Code);
        Assert.Equal("saved after the plan\n", Get(A));
        Assert.True(store.IsHeld(A));
    }

    [Fact]
    public async Task Submission_uses_compare_and_replace_at_the_write_boundary()
    {
        PutHeld(A, Basis, Mine, Theirs);
        var racing = new HexLinkHoldService(folder, store)
        {
            Apply = (full, wire, hash, bytes, stop, mode) =>
            {
                Put(wire, "new local save\n");
                return HexLinkFiles.CompareAndReplace(full, wire, hash, bytes, stop, mode);
            },
        };
        var changed = await Assert.ThrowsAsync<HexLinkException>(() => racing.Submit());
        Assert.Equal(HexLinkErrors.CONFLICT, changed.Code);
        Assert.Equal("new local save\n", Get(A));
        Assert.True(store.IsHeld(A));
    }

    private HexLinkHoldService CheckedService(string command) =>
        new(folder, store) { GatePolicy = () => new(command, null, null, null, 15) };

    private void PutHeld(string wire, string basis, string mine, string? theirs = null)
    {
        store.Hold(wire, Bytes(basis), HexLinkHoldBaseSource.Agreed);
        Put(wire, mine);
        if (theirs is not null)
            store.SetIncoming(wire, Bytes(theirs), "Peer");
    }

    private string Full(string wire) => Path.Combine(folder.Path, wire["work/".Length..].Replace('/', Path.DirectorySeparatorChar));
    private void Put(string wire, string text) => File.WriteAllText(Full(wire), text);
    private string Get(string wire) => File.ReadAllText(Full(wire));
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    public void Dispose() => Directory.Delete(root, recursive: true);
}
