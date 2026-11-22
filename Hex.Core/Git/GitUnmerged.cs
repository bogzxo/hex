using Hex.Core.Merge;

namespace Hex.Core.Git;

internal sealed record GitIndexStage(string Mode, string ObjectId);
internal sealed record GitUnmergedFile(string Path, GitIndexStage? Base, GitIndexStage? Mine, GitIndexStage? Theirs);

/// <summary>The index stages and working file seen when a Git conflict is opened.</summary>
internal sealed class GitUnmerged
{
    private readonly GitRepository repository;
    private readonly string full;
    private readonly GitUnmergedFile? stages;
    private readonly byte[]? before;
    private readonly bool? wasExecutable;

    private GitUnmerged(GitRepository repository, string full, GitUnmergedFile? stages, byte[]? before)
    {
        this.repository = repository;
        this.full = full;
        this.stages = stages;
        this.before = before;
        if (before is not null && !OperatingSystem.IsWindows())
            wasExecutable = File.GetUnixFileMode(full).HasFlag(UnixFileMode.UserExecute);
    }

    public static IReadOnlyList<GitUnmergedFile> List(GitRepository repository) => ReadStages(repository, null);

    public static GitUnmergedFile? Read(GitRepository repository, string path) => ReadStages(repository, path).SingleOrDefault();

    public static async Task<MergeSession?> Open(GitRepository repository, string full, string? markedText = null,
        CancellationToken cancel = default)
    {
        string path = repository.Relative(full);
        if (Path.IsPathRooted(path) || path is ".." or "." || path.StartsWith("../", StringComparison.Ordinal))
            throw new IOException("The file is outside the repository.");

        GitUnmergedFile? stages = Read(repository, path);
        if (new FileInfo(full).LinkTarget is not null || Directory.Exists(full) ||
            stages is not null && new[] { stages.Base, stages.Mine, stages.Theirs }
                .Any(stage => stage is not null && stage.Mode is not ("100644" or "100755")))
            throw new IOException("Resolve this symbolic link, folder or submodule with Git.");

        byte[]? before = File.Exists(full) ? await File.ReadAllBytesAsync(full, cancel).ConfigureAwait(false) : null;
        var edit = new GitUnmerged(repository, full, stages, before);
        var (mineLabel, theirsLabel, incomingRef) = Labels(repository);
        var title = Path.GetFileName(full);
        if (stages is null)
        {
            var decoded = before is null ? null : HexLinkFiles.Decode(before);
            string? text = markedText ?? decoded?.Text;
            if (text is null)
                return null;

            MergeModel marked = MergeModel.FromMarkers(text, decoded?.Encoding ?? "utf-8");
            return marked.ConflictCount == 0 ? null :
                new(title, "Mine", "Theirs", "Base", marked, bytes => edit.Complete(bytes, null, cancel),
                    static () => { }, full);
        }

        byte[] basis = ReadBlob(repository, stages.Base, cancel);
        byte[] mine = ReadBlob(repository, stages.Mine, cancel);
        byte[] theirs = ReadBlob(repository, stages.Theirs, cancel);
        bool missingSide = stages.Mine is null || stages.Theirs is null;
        MergeModel model = missingSide ? MergeModel.FromTwo(mine, theirs) :
            await MergeModel.FromThree(basis, mine, theirs, cancel).ConfigureAwait(false);
        return new(title, mineLabel, theirsLabel, "Base", model,
            bytes => edit.Complete(bytes, Executable(stages.Mine), cancel), static () => { }, full)
        {
            MineModified = DateOf(repository, "HEAD"),
            TheirsModified = DateOf(repository, incomingRef),
            SideChoices = missingSide || model.IsBinary
                ? new(stages.Mine is null, stages.Theirs is null, side => side == MergeSide.Mine
                    ? edit.Complete(stages.Mine is null ? null : mine, Executable(stages.Mine), cancel)
                    : edit.Complete(stages.Theirs is null ? null : theirs, Executable(stages.Theirs), cancel))
                : null,
        };
    }

