using Hex.Core;
using Hex.Core.Git;

namespace Hex.Tests;

public sealed partial class HexLinkWorktreeTests
{
    [Fact]
    public async Task Worktree_safety_a_private_ignore_rule_does_not_publish_a_tracked_deletion()
    {
        if (HexLinkGit.Why() is not null)
            return;
        var (draft, tree, shared, _) = await SafetyDraft();
        File.WriteAllText(Path.Combine(draft.Root, HexLinkIgnore.FileName), "tracked.txt\n");

        var plan = await draft.Prepare(tree.Manifest(), CancellationToken.None);
        Assert.Empty(plan.Conflicts);
        Assert.Equal("work/" + HexLinkIgnore.FileName, Assert.Single(plan.Edits).Path);
        Assert.Equal("base\n", File.ReadAllText(Path.Combine(shared.Path, "tracked.txt")));
        Assert.Equal("base\n", File.ReadAllText(Path.Combine(draft.Root, "tracked.txt")));
        Assert.DoesNotContain("work/tracked.txt", (await draft.Status(CancellationToken.None)).Paths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worktree_safety_an_authority_ignore_rule_does_not_delete_or_conflict(bool editDraft)
    {
        if (HexLinkGit.Why() is not null)
            return;
        var (draft, tree, shared, _) = await SafetyDraft();
        string privateFile = Path.Combine(draft.Root, "tracked.txt");
        string privateText = editDraft ? "private edit\n" : "base\n";
        File.WriteAllText(privateFile, privateText);
        File.WriteAllText(Path.Combine(shared.Path, HexLinkIgnore.FileName), "tracked.txt\n");
        var snapshot = tree.Manifest();

        var plan = await draft.Prepare(snapshot, CancellationToken.None, (wire, _) =>
            File.ReadAllBytesAsync(HexLinkFiles.Resolve([shared], wire)));
        Assert.Empty(plan.Edits);
        Assert.Empty(plan.Conflicts);
        await draft.Follow(snapshot, CancellationToken.None);
        Assert.True(File.Exists(privateFile));
        Assert.Equal(privateText, File.ReadAllText(privateFile));
        Assert.Equal("base\n", File.ReadAllText(Path.Combine(shared.Path, "tracked.txt")));
    }

    [Fact]
    public async Task Worktree_safety_accept_advances_the_base_even_if_the_draft_has_a_newer_edit()
    {
        if (HexLinkGit.Why() is not null)
            return;
        var (draft, tree, shared, storage) = await SafetyDraft();
        string privateFile = Path.Combine(draft.Root, "tracked.txt");
        string authorityFile = Path.Combine(shared.Path, "tracked.txt");
        const string wire = "work/tracked.txt";
        var published = new HexLinkDraftPlan(
            [new(wire, HexLinkFiles.Hash(Bytes("base\n")), Bytes("published v1\n"))], [],
            new Dictionary<string, string?> { [wire] = HexLinkFiles.Hash(Bytes("checked draft\n")) });
        File.WriteAllText(privateFile, "private v2\n");
        File.WriteAllText(authorityFile, "published v1\n");

        await draft.Accept(published, CancellationToken.None);
        Guid id = Guid.ParseExact(Path.GetFileName(Path.GetDirectoryName(draft.Root)!), "N");
        var reopened = await HexLinkPrivateWorktree.Open(storage, id, shared, null, null, CancellationToken.None);
        Assert.Equal("private v2\n", File.ReadAllText(privateFile));
        var next = await reopened.Prepare(tree.Manifest(), CancellationToken.None);
        Assert.Empty(next.Conflicts);
        var edit = Assert.Single(next.Edits);
        Assert.Equal(HexLinkFiles.Hash(Bytes("published v1\n")), edit.Base);
        Assert.Equal(Bytes("private v2\n"), edit.Bytes);
    }

    [Fact]
    public async Task Worktree_safety_an_unreachable_authority_does_not_block_discard()
    {
        if (HexLinkGit.Why() is not null)
            return;
        var (draft, tree, shared, _) = await SafetyDraft();
        File.WriteAllText(Path.Combine(draft.Root, "tracked.txt"), "private\n");
        var offline = new HexLinkDraftRoute(_ => Task.FromResult(tree.Manifest()),
            (_, _) => throw new IOException("Authority offline."), (_, _, _, _) => throw new IOException("Authority offline."));
        await Assert.ThrowsAsync<IOException>(() => draft.Publish(offline, new HexLinkWorktreeGateOptions(), true, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(draft.Root)!, "pending.json")));

        await draft.Discard(CancellationToken.None);

        Assert.False(Directory.Exists(draft.Root));
        Assert.Equal("base\n", File.ReadAllText(Path.Combine(shared.Path, "tracked.txt")));
    }

    [Fact]
    public async Task Worktree_safety_retries_prepare_instead_of_replaying_a_stale_pending_plan()
    {
        if (HexLinkGit.Why() is not null)
            return;
        var (draft, tree, shared, _) = await SafetyDraft();
        string privateFile = Path.Combine(draft.Root, "tracked.txt");
        File.WriteAllText(privateFile, "private v1\n");
        Guid oldId = Guid.Empty;
        var disconnected = new HexLinkDraftRoute(_ => Task.FromResult(tree.Manifest()), (_, _) => Task.FromResult<HexLinkPublishReceipt?>(null),
            (id, _, _, _) => { oldId = id; throw new IOException("Connection lost."); });
        await Assert.ThrowsAsync<IOException>(() => draft.Publish(disconnected, new HexLinkWorktreeGateOptions(), true, CancellationToken.None));
        Assert.NotEqual(Guid.Empty, oldId);
        File.WriteAllText(privateFile, "private v2\n");
        var retried = disconnected with
        {
            Send = async (id, edits, _, cancel) =>
            {
                Assert.NotEqual(oldId, id);
                var edit = Assert.Single(edits);
                Assert.Equal(Bytes("private v2\n"), edit.Bytes);
                await HexLinkFiles.CompareAndReplace(Path.Combine(shared.Path, "tracked.txt"), edit.Path, edit.Base, edit.Bytes, cancel);
                return new HexLinkPublishReceipt(id, new string('a', 40), edits.Count);
            }
        };

        var result = await draft.Publish(retried, new HexLinkWorktreeGateOptions(), true, CancellationToken.None);
        Assert.NotNull(result.Receipt);
        Assert.Equal("private v2\n", File.ReadAllText(privateFile));
        Assert.Equal("private v2\n", File.ReadAllText(Path.Combine(shared.Path, "tracked.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worktree_safety_an_oversized_file_is_named_when_publication_is_refused(bool authority)
    {
        if (HexLinkGit.Why() is not null)
            return;
        var (draft, tree, shared, _) = await SafetyDraft();
        File.WriteAllText(Path.Combine(draft.Root, "tracked.txt"), "private\n");
        string large = Path.Combine(authority ? shared.Path : draft.Root, "tracked.txt");
        using (var output = File.Create(large))
            output.SetLength(HexLinkFiles.MOST_READ + 1L);

        var error = await Assert.ThrowsAsync<HexLinkException>(() => draft.Prepare(tree.Manifest(), CancellationToken.None));
        Assert.Equal(HexLinkErrors.TOO_BIG, error.Code);
        Assert.Contains("work/tracked.txt", error.Message);
    }

    [Fact]
    public async Task Worktree_safety_discard_reclaims_snapshot_objects_and_preserves_other_drafts()
    {
        if (HexLinkGit.Why() is not null)
            return;
        var (draft, tree, shared, storage) = await SafetyDraft();
        string oldCommit = Git(draft.Root, "rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(shared.Path, "tracked.txt"), "second snapshot\n");
        var other = await HexLinkPrivateWorktree.Open(storage, Guid.NewGuid(), shared, tree.Manifest(), null, CancellationToken.None);
        string activeCommit = Git(other.Root, "rev-parse", "HEAD");
        string objects = Assert.Single(Directory.GetDirectories(Path.Combine(storage, "objects")));

        await draft.Discard(CancellationToken.None);

        Assert.False(Directory.Exists(Path.GetDirectoryName(draft.Root)));
        Assert.False(GitProcess.Run(objects, ["cat-file", "-e", oldCommit]).Ok);
        Assert.Equal(activeCommit, Git(objects, "rev-parse", $"refs/hexlink/snapshots/{activeCommit}"));
        Assert.Equal("second snapshot\n", File.ReadAllText(Path.Combine(other.Root, "tracked.txt")));
        Assert.Empty((await other.Status(CancellationToken.None)).Paths);
    }

    [Fact]
    public async Task Worktree_safety_authority_refuses_a_deletion_if_a_rule_appears_after_prepare()
    {
        if (HexLinkGit.Why() is not null)
            return;
        var (draft, tree, shared, _) = await SafetyDraft();
        File.Delete(Path.Combine(draft.Root, "tracked.txt"));
        var plan = await draft.Prepare(tree.Manifest(), CancellationToken.None);
        Assert.Null(Assert.Single(plan.Edits).Bytes);
        File.WriteAllText(Path.Combine(shared.Path, HexLinkIgnore.FileName), "tracked.txt\n");

        using var services = new LinkServices(HexLinkTestKit.DeviceA);
        shared = shared with { Authority = new HexLinkAuthority(services.Device.Id, 1) };
        services.Workspace = services.Workspace with { Folders = [shared] };
        var publisher = new HexLinkWorktreePublication(services, Folder("publication"), _ => [],
            (_, _, _, _) => Task.CompletedTask, (_, _, _, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask);
        var route = Assert.IsType<HexLinkDraftRoute>(publisher.Route(shared, null));
        var error = await Assert.ThrowsAsync<HexLinkException>(() => route.Send(Guid.NewGuid(), plan.Edits, false, CancellationToken.None));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, error.Code);
        Assert.Equal("base\n", File.ReadAllText(Path.Combine(shared.Path, "tracked.txt")));
    }

    [Fact]
    public async Task Worktree_safety_a_later_draft_edit_keeps_authority_changes_merged_into_the_receipt()
    {
        if (HexLinkGit.Why() is not null)
            return;
        var (draft, tree, shared, storage) = await SafetyDraft("base one\nbase two\nbase three\nbase four\n");
        string draftPath = Path.Combine(draft.Root, "tracked.txt");
        string authorityPath = Path.Combine(shared.Path, "tracked.txt");
        File.WriteAllText(draftPath, "private v1\nbase two\nbase three\nbase four\n");
        File.WriteAllText(authorityPath, "base one\nbase two\nbase three\nauthority change\n");
        HexLinkPublishReceipt? receipt = null;
        var route = new HexLinkDraftRoute(_ => Task.FromResult(tree.Manifest()), (_, _) => Task.FromResult(receipt),
            async (id, edits, _, cancel) =>
            {
                var edit = Assert.Single(edits);
                Assert.Equal(Bytes("private v1\nbase two\nbase three\nauthority change\n"), edit.Bytes);
                await HexLinkFiles.CompareAndReplace(authorityPath, edit.Path, edit.Base, edit.Bytes, cancel);
                File.WriteAllText(draftPath, "private v2\nbase two\nbase three\nbase four\n");
                receipt = new HexLinkPublishReceipt(id, new string('a', 40), edits.Count);
                throw new IOException("The reply was lost.");
            });
        await Assert.ThrowsAsync<IOException>(() => draft.Publish(route, new HexLinkWorktreeGateOptions(), true, CancellationToken.None));
        Assert.NotNull(receipt);

        Guid id = Guid.ParseExact(Path.GetFileName(Path.GetDirectoryName(draft.Root)!), "N");
        var reopened = await HexLinkPrivateWorktree.Open(storage, id, shared, null, null, CancellationToken.None);
        var result = await reopened.Publish(route, new HexLinkWorktreeGateOptions(), true, CancellationToken.None);
        Assert.Equal(receipt, result.Receipt);
        Assert.StartsWith("private v2\n", File.ReadAllText(draftPath));
        reopened = await HexLinkPrivateWorktree.Open(storage, id, shared, null, null, CancellationToken.None);
        var next = await reopened.Prepare(tree.Manifest(), CancellationToken.None);
        Assert.Empty(next.Conflicts);
        Assert.Equal(Bytes("private v2\nbase two\nbase three\nauthority change\n"), Assert.Single(next.Edits).Bytes);
    }

    private async Task<(HexLinkPrivateWorktree Draft, HexLinkTree Tree, HexLinkFolder Shared, string Storage)> SafetyDraft(string contents = "base\n")
    {
        string root = Folder("shared");
        Git(root, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(root, "tracked.txt"), contents);
        Git(root, "add", "tracked.txt");
        Git(root, "-c", "user.name=Hex test", "-c", "user.email=test@example.invalid", "commit", "-qm", "Base");
        var shared = new HexLinkFolder("work", root);
        var tree = new HexLinkTree(shared);
        string storage = Folder("private");
        var draft = await HexLinkPrivateWorktree.Open(storage, Guid.NewGuid(), shared, tree.Manifest(), null, CancellationToken.None);
        return (draft, tree, shared, storage);
    }
}
