using System.Text;

using Hex.Core;
using Hex.Core.Git;

namespace Hex.Tests;

public sealed class HexLinkWorktreeTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "hwt-" + Guid.NewGuid().ToString("N")[..12]);

    private string Folder(string name)
    {
        string folder = Path.Combine(scratch, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Git(string directory, params string[] args)
    {
        GitResult result = GitProcess.Run(directory, ["-c", "core.longpaths=true", "-c", "core.autocrlf=false", .. args]);
        Assert.True(result.Ok, result.Why);
        return result.Output.Trim();
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task A_detached_private_worktree_captures_exact_bytes_without_touching_the_shared_index_or_head()
    {
        if (HexLinkGit.Why() is not null)
            return;
        string shared = Folder("shared");
        Git(shared, "init", "-q", "-b", "shared");
        string tracked = Path.Combine(shared, "project.txt");
        File.WriteAllText(tracked, "committed\n");
        Git(shared, "add", "project.txt");
        Git(shared, "-c", "user.name=Hex test", "-c", "user.email=test@example.invalid", "commit", "-qm", "Original");
        string head = Git(shared, "rev-parse", "HEAD");
        string refs = Git(shared, "show-ref");
        byte[] index = File.ReadAllBytes(Path.Combine(shared, ".git", "index"));
        byte[] edited = Bytes("current working text\r\n");
        File.WriteAllBytes(tracked, edited);
        File.WriteAllBytes(Path.Combine(shared, "untracked.bin"), [0, 10, 255, 42]);

        var store = await HexLinkWorktreeGit.Open(Folder("objects"), CancellationToken.None);
        string snapshot = await store.Capture(shared, [new("project.txt"), new("untracked.bin")], CancellationToken.None);
        string draft = Path.Combine(scratch, "draft");
        await store.Checkout(snapshot, draft, CancellationToken.None);

        Assert.Equal(edited, File.ReadAllBytes(Path.Combine(draft, "project.txt")));
        Assert.Equal(new byte[] { 0, 10, 255, 42 }, File.ReadAllBytes(Path.Combine(draft, "untracked.bin")));
        File.WriteAllText(Path.Combine(draft, "project.txt"), "private");
        Assert.Equal(edited, File.ReadAllBytes(tracked));
        Assert.Equal(head, Git(shared, "rev-parse", "HEAD"));
        Assert.Equal(refs, Git(shared, "show-ref"));
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(shared, ".git", "index")));
    }

    [Fact]
    public async Task Merge_applies_disjoint_changes_and_keeps_overlaps_for_resolution()
    {
        if (HexLinkGit.Why() is not null)
            return;
        byte[] basis = Bytes("one\ntwo\nthree\nfour\n");
        var clean = await HexLinkWorktreeMerge.Merge(basis, Bytes("draft\ntwo\nthree\nfour\n"),
            Bytes("one\ntwo\nthree\nshared\n"), CancellationToken.None);
        Assert.False(clean.Conflict);
        Assert.Equal(Bytes("draft\ntwo\nthree\nshared\n"), clean.Bytes);

        var conflict = await HexLinkWorktreeMerge.Merge(basis, Bytes("draft\ntwo\nthree\nfour\n"),
            Bytes("shared\ntwo\nthree\nfour\n"), CancellationToken.None);
        Assert.True(conflict.Conflict);
        Assert.Contains("<<<<<<<", Encoding.UTF8.GetString(conflict.Bytes!));
    }

    [Fact]
    public async Task Binary_and_delete_modify_merges_keep_the_private_version()
    {
        byte[] draft = [0, 2];
        var binary = await HexLinkWorktreeMerge.Merge([0, 1], draft, [0, 3], CancellationToken.None);
        Assert.True(binary.Conflict);
        Assert.Equal(draft, binary.Bytes);
        var deleted = await HexLinkWorktreeMerge.Merge(Bytes("base"), null, Bytes("shared"), CancellationToken.None);
        Assert.True(deleted.Conflict);
        Assert.Null(deleted.Bytes);
    }

    [Fact]
    public async Task One_stale_base_refuses_the_entire_publish_set()
    {
        if (HexLinkGit.Why() is not null)
            return;
        var disk = new PublishDisk();
        disk.Files["work/a"] = Bytes("before a");
        disk.Files["work/b"] = Bytes("another editor won");
        HexLinkPublishJournal journal = await Journal(disk);
        Guid publish = Guid.NewGuid();
        await Assert.ThrowsAsync<HexLinkException>(() => journal.Publish(publish,
            [Edit("work/a", "before a", "after a"), Edit("work/b", "before b", "after b")], CancellationToken.None));
        Assert.Equal(Bytes("before a"), disk.Files["work/a"]);
        Assert.Equal(Bytes("another editor won"), disk.Files["work/b"]);
        Assert.Equal(0, disk.Writes);
        Assert.Null(await journal.Recover(publish, CancellationToken.None));
    }

    [Fact]
    public async Task A_committed_set_resumes_after_disconnect_and_acknowledges_only_once()
    {
        if (HexLinkGit.Why() is not null)
            return;
        var disk = new PublishDisk { InterruptAt = "work/b" };
        disk.Files["work/a"] = Bytes("before a");
        disk.Files["work/b"] = Bytes("before b");
        Guid publish = Guid.NewGuid();
        HexLinkPublishEdit[] edits = [Edit("work/a", "before a", "after a"), Edit("work/b", "before b", "after b")];
        HexLinkPublishJournal journal = await Journal(disk);
        await Assert.ThrowsAsync<IOException>(() => journal.Publish(publish, edits, CancellationToken.None));
        Assert.Equal(Bytes("after a"), disk.Files["work/a"]);
        Assert.Equal(Bytes("before b"), disk.Files["work/b"]);
        Assert.Equal(0, disk.Notices);

        disk.InterruptAt = null;
        journal = await Journal(disk);
        HexLinkPublishReceipt? receipt = await journal.Recover(publish, CancellationToken.None);
        Assert.NotNull(receipt);
        Assert.Equal(2, receipt.Files);
        Assert.Equal(Bytes("after b"), disk.Files["work/b"]);
        Assert.Equal(receipt, await journal.Publish(publish, edits, CancellationToken.None));
        Assert.Equal(2, disk.Writes);
        Assert.Equal(1, disk.Notices);
    }

    [Fact]
    public async Task A_projected_publication_retries_the_durable_notice_without_rewriting_files()
    {
        if (HexLinkGit.Why() is not null)
            return;
        var disk = new PublishDisk { InterruptNotice = true };
        disk.Files["work/a"] = Bytes("before");
        Guid id = Guid.NewGuid();
        var journal = await Journal(disk);

        await Assert.ThrowsAsync<IOException>(() => journal.Publish(id, [Edit("work/a", "before", "after")], CancellationToken.None));
        Assert.Equal(Bytes("after"), disk.Files["work/a"]);
        Assert.Equal(1, disk.Writes);
        Assert.Equal(0, disk.Notices);
        Assert.True(disk.RecoveryPending);

        disk.InterruptNotice = false;
        journal = await Journal(disk);
        var receipt = await journal.Recover(id, CancellationToken.None);
        Assert.NotNull(receipt);
        Assert.Equal(receipt, await journal.Recover(id, CancellationToken.None));
        Assert.Equal(1, disk.Writes);
        Assert.Equal(1, disk.Notices);
        Assert.False(disk.RecoveryPending);
    }

    [Fact]
    public async Task Nested_private_worktrees_reopen_and_prepare_changes_against_the_latest_shared_base()
    {
        if (HexLinkGit.Why() is not null)
            return;
        string shared = Folder("shared");
        Git(shared, "init", "-q", "-b", "shared");
        File.WriteAllText(Path.Combine(shared, ".gitignore"), "engine/\n");
        Git(shared, "add", ".");
        Git(shared, "-c", "user.name=Hex test", "-c", "user.email=test@example.invalid", "commit", "-qm", "Outer");
        string nested = Path.Combine(shared, "engine");
        Directory.CreateDirectory(nested);
        Git(nested, "init", "-q", "-b", "main");
        string target = Path.Combine(nested, "tool.txt");
        File.WriteAllText(target, "base one\nbase two\nbase three\nbase four\n");
        Git(nested, "add", ".");
        Git(nested, "-c", "user.name=Hex test", "-c", "user.email=test@example.invalid", "commit", "-qm", "Nested");

        var folder = new HexLinkFolder("work", shared);
        var tree = new HexLinkTree(folder);
        string storage = Folder("private");
        Guid id = Guid.NewGuid();
        HexLinkPrivateWorktree draft = await HexLinkPrivateWorktree.Open(storage, id, folder, tree.Manifest(), null, CancellationToken.None);
        Assert.Equal(0, (await draft.Status(CancellationToken.None)).Files);
        string own = Path.Combine(draft.Root, "engine", "tool.txt");
        File.WriteAllText(own, "draft one\nbase two\nbase three\nbase four\n");
        File.WriteAllText(target, "base one\nbase two\nbase three\nshared four\n");

        draft = await HexLinkPrivateWorktree.Open(storage, id, folder, null, null, CancellationToken.None);
        Assert.Equal(new[] { "work/engine/tool.txt" }, (await draft.Status(CancellationToken.None)).Paths);
        string incoming = Path.Combine(draft.Root, "engine", "incoming.txt");
        File.WriteAllText(Path.Combine(nested, "incoming.txt"), "incoming");
        draft.HoldIncoming = true;
        await draft.Follow(tree.Manifest(), CancellationToken.None);
        Assert.False(File.Exists(incoming));
        draft = await HexLinkPrivateWorktree.Open(storage, id, folder, null, null, CancellationToken.None);
        Assert.True(draft.HoldIncoming);
        draft.HoldIncoming = false;
        await draft.Follow(tree.Manifest(), CancellationToken.None);
        Assert.Equal("incoming", File.ReadAllText(incoming));
        Assert.StartsWith("draft one", File.ReadAllText(own));
        HexLinkDraftPlan plan = await draft.Prepare(tree.Manifest(), CancellationToken.None);
        Assert.Empty(plan.Conflicts);
        Assert.Equal(Bytes("draft one\nbase two\nbase three\nshared four\n"), Assert.Single(plan.Edits).Bytes);
        Assert.StartsWith("base one", File.ReadAllText(target));
        var fence = HexLinkPublicationFence.ForRoot(shared);
        int notices = 0;
        var receiver = new HexLinkPublishTarget(async (id, stop) => await fence.EnterExclusive(HexLinkFenceKind.Publication, stop, recovery: id),
            (wire, _) => Task.FromResult(HexLinkFiles.HashOf(HexLinkFiles.Resolve([folder], wire))),
            async (edit, stop) => await HexLinkFiles.CompareAndReplace(HexLinkFiles.Resolve([folder], edit.Path),
                edit.Path, edit.Base, edit.Bytes, stop),
            (_, _, _) => { notices++; return Task.CompletedTask; },
            (id, pending) => { if (pending) fence.HoldRecovery(id, HexLinkFenceKind.Publication); else fence.ReleaseRecovery(id); });
        var objects = await HexLinkWorktreeGit.Open(Folder("pub-obj"), CancellationToken.None);
        var journal = new HexLinkPublishJournal(Folder("pub-log"), objects, receiver);
        var route = new HexLinkDraftRoute(_ => Task.FromResult(tree.Manifest()), journal.Recover,
            (publication, edits, bypass, stop) =>
            {
                Assert.False(bypass);
                return journal.Publish(publication, edits, stop);
            });

        var blocked = await draft.Publish(route, new HexLinkWorktreeGateOptions(Build: "exit 7"), false, CancellationToken.None);
        Assert.Null(blocked.Receipt);
        Assert.NotNull(blocked.Gate);
        Assert.False(blocked.Gate.Passed);
        Assert.Equal(7, blocked.Gate.ExitCode);
        Assert.True(File.Exists(blocked.Gate.Log));
        Assert.StartsWith("base one", File.ReadAllText(target));
        Assert.Equal(0, notices);

        var passed = await draft.Publish(route, new HexLinkWorktreeGateOptions(Build: "exit 0"), false, CancellationToken.None);
        Assert.NotNull(passed.Receipt);
        Assert.True(passed.Gate?.Passed);
        Assert.Equal(plan.Edits[0].Bytes, File.ReadAllBytes(Path.Combine(draft.GateRoot, "engine", "tool.txt")));
        Assert.Equal(plan.Edits[0].Bytes, File.ReadAllBytes(target));
        Assert.Equal(plan.Edits[0].Bytes, File.ReadAllBytes(own));
        Assert.Empty((await draft.Status(CancellationToken.None)).Paths);
        Assert.Equal(1, notices);
        await draft.Discard(CancellationToken.None);
        Assert.False(Directory.Exists(draft.Root));
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task Scoped_drafts_keep_unopened_repositories_out_of_changes_until_they_are_opened()
    {
        if (HexLinkGit.Why() is not null)
            return;

        var folder = TwoRepositories();
        var tree = new HexLinkTree(folder);
        string storage = Folder("storage");
        Guid id = Guid.NewGuid();
        var progress = new List<HexLinkWorktreeProgress>();
        var rootOnly = new HashSet<string> { "" };
        var draft = await HexLinkPrivateWorktree.Open(storage, id, folder, tree.Manifest(), null,
            CancellationToken.None, rootOnly, progress.Add);
        Assert.Equal("", Assert.Single(draft.MaterializedRepositories));
        Assert.Contains("engine", draft.RepositoryPaths);
        Assert.Equal(1, Assert.Single(progress).Total);
        Assert.False(Directory.Exists(Path.Combine(draft.Root, "engine")));
        Assert.Empty((await draft.Status(CancellationToken.None)).Paths);

        File.WriteAllText(Path.Combine(draft.Root, "root.txt"), "my private edit");
        File.WriteAllText(Path.Combine(folder.Path, "engine", "engine.txt"), "current shared bytes");
        var reused = await HexLinkPrivateWorktree.Open(storage, id, folder, null, null, CancellationToken.None);
        await reused.Follow(tree.Manifest(), CancellationToken.None);
        Assert.False(Directory.Exists(Path.Combine(reused.Root, "engine")));
        await reused.EnsureRepositories(new HashSet<string> { "engine" }, tree.Manifest(), null, CancellationToken.None);
        Assert.Equal(draft.Root, reused.Root);
        Assert.Equal("current shared bytes", File.ReadAllText(Path.Combine(reused.Root, "engine", "engine.txt")));
        Assert.False(Directory.Exists(Path.Combine(reused.Root, "engine", "obj")));
        Assert.Equal("work/root.txt", Assert.Single((await reused.Status(CancellationToken.None)).Paths));

        var reopened = await HexLinkPrivateWorktree.Open(storage, id, folder, null, null, CancellationToken.None);
        Assert.Contains("engine", reopened.MaterializedRepositories);
        Assert.Equal("my private edit", File.ReadAllText(Path.Combine(reopened.Root, "root.txt")));
        await reopened.Discard(CancellationToken.None);
    }

    [Fact]
    public async Task Cancellation_during_a_nested_checkout_removes_the_partial_draft()
    {
        if (HexLinkGit.Why() is not null)
            return;

        var folder = TwoRepositories();
        string storage = Folder("storage");
        Guid id = Guid.NewGuid();
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HexLinkPrivateWorktree.Open(storage, id,
            folder, new HexLinkTree(folder).Manifest(), null, stop.Token, progress: step =>
            {
                if (step.Number == 2)
                    stop.Cancel();
            }));
        string data = Path.Combine(storage, "drafts", id.ToString("N"));
        Assert.False(Directory.Exists(Path.Combine(data, "tree")));
        Assert.False(File.Exists(Path.Combine(data, "setup.json")));
        Assert.Null(HexLinkPrivateWorktree.RecordedFolder(storage, id));
        Assert.Equal("engine", File.ReadAllText(Path.Combine(folder.Path, "engine", "engine.txt")));
    }

    [Fact]
    public async Task Startup_recovers_setup_markers_without_removing_a_committed_private_tree()
    {
        if (HexLinkGit.Why() is not null)
            return;

        var folder = TwoRepositories();
        string storage = Folder("storage");
        Guid readyId = Guid.NewGuid();
        var ready = await HexLinkPrivateWorktree.Open(storage, readyId, folder, new HexLinkTree(folder).Manifest(),
            null, CancellationToken.None);
        string readyData = Path.GetDirectoryName(ready.Root)!;
        string interrupted = Path.Combine(storage, "drafts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(interrupted, "tree"));
        File.WriteAllText(Path.Combine(interrupted, "tree", "partial.txt"), "incomplete checkout");
        var marker = new System.Text.Json.Nodes.JsonObject
        {
            ["source"] = folder.Path,
            ["repositories"] = new System.Text.Json.Nodes.JsonArray("", "engine")
        };
        File.WriteAllText(Path.Combine(readyData, "setup.json"), marker.ToJsonString());
        File.WriteAllText(Path.Combine(interrupted, "setup.json"), marker.ToJsonString());
        await HexLinkPrivateWorktree.RecoverSetups(storage, CancellationToken.None);
        Assert.False(Directory.Exists(Path.Combine(interrupted, "tree")));
        Assert.False(File.Exists(Path.Combine(readyData, "setup.json")));
        Assert.Equal("engine", File.ReadAllText(Path.Combine(ready.Root, "engine", "engine.txt")));
        await ready.Discard(CancellationToken.None);
    }

    [Fact]
    public void The_configured_hex_gate_opens_the_engine_and_hidl_plus_requested_repositories()
    {
        string[] layout = ["", "hex", "dusk", "horizon", "dusk/horizon", "hex/hex-extensions", "horizon/Horizon.HIDL"];
        var selected = HexLinkWorktreeScope.Initial(layout, "hex/docs/_linkpass/proj", ["dusk/horizon"]);
        Assert.True(selected.SetEquals(["", "hex", "horizon", "horizon/Horizon.HIDL", "dusk", "dusk/horizon"]));
        Assert.True(HexLinkWorktreeScope.Initial(layout, null, []).SetEquals(layout));
        Assert.Throws<IOException>(() => HexLinkWorktreeScope.Initial(layout, "hex", ["../outside"]));
    }

    private HexLinkFolder TwoRepositories()
    {
        string shared = Folder("shared");
        Git(shared, "init", "-q", "-b", "shared");
        File.WriteAllText(Path.Combine(shared, ".gitignore"), "engine/\n");
        File.WriteAllText(Path.Combine(shared, "root.txt"), "root");
        Git(shared, "add", ".");
        Git(shared, "-c", "user.name=Hex test", "-c", "user.email=test@example.invalid", "commit", "-qm", "Outer");

        string nested = Directory.CreateDirectory(Path.Combine(shared, "engine")).FullName;
        Git(nested, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(nested, "engine.txt"), "engine");
        File.WriteAllText(Path.Combine(nested, ".gitignore"), "obj/\n");
        Git(nested, "add", ".");
        Git(nested, "-c", "user.name=Hex test", "-c", "user.email=test@example.invalid", "commit", "-qm", "Nested");
        Directory.CreateDirectory(Path.Combine(nested, "obj"));
        File.WriteAllText(Path.Combine(nested, "obj", "ignored.txt"), "build output");
        return new HexLinkFolder("work", shared);
    }

    [Fact]
    public async Task A_scoped_publish_ignores_unrelated_authority_changes_and_retries_moving_inputs()
    {
        if (HexLinkGit.Why() is not null)
            return;

        string replica = Folder("replica"), authority = Folder("authority");
        foreach (string path in new[] { replica, authority })
        {
            Git(path, "init", "-q", "-b", "main");
            Directory.CreateDirectory(Path.Combine(path, "src"));
            File.WriteAllText(Path.Combine(path, ".gitignore"), "bin/\nobj/\n");
            File.WriteAllText(Path.Combine(path, "src", "Fixture.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(path, "src", "LineEnds.cs"), "public class LineEnds {}" + (path == replica ? "\r\n" : "\n"));
            File.WriteAllText(Path.Combine(path, "src", "Input.cs"), $"public class {(path == replica ? "Old" : "BeforeRetry")} {{}}\n");
            Git(path, "add", ".");
            Git(path, "-c", "user.name=Hex test", "-c", "user.email=test@example.invalid", "commit", "-qm", "Fixture");
        }
        Directory.CreateDirectory(Path.Combine(authority, "unrelated"));
        File.WriteAllText(Path.Combine(authority, "unrelated", ".DS_Store"), "Finder data only on the authority");
        var local = new HexLinkFolder("work", replica);
        var remote = new HexLinkFolder("work", authority);
        var draft = await HexLinkPrivateWorktree.Open(Folder("storage"), Guid.NewGuid(), local,
            new HexLinkTree(local).Manifest(), null, CancellationToken.None);
        File.WriteAllText(Path.Combine(draft.Root, "src", "Publication.cs"), "public class Publication {}\n");

        var remoteTree = new HexLinkTree(remote);
        string input = Path.Combine(authority, "src", "Input.cs");
        int snapshots = 0, reads = 0;
        var route = new HexLinkDraftRoute(_ =>
        {
            File.WriteAllText(Path.Combine(authority, "unrelated", "moving.txt"), (++snapshots).ToString());
            return Task.FromResult(remoteTree.Manifest(verify: true));
        }, (_, _) => Task.FromResult<HexLinkPublishReceipt?>(null),
        (publication, edits, _, _) =>
        {
            var edit = Assert.Single(edits);
            Assert.Equal("work/src/Publication.cs", edit.Path);
            File.WriteAllBytes(HexLinkFiles.Resolve([remote], edit.Path), edit.Bytes!);
            return Task.FromResult(new HexLinkPublishReceipt(publication, new string('a', 40), 1));
        }, (wire, _) =>
        {
            Assert.Equal("work/src/Input.cs", wire);
            if (++reads == 1)
                File.WriteAllText(input, "public class AfterRetry {}\n");
            return File.ReadAllBytesAsync(input);
        });

        var options = new HexLinkWorktreeGateOptions(
            Build: "dotnet build Fixture.csproj -m:1 -v:q -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false",
            WorkingFolder: "src");
        var result = await draft.Publish(route, options, false, CancellationToken.None);
        Assert.True(result.Gate!.Passed, result.Gate.Output);
        Assert.NotNull(result.Receipt);
        Assert.Equal(3, snapshots);
        Assert.Equal(2, reads);
        Assert.Equal("public class AfterRetry {}\n", File.ReadAllText(Path.Combine(draft.GateRoot, "src", "Input.cs")));
        Assert.Equal("public class LineEnds {}\r\n", File.ReadAllText(Path.Combine(draft.GateRoot, "src", "LineEnds.cs")));
        Assert.Equal("public class Publication {}\n", File.ReadAllText(Path.Combine(authority, "src", "Publication.cs")));
    }

    [Fact]
    public async Task Recovery_never_overwrites_a_newer_external_edit()
    {
        if (HexLinkGit.Why() is not null)
            return;
        var disk = new PublishDisk { InterruptAt = "work/b" };
        disk.Files["work/a"] = Bytes("before a");
        disk.Files["work/b"] = Bytes("before b");
        Guid publication = Guid.NewGuid();
        var journal = await Journal(disk);
        await Assert.ThrowsAsync<IOException>(() => journal.Publish(publication,
            [Edit("work/a", "before a", "after a"), Edit("work/b", "before b", "after b")], CancellationToken.None));

        disk.InterruptAt = null;
        disk.Files["work/b"] = Bytes("new external content");
        await Assert.ThrowsAsync<HexLinkException>(() => journal.Recover(publication, CancellationToken.None));
        Assert.Equal(Bytes("new external content"), disk.Files["work/b"]);
        Assert.Equal(1, disk.Writes);
        Assert.Equal(0, disk.Notices);
    }

    private async Task<HexLinkPublishJournal> Journal(PublishDisk disk)
    {
        var store = await HexLinkWorktreeGit.Open(Folder("objects"), CancellationToken.None);
        return new HexLinkPublishJournal(Folder("journal"), store, new HexLinkPublishTarget(
            (_, stop) => disk.Fence(stop), disk.Hash, disk.Apply, disk.Announce, (_, pending) => disk.RecoveryPending = pending));
    }

    private static HexLinkPublishEdit Edit(string path, string before, string after) => new(path, HexLinkFiles.Hash(Bytes(before)), Bytes(after));

    private sealed class PublishDisk
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public string? InterruptAt { get; set; }
        public bool InterruptNotice { get; set; }
        public bool RecoveryPending { get; set; }
        public int Writes { get; private set; }
        public int Notices { get; private set; }
        private bool fenced;

        public ValueTask<IAsyncDisposable> Fence(CancellationToken cancel)
        {
            Assert.False(fenced);
            fenced = true;
            return ValueTask.FromResult<IAsyncDisposable>(new Scope(this));
        }

        public Task<string?> Hash(string path, CancellationToken cancel)
        {
            Assert.True(fenced);
            return Task.FromResult(Files.TryGetValue(path, out byte[]? bytes) ? HexLinkFiles.Hash(bytes) : null);
        }

        public Task Apply(HexLinkPublishEdit edit, CancellationToken cancel)
        {
            Assert.True(fenced);
            if (edit.Path == InterruptAt)
                throw new IOException("Disconnected during projection.");
            Assert.Equal(edit.Base, Files.TryGetValue(edit.Path, out byte[]? previous) ? HexLinkFiles.Hash(previous) : null);
            if (edit.Bytes is { } bytes)
                Files[edit.Path] = bytes;
            else
                Files.Remove(edit.Path);
            Writes++;
            return Task.CompletedTask;
        }

        public Task Announce(HexLinkPublishReceipt receipt, IReadOnlyList<HexLinkPublishEdit> applied, CancellationToken cancel)
        {
            Assert.True(fenced);
            Assert.Equal(receipt.Files, applied.Count);
            if (InterruptNotice)
                throw new IOException("Disconnected before the receipt was queued.");
            Notices++;
            return Task.CompletedTask;
        }

        private sealed class Scope(PublishDisk disk) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                disk.fenced = false;
                return ValueTask.CompletedTask;
            }
        }
    }

    public void Dispose()
    {
        if (!Directory.Exists(scratch))
            return;
        foreach (string file in Directory.EnumerateFiles(scratch, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(scratch, recursive: true);
    }
}