    private async Task Complete(byte[]? bytes, bool? executable, CancellationToken cancel)
    {
        string path = repository.Relative(full);
        CheckStages();
        string? next = await Replace(before is null ? null : HexLinkFiles.Hash(before), bytes, executable, cancel)
            .ConfigureAwait(false);
        try
        {
            CheckStages();
            string[] arguments = bytes is null ? ["--literal-pathspecs", "rm", "-f", "--", path] :
                ["--literal-pathspecs", "add", "--", path];
            GitResult staged = GitProcess.Run(repository.Root, arguments, cancel: cancel);
            if (!staged.Ok)
                throw new IOException(staged.Why);
        }
        catch (Exception failed)
        {
            try
            {
                await Replace(next, before, wasExecutable, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException or HexLinkException)
            {
                throw new IOException($"Git could not stage the result: {failed.Message} The working file could not be restored: {rollback.Message}", failed);
            }
            throw;
        }

        void CheckStages()
        {
            if (Read(repository, path) != stages)
                throw new IOException("Git's index changed. Open the merge editor again.");
        }
    }

    private Task<string?> Replace(string? hash, byte[]? bytes, bool? executable, CancellationToken cancel)
    {
        string path = repository.Relative(full);
        if (bytes is { Length: > HexLinkFiles.MOST_READ })
            return ReplaceLarge(bytes);
        return HexLinkFiles.CompareAndReplace(full, path, hash, bytes, cancel, executable);

        async Task<string?> ReplaceLarge(byte[] content)
        {
            using var stream = new MemoryStream(content, writable: false);
            return await HexLinkFiles.CompareAndReplace(full, path, hash, stream, content.LongLength,
                HexLinkFiles.Hash(content), cancel, executable).ConfigureAwait(false);
        }
    }

    private static bool? Executable(GitIndexStage? stage) => stage is null ? null : stage.Mode == "100755";

    private static byte[] ReadBlob(GitRepository repository, GitIndexStage? stage, CancellationToken cancel)
    {
        if (stage is null)
            return [];

        // Read the blob identified by ls-files. The stage names may change while a different command updates the index.
        using var output = new MemoryStream();
        GitResult result = GitProcess.Run(repository.Root, ["show", stage.ObjectId], cancel: cancel,
            standardOutput: output);
        return result.Ok ? output.ToArray() : throw new IOException(result.Why);
    }

    private static IReadOnlyList<GitUnmergedFile> ReadStages(GitRepository repository, string? path)
    {
        string[] arguments = path is null ? ["ls-files", "-u", "-z"] :
            ["--literal-pathspecs", "ls-files", "-u", "-z", "--", path];
        GitResult result = GitProcess.Run(repository.Root, arguments);
        if (!result.Ok)
            throw new IOException(result.Why);

        var files = new Dictionary<string, GitUnmergedFile>(StringComparer.Ordinal);
        foreach (string entry in result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int tab = entry.IndexOf('\t');
            string[] fields = entry[..tab].Split(' ');
            string name = entry[(tab + 1)..];
            if (path is not null && name != path)
                continue;
            var file = files.GetValueOrDefault(name) ?? new(name, null, null, null);
            var stage = new GitIndexStage(fields[0], fields[1]);
            files[name] = fields[2] switch
            {
                "1" => file with { Base = stage },
                "2" => file with { Mine = stage },
                "3" => file with { Theirs = stage },
                _ => file,
            };
        }
        return [.. files.Values];
    }

    private static (string Mine, string Theirs, string Ref) Labels(GitRepository repository)
    {
        string root = repository.Root;
        string branch = GitProcess.Run(root, ["symbolic-ref", "--quiet", "--short", "HEAD"]).Output.Trim();
        var (incoming, fallback) = repository.Operation() switch
        {
            GitOperation.Merging => ("MERGE_HEAD", "incoming branch"),
            GitOperation.Rebasing => ("REBASE_HEAD", "rebased commit"),
            GitOperation.CherryPicking => ("CHERRY_PICK_HEAD", "cherry-picked commit"),
            GitOperation.Reverting => ("REVERT_HEAD", "reverted commit"),
            _ => ("stash", "stash"),
        };
        if (incoming == "REBASE_HEAD")
        {
            // Stage 2 belongs to the branch being rebased onto. Stage 3 is the user's picked commit.
            branch = Describe(repository, "HEAD", OnTo(repository) ?? "HEAD");
            return ($"{branch} (onto)", CommitLabel(repository, incoming, fallback), incoming);
        }

        if (incoming == "stash")
            return ("Mine: working tree", "Theirs: stash", incoming);

        string other = incoming == "MERGE_HEAD" ? Describe(repository, incoming, incoming)
            : CommitLabel(repository, incoming, fallback);
        return ($"Mine: {(branch.Length == 0 ? "working tree" : branch)}",
            $"Theirs: {(other.Length > 0 ? other : fallback)}", incoming);
    }

    private static string? OnTo(GitRepository repository)
    {
        foreach (string state in new[] { "rebase-merge/onto", "rebase-apply/onto" })
        {
            GitResult found = GitProcess.Run(repository.Root, ["rev-parse", "--git-path", state]);
            string path = Path.Combine(repository.Root, found.Output.TrimEnd('\r', '\n'));
            if (found.Ok && File.Exists(path))
                return File.ReadAllText(path).Trim();
        }
        return null;
    }

    private static string Describe(GitRepository repository, string reference, string branchPoint)
    {
        GitResult branch = GitProcess.Run(repository.Root,
            ["for-each-ref", "--format=%(refname:short)", $"--points-at={branchPoint}", "refs/heads", "refs/remotes"]);
        if (branch.Ok && branch.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() is { } name)
            return name;
        return GitProcess.Run(repository.Root, ["show", "-s", "--format=%h %s", reference]).Output.Trim();
    }

    private static string CommitLabel(GitRepository repository, string reference, string fallback)
    {
        GitResult result = GitProcess.Run(repository.Root, ["show", "-s", "--format=%h %s", reference]);
        return result.Ok ? result.Output.Trim() : fallback;
    }

    private static DateTimeOffset? DateOf(GitRepository repository, string reference)
    {
        GitResult result = GitProcess.Run(repository.Root, ["show", "-s", "--format=%ct", reference]);
        return result.Ok && long.TryParse(result.Output.Trim(), out long epoch)
            ? DateTimeOffset.FromUnixTimeSeconds(epoch) : null;
    }
}
