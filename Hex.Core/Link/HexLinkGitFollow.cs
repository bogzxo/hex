using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hex.Core.Git;

namespace Hex.Core;

internal sealed record HexLinkHeadTip(string Head, string? Branch)
{
    internal string? Ref => Branch is null ? null : "refs/heads/" + Branch;

    internal JsonObject ToJson() => new() { ["head"] = Head, ["branch"] = Branch };

    internal static HexLinkHeadTip? Of(JsonObject? data) =>
        HexLinkData.String(data, "head", 65) is { } head && HexLinkGit.IsCommitId(head)
            ? new(head, HexLinkData.String(data, "branch", 1024)) : null;
}

internal sealed record HexLinkHeadMove(string? Backup, bool Rewrite, string? Why = null);

internal static class HexLinkGitFollow
{
    private const string JOURNAL = "hexlink-head-follow.json";
    private const string BACKUPS = "refs/hexlink/backup/";
    private static readonly string disabledHooks = Path.Combine(Path.GetTempPath(), "hexlink-no-hooks-" + Guid.NewGuid().ToString("N"));

    internal static HexLinkHeadTip Read(string repository, CancellationToken cancel) =>
        new(HexLinkProvision.ReadHead(repository, cancel) ?? throw new InvalidOperationException("The repository has no HEAD commit."),
            HexLinkProvision.ReadBranch(repository, cancel));

    internal static void Import(string repository, string bundle, CancellationToken cancel)
    {
        Git(repository, ["bundle", "verify", "-q", bundle], cancel);
        Git(repository, ["bundle", "unbundle", bundle], cancel);
    }

    // earlier is a previously agreed leader state, held by this device. A new remote claim alone cannot authorize a rewrite.
    internal static async Task<HexLinkHeadMove> Move(string repository, HexLinkHeadTip expected, HexLinkHeadTip target,
        HexLinkHeadTip? earlier, CancellationToken cancel)
    {
        var fence = HexLinkPublicationFence.ForRoot(repository);
        string gitDirectory = await Task.Run(() =>
        {
            if (!PlatformPaths.Same(Git(repository, ["rev-parse", "--show-toplevel"], cancel).Trim(), repository))
                throw new InvalidOperationException("The selected folder does not identify its own Git checkout.");
            return Git(repository, ["rev-parse", "--absolute-git-dir"], cancel).Trim();
        }, cancel);
        string journal = Path.Combine(gitDirectory, JOURNAL);
        JsonObject? pending = ReadJournal(journal);
        Guid? recovery = null;
        if (pending is not null)
        {
            if (!Guid.TryParseExact(HexLinkData.String(pending, "id", 33), "N", out Guid id))
                throw new IOException("Hex Link's Git index recovery journal is damaged.");
            fence.HoldRecovery(id, HexLinkFenceKind.GitFollow);
            recovery = id;
        }

        await using var exclusive = await fence.EnterExclusive(HexLinkFenceKind.GitFollow, cancel, recovery: recovery);
        return await Task.Run(() =>
        {
            if (pending is not null)
            {
                Recover(repository, gitDirectory, journal, pending, exclusive.Stop);
                fence.ReleaseRecovery(recovery!.Value);
            }
            return Change(repository, gitDirectory, journal, fence, expected, target, earlier, exclusive.Stop);
        }, exclusive.Stop);
    }

    private static HexLinkHeadMove Change(string repository, string gitDirectory, string journal, HexLinkPublicationFence fence,
        HexLinkHeadTip expected, HexLinkHeadTip target, HexLinkHeadTip? earlier, CancellationToken cancel)
    {
        if (!HexLinkGit.IsCommitId(expected.Head) || !HexLinkGit.IsCommitId(target.Head))
            throw HexLinkException.Invalid("HEAD follow needs both exact Git commits.");
        if (target.Ref is { } reference)
            Git(repository, ["check-ref-format", reference], cancel);
        Git(repository, ["cat-file", "-e", target.Head + "^{commit}"], cancel);

        string index = Path.Combine(gitDirectory, "index"), locked = index + ".lock";
        Guid id = Guid.NewGuid();
        string alternate = Path.Combine(gitDirectory, "hexlink-index-" + id.ToString("N"));
        string backup = BACKUPS + id.ToString("N");
        bool keepForRecovery = false;
        FileStream stage = new(locked, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try
        {
            HexLinkHeadTip actual = Read(repository, cancel);
            if (actual == target)
                return new(null, false);
            if (actual != expected)
                return new(null, false, "HEAD changed here while Link fetched the objects.");

            bool rewrite = !Ancestor(repository, actual.Head, target.Head, cancel);
            if (rewrite && earlier != actual)
                return new(null, false, "This device has commits the other device lacks.");
            if (BusyGit(gitDirectory))
                return new(null, false, "A merge, rebase, or other Git operation is in progress.");
            GitResult staged = Run(repository, ["diff", "--cached", "--quiet", "--no-ext-diff", "HEAD", "--"], cancel);
            if (staged.Exit == 1)
                return new(null, false, "The checkout has staged changes. Finish or unstage them before following HEAD.");
            Check(staged);

            string? targetBefore = null;
            if (target.Ref is { } to)
            {
                if (actual.Ref == to)
                    targetBefore = actual.Head;
                else
                {
                    string worktrees = Git(repository, ["worktree", "list", "--porcelain", "-z"], cancel);
                    if (worktrees.Split('\0').Contains("branch " + to, StringComparer.Ordinal))
                        return new(null, false, $"Branch {target.Branch} is checked out in another worktree.");
                    GitResult at = Run(repository, ["rev-parse", "--verify", "--quiet", to], cancel);
                    if (at.Exit != 1)
                    {
                        Check(at);
                        targetBefore = at.Output.Trim();
                        if (!Ancestor(repository, targetBefore, target.Head, cancel))
                            return new(null, false, $"Branch {target.Branch} has local commits the other device lacks.");
                    }
                }
            }

            Git(repository, ["read-tree", target.Head], cancel, environment: new Dictionary<string, string> { ["GIT_INDEX_FILE"] = alternate });
            using (var prepared = File.OpenRead(alternate))
                prepared.CopyTo(stage);
            stage.Flush(flushToDisk: true);
            stage.Position = 0;
            string checksum = Convert.ToHexString(SHA256.HashData(stage));
            cancel.ThrowIfCancellationRequested();
            if (Read(repository, cancel) != actual)
                return new(null, false, "HEAD changed here while Link prepared the index.");

            HexLinkFiles.WriteAtomic(journal, Encoding.UTF8.GetBytes(new JsonObject
            {
                ["id"] = id.ToString("N"), ["old"] = actual.ToJson(), ["next"] = target.ToJson(), ["sha256"] = checksum
            }.ToJsonString()));
            keepForRecovery = true;
            // Once the journal is in place, finish the local commit even if the network request was cancelled.
            CommitRef(repository, actual, target, targetBefore, backup);
            stage.Dispose();
            File.Move(locked, index, overwrite: true);
            File.Delete(journal);
            keepForRecovery = false;
            return new(backup, rewrite);
        }
        finally
        {
            stage.Dispose();
            HexLinkGit.TryDelete(alternate);
            if (keepForRecovery)
                fence.HoldRecovery(id, HexLinkFenceKind.GitFollow);
            else
                HexLinkGit.TryDelete(locked);
        }
    }

    private static void CommitRef(string repository, HexLinkHeadTip old, HexLinkHeadTip target, string? targetBefore, string backup)
    {
        var transaction = new StringBuilder("start\n");
        transaction.Append("create ").Append(backup).Append(' ').Append(old.Head).Append('\n');
        if (target.Ref is { } to)
        {
            if (targetBefore is null)
                transaction.Append("create ").Append(to).Append(' ').Append(target.Head).Append('\n');
            else
                transaction.Append("update ").Append(to).Append(' ').Append(target.Head).Append(' ').Append(targetBefore).Append('\n');
            if (old.Ref != to)
                transaction.Append("option no-deref\nsymref-update HEAD ").Append(to).Append(old.Ref is null ? " oid " : " ref ")
                    .Append(old.Ref ?? old.Head).Append('\n');
        }
        else
            transaction.Append("option no-deref\nupdate HEAD ").Append(target.Head).Append(' ').Append(old.Head).Append('\n');

        transaction.Append("prepare\ncommit\n");
        Git(repository, ["update-ref", "-m", "Hex Link HEAD follow", "--stdin"], CancellationToken.None, transaction.ToString());
    }

    private static JsonObject? ReadJournal(string path) =>
        File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new IOException("Invalid Git index recovery journal.") : null;

    private static void Recover(string repo, string gitDirectory, string journal, JsonObject entry, CancellationToken cancel)
    {
        HexLinkHeadTip before = HexLinkHeadTip.Of(HexLinkData.Object(entry, "old")) ?? throw new IOException("Invalid Git index recovery journal.");
        HexLinkHeadTip after = HexLinkHeadTip.Of(HexLinkData.Object(entry, "next")) ?? throw new IOException("Invalid Git index recovery journal.");
        string checksum = HexLinkData.Need(entry, "sha256", 65);
        string index = Path.Combine(gitDirectory, "index"), locked = index + ".lock";
        HexLinkHeadTip current = Read(repo, cancel);
        bool pendingIndex = Matches(locked);
        if (current == after)
        {
            if (pendingIndex)
                File.Move(locked, index, overwrite: true);
            else if (!Matches(index))
                throw new IOException("Cannot recover the prepared Git index. The old HEAD is under refs/hexlink/backup.");
        }
        else if (current == before && pendingIndex)
            File.Delete(locked);
        else
            throw new IOException("HEAD or the Git index changed during recovery. The recovery journal was kept.");
        File.Delete(journal);

        bool Matches(string file)
        {
            if (!File.Exists(file))
                return false;
            using var bytes = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
            return checksum.Equals(Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.Ordinal);
        }
    }

    private static bool BusyGit(string gitDirectory) =>
        ((string[])["MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "REBASE_HEAD", "rebase-merge", "rebase-apply", "sequencer"])
            .Any(name => File.Exists(Path.Combine(gitDirectory, name)) || Directory.Exists(Path.Combine(gitDirectory, name)));

    internal static bool Ancestor(string repository, string before, string next, CancellationToken cancel)
    {
        GitResult result = Run(repository, ["merge-base", "--is-ancestor", before, next], cancel);
        if (result.Exit == 1)
            return false;
        Check(result);
        return true;
    }

    private static string Git(string repo, string[] args, CancellationToken cancel, string? input = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        GitResult result = Run(repo, args, cancel, input, environment);
        Check(result);
        return result.Output;
    }

    private static GitResult Run(string repo, string[] args, CancellationToken cancel, string? input = null,
        IReadOnlyDictionary<string, string>? environment = null) =>
        GitProcess.Run(repo, ["-c", "core.hooksPath=" + disabledHooks, "-c", "core.fsmonitor=false", .. args],
            input, unattended: true, environment: environment, cancel: cancel);

    private static void Check(GitResult result)
    {
        if (!result.Ok)
            throw new InvalidOperationException(HexLinkGit.ErrorOf(result));
    }
}
