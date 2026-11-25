namespace Hex.Core.Git;

/// <summary>
/// A repository on the disk, found from a file or a folder in it, and what Hex asks of it. Every call runs git and
/// waits, so it's all for a background thread (HexGit in Dawn does the asking and brings the answers back).
/// </summary>
internal sealed partial class GitRepository
{
    private static readonly string[] StatusArguments = ["status", "--porcelain=v2", "-z", "--branch", "--untracked-files=all", "--ignored=matching"];
    private static readonly string[] BranchesArguments = ["for-each-ref", "--sort=-committerdate", "--format=%(HEAD)%00%(refname)%00%(refname:short)%00%(upstream:short)%00%(committerdate:relative)%00%(subject)", "refs/heads", "refs/remotes"];
    private static readonly string[] RemoteArguments = ["remote"];
    private static readonly string[] HasCommitArguments = ["rev-parse", "--verify", "--quiet", "HEAD"];
    private static readonly string[] StageCommand = ["add", "--all", "--"];
    private static readonly string[] UnstageRestoreCommand = ["restore", "--staged", "--"];
    private static readonly string[] UnstageRemoveCommand = ["rm", "--cached", "-r", "-q", "--"];
    private static readonly string[] DiscardCommand = ["restore", "--worktree", "--"];
    private static readonly string[] CommitArguments = ["commit", "--file=-", "--cleanup=whitespace"];
    private static readonly string[] FetchArguments = ["fetch", "--all", "--prune", "--quiet"];
    private static readonly string[] PushQuietArguments = ["push", "--quiet"];

    // Coalesce concurrent in-flight read-only status/branch queries on the same repository root without caching completed results.
    private static readonly Lock InFlightGate = new();
    private static readonly Dictionary<string, Task<GitStatus?>> InFlightStatus = new(PlatformPaths.Comparer);
    private static readonly Dictionary<string, Task<IReadOnlyList<GitBranch>>> InFlightBranches = new(PlatformPaths.Comparer);

    /// <summary>The folder the working tree starts in, the one with .git in it.</summary>
    public string Root { get; }

    private GitRepository(string root) => Root = root;

    /// <summary>
    /// Detaches any in-flight read-only status or branch queries for <paramref name="root"/> so callers polling during
    /// or after a write or index mutation never join a pre-mutation query.
    /// </summary>
    internal static void InvalidateInFlight(string root)
    {
        lock (InFlightGate)
        {
            InFlightStatus.Remove(root);
            InFlightBranches.Remove(root);
        }
    }

    internal static T RunCoalesced<T>(Dictionary<string, Task<T>> table, string root, Func<T> execute)
    {
        Task<T>? existing;
        TaskCompletionSource<T>? leader = null;
        lock (InFlightGate)
        {
            if (!table.TryGetValue(root, out existing))
            {
                leader = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                table[root] = leader.Task;
            }
        }

        if (existing is not null)
            return existing.GetAwaiter().GetResult();

        try
        {
            T result = execute();
            leader!.TrySetResult(result);
            return result;
        }
        catch (Exception ex)
        {
            leader!.TrySetException(ex);
            throw;
        }
        finally
        {
            lock (InFlightGate)
            {
                if (table.TryGetValue(root, out Task<T>? current) && ReferenceEquals(current, leader!.Task))
                    table.Remove(root);
            }
        }
    }

    /// <summary>
    /// The repository a path is in, the nearest folder up from it with a .git (a folder, or a file in a submodule or a
    /// worktree), null for none or no git to ask.
    /// </summary>
    public static GitRepository? Find(string path)
    {
        if (!GitProcess.Enabled)
            return null;
        string? at = Directory.Exists(path) ? Path.GetFullPath(path) : Path.GetDirectoryName(Path.GetFullPath(path));
        while (at is not null)
        {
            string dot = Path.Combine(at, ".git");
            if (Directory.Exists(dot) || File.Exists(dot))
                return GitProcess.Installed ? new GitRepository(Path.TrimEndingDirectorySeparator(at)) : null;
            at = Path.GetDirectoryName(at);
        }

        return null;
    }

    /// <summary>A path in the repository the way git writes it, from the root with forward slashes.</summary>
    public string Relative(string path) => Path.GetRelativePath(Root, Path.GetFullPath(path)).Replace('\\', '/');

    /// <summary>The repository as it is now, null if git wouldn't say.</summary>
    public GitStatus? Status()
    {
        return RunCoalesced(InFlightStatus, Root, () =>
            GitProcess.Run(Root, StatusArguments, (exit, output, _) => exit == 0 ? GitStatus.Parse(Root, output) : null));
    }

    /// <summary>
    /// A file as git has it in the index, what the next commit would take (the same as the last commit until something's
    /// staged). What the editor's changes are measured against, the way VS Code does it. Null for a file git doesn't know.
    /// </summary>
    public string? Staged(string path)
    {
        GitResult result = GitProcess.Run(Root, ["show", $":{Relative(path)}"]);
        return result.Ok ? result.Output : null;
    }

    /// <summary>A file as it was in a commit ("HEAD", a hash), null where it wasn't.</summary>
    public string? AtCommit(string path, string commit)
    {
        GitResult result = GitProcess.Run(Root, ["show", $"{commit}:{Relative(path)}"]);
        return result.Ok ? result.Output : null;
    }

    /// <summary>
    /// Who last changed every line of a file as the editor has it, the text handed to git so lines typed since the last
    /// save are new and not somebody else's. Empty for a file git doesn't know.
    /// </summary>
    public IReadOnlyList<GitBlameLine> Blame(string path, string text)
    {
        return GitProcess.Run(
            Root,
            ["blame", "--porcelain", "--contents", "-", "--", Relative(path)],
            static (exit, output, _) => exit == 0 ? GitBlame.Parse(output) : [],
            text);
    }

    /// <summary>Every branch here and on the remotes, the one checked out first.</summary>
    public IReadOnlyList<GitBranch> Branches()
    {
        return RunCoalesced(InFlightBranches, Root, () =>
            GitProcess.Run(Root, BranchesArguments, static (exit, output, _) => exit == 0 ? GitBranch.Parse(output) : []));
    }

    /// <summary>Checks a branch out, a remote one as a local branch following it. All of what git said when it went wrong, null when it didn't.</summary>
    public string? Switch(GitBranch branch)
    {
        InvalidateInFlight(Root);
        try
        {
            GitResult result = branch.Remote
                ? GitProcess.Run(Root, ["switch", "--track", branch.Name])
                : GitProcess.Run(Root, ["switch", branch.Name]);
            return result.Ok ? null : Said(result);
        }
        finally
        {
            InvalidateInFlight(Root);
        }
    }

    /// <summary>Makes a branch where HEAD is and checks it out. All of what git said when it went wrong, null when it didn't.</summary>
    public string? CreateBranch(string name)
    {
        InvalidateInFlight(Root);
        try
        {
            GitResult result = GitProcess.Run(Root, ["switch", "-c", name]);
            return result.Ok ? null : Said(result);
        }
        finally
        {
            InvalidateInFlight(Root);
        }
    }

    /* Changes, staged, unstaged, thrown away and committed */

    /// <summary>Stages files, all of what's changed in them (a deleted one's deletion). What git said when it went wrong, null when it didn't.</summary>
    public string? Stage(IReadOnlyList<string> paths) => Each(paths, StageCommand);

    /// <summary>
    /// Takes files out of what the next commit takes, back to the last commit's, what's in the working tree left as it is.
    /// In a repository with no commit yet there's nothing to go back to, so they're taken out of the index altogether.
    /// </summary>
    public string? Unstage(IReadOnlyList<string> paths) => HasCommit()
        ? Each(paths, UnstageRestoreCommand)
        : Each(paths, UnstageRemoveCommand);

    /// <summary>
    /// Throws away what's changed in files and not staged, back to what the index has. Only for files git knows, a new file
    /// nobody added is the caller's to throw away (to the trash, not gone for good).
    /// </summary>
    public string? Discard(IReadOnlyList<string> paths) => Each(paths, DiscardCommand);

    /// <summary>Commits what's staged with a message, which can be more than one line. What git said when it went wrong (a hook said no), null when it didn't.</summary>
    public string? Commit(string message)
    {
        InvalidateInFlight(Root);
        try
        {
            // Whitespace only, not strip, which takes lines starting with a # for comments, a Markdown heading among them
            GitResult result = GitProcess.Run(Root, CommitArguments, message);
            return result.Ok ? null : Said(result);
        }
        finally
        {
            InvalidateInFlight(Root);
        }
    }

    /// <summary>
    /// Puts a text into the index as a file's staged version, what the next commit takes of it, without touching the file
    /// on the disk. How a single change is staged, the index's text with that change made to it. git's own filters
    /// (line endings) are run on it on the way in, the same as for git add. What git said when it went wrong, null when it didn't.
    /// </summary>
    public string? StageText(string path, string text)
    {
        InvalidateInFlight(Root);
        try
        {
            string relative = Relative(path);
            (bool ok, string value) hashed = GitProcess.Run(
                Root,
                ["hash-object", "-w", "--stdin", $"--path={relative}"],
                static (exit, output, error) => exit == 0 ? (true, output.Trim().ToString()) : (false, GitProcess.FormatSaid(output, error)),
                text);
            if (!hashed.ok)
                return hashed.value;

            // The file's mode as the index has it (an executable script stays one), a plain file's for one it hasn't got
            string mode = GitProcess.Run(
                Root,
                ["ls-files", "-s", "--", relative],
                static (exit, output, _) =>
                {
                    if (exit != 0)
                        return "100644";
                    int space = output.IndexOf(' ');
                    ReadOnlySpan<char> modeSpan = space >= 0 ? output[..space] : output;
                    return modeSpan.Length == 6 ? modeSpan.ToString() : "100644";
                });

            GitResult updated = GitProcess.Run(Root, ["update-index", "--add", "--cacheinfo", $"{mode},{hashed.value},{relative}"]);
            return updated.Ok ? null : Said(updated);
        }
        finally
        {
            InvalidateInFlight(Root);
        }
    }

    /* A file's history */

    /// <summary>
    /// The commits that changed a file, the newest first, followed through renames, each with the path the file had in it.
    /// At most so many, a file of a long life has thousands.
    /// </summary>
    public IReadOnlyList<GitCommitOfFile> FileLog(string path, int most = 200)
    {
        return GitProcess.Run(
            Root,
            ["log", "--follow", $"--max-count={most}", "--format=%x01%H%x00%an%x00%at%x00%s", "--name-only", "--", Relative(path)],
            static (exit, output, _) => exit == 0 ? ParseFileLog(output) : []);
    }

    internal static IReadOnlyList<GitCommitOfFile> ParseFileLog(ReadOnlySpan<char> remaining)
    {
        var commits = new List<GitCommitOfFile>();
        while (!remaining.IsEmpty)
        {
            int cut = remaining.IndexOf('\u0001');
            ReadOnlySpan<char> record;
            if (cut < 0)
            {
                record = remaining;
                remaining = default;
            }
            else
            {
                record = remaining[..cut];
                remaining = remaining[(cut + 1)..];
            }

            if (record.IsEmpty)
                continue;

            ReadOnlySpan<char> header = NextNonEmptyLine(ref record);
            ReadOnlySpan<char> fileLine = NextNonEmptyLine(ref record);
            if (header.IsEmpty || fileLine.IsEmpty)
                continue;

            if (!TrySplitFourNul(header, out ReadOnlySpan<char> hash, out ReadOnlySpan<char> author, out ReadOnlySpan<char> time, out ReadOnlySpan<char> subject))
                continue;

            long.TryParse(time, System.Globalization.CultureInfo.InvariantCulture, out long seconds);
            commits.Add(new GitCommitOfFile(hash.ToString(), author.ToString(), DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime(), subject.ToString(), fileLine.Trim().ToString()));
        }

        commits.TrimExcess();
        return commits;
    }

    private static ReadOnlySpan<char> NextNonEmptyLine(ref ReadOnlySpan<char> span)
    {
        while (!span.IsEmpty)
        {
            int nl = span.IndexOf('\n');
            ReadOnlySpan<char> line;
            if (nl < 0)
            {
                line = span;
                span = default;
            }
            else
            {
                line = span[..nl];
                span = span[(nl + 1)..];
            }

            if (!line.IsEmpty)
                return line;
        }
        return default;
    }

    private static bool TrySplitFourNul(ReadOnlySpan<char> line, out ReadOnlySpan<char> f0, out ReadOnlySpan<char> f1, out ReadOnlySpan<char> f2, out ReadOnlySpan<char> f3)
    {
        int n0 = line.IndexOf('\0');
        if (n0 < 0) { f0 = f1 = f2 = f3 = default; return false; }
        f0 = line[..n0];
        ReadOnlySpan<char> r1 = line[(n0 + 1)..];
        int n1 = r1.IndexOf('\0');
        if (n1 < 0) { f1 = f2 = f3 = default; return false; }
        f1 = r1[..n1];
        ReadOnlySpan<char> r2 = r1[(n1 + 1)..];
        int n2 = r2.IndexOf('\0');
        if (n2 < 0) { f2 = f3 = default; return false; }
        f2 = r2[..n2];
        ReadOnlySpan<char> r3 = r2[(n2 + 1)..];
        int n3 = r3.IndexOf('\0');
        f3 = n3 >= 0 ? r3[..n3] : r3;
        return true;
    }

    /// <summary>A file as it was in a commit by the path it had there (from <see cref="FileLog"/>), null where it wasn't.</summary>
    public string? AtCommitPath(string relative, string commit)
    {
        GitResult result = GitProcess.Run(Root, ["show", $"{commit}:{relative}"]);
        return result.Ok ? result.Output : null;
    }

    /* Repositories made, of a folder or cloned */

    /// <summary>A folder made a repository, git init in it. What went wrong, null when it didn't.</summary>
    public static string? Init(string folder)
    {
        GitResult result = GitProcess.Run(folder, ["init"]);
        return result.Ok ? null : Said(result);
    }

    /// <summary>A repository cloned into a folder that isn't there yet. What went wrong, null when it didn't.</summary>
    public static string? Clone(string url, string into)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(into)) ?? into;
        GitResult result = GitProcess.Run(parent, ["clone", "--", url, into], patience: Network);
        return result.Ok ? null : Said(result);
    }

    /// <summary>The name of the folder a clone of an address goes in, the last part of it without .git, the way git names it.</summary>
    public static string CloneName(string url)
    {
        ReadOnlySpan<char> last = url.AsSpan().Trim().TrimEnd("/\\");
        int cut = Math.Max(last.LastIndexOf('/'), Math.Max(last.LastIndexOf('\\'), last.LastIndexOf(':')));
        if (cut >= 0)
            last = last[(cut + 1)..];
        if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            last = last[..^4];
        return last.Length > 0 ? last.ToString() : "repository";
    }

    /* The remotes, fetched, pulled and pushed */

    // Going over the network takes as long as it takes, a big fetch on a slow line well past the usual patience
    private static readonly TimeSpan Network = TimeSpan.FromMinutes(5);

    /// <summary>Whether the repository has a remote at all, nothing to fetch from or push to without one.</summary>
    public bool HasRemote() => GitProcess.Run(Root, RemoteArguments, static (_, output, _) => !output.Trim().IsEmpty);

    /// <summary>What the remotes have that this hasn't, fetched without touching a branch. What git said when it went wrong, null when it didn't.</summary>
    public string? Fetch() => Fetch(unattended: false);

    /// <summary>The same fetched in the background, never stopping to ask anybody anything to sign in.</summary>
    public string? FetchUnattended() => Fetch(unattended: true);

    private string? Fetch(bool unattended)
    {
        InvalidateInFlight(Root);
        try
        {
            GitResult result = GitProcess.Run(Root, FetchArguments, patience: Network, unattended: unattended);
            return result.Ok ? null : Said(result);
        }
        finally
        {
            InvalidateInFlight(Root);
        }
    }

    /// <summary>
    /// The branch brought up to date with what it follows. Mode is how, merge, rebase or ff_only, anything else (default)
    /// the way git pull is set up to. What went wrong, null when it didn't.
    /// </summary>
    public string? Pull(string mode = "default")
    {
        InvalidateInFlight(Root);
        try
        {
            string[] arguments = PullFlag(mode) is { } flag ? ["pull", "--quiet", flag] : ["pull", "--quiet"];
            GitResult result = GitProcess.Run(Root, arguments, patience: Network);
            return result.Ok ? null : Said(result);
        }
        finally
        {
            InvalidateInFlight(Root);
        }
    }

    /// <summary>What git pull is told for a mode, --no-rebase, --rebase or --ff-only, null for leaving it to git config.</summary>
    public static string? PullFlag(string mode) => mode switch
    {
        "merge" => "--no-rebase",
        "rebase" => "--rebase",
        "ff_only" => "--ff-only",
        _ => null
    };

    /// <summary>
    /// The branch's commits sent to what it follows, or a branch that follows nothing yet published to the first remote
    /// and set to follow it there. What went wrong, null when it didn't.
    /// </summary>
    public string? Push(string branch, bool hasUpstream)
    {
        InvalidateInFlight(Root);
        try
        {
            if (hasUpstream)
            {
                GitResult pushed = GitProcess.Run(Root, PushQuietArguments, patience: Network);
                return pushed.Ok ? null : Said(pushed);
            }

            string remote = GitProcess.Run(Root, RemoteArguments, static (_, output, _) =>
            {
                ReadOnlySpan<char> remotes = output;
                ReadOnlySpan<char> firstRemote = NextNonEmptyLine(ref remotes).Trim();
                return firstRemote.IsEmpty ? "origin" : firstRemote.ToString();
            });
            GitResult published = GitProcess.Run(Root, ["push", "--quiet", "--set-upstream", remote, branch], patience: Network);
            return published.Ok ? null : Said(published);
        }
        finally
        {
            InvalidateInFlight(Root);
        }
    }

    /// <summary>Whether HEAD is on a commit, false in a repository nobody committed to yet.</summary>
    public bool HasCommit() => GitProcess.Run(Root, HasCommitArguments, static (exit, _, _) => exit == 0);

    // Runs a command on some paths, as many at a time as fit on a command line
    private string? Each(IReadOnlyList<string> paths, string[] command)
    {
        InvalidateInFlight(Root);
        try
        {
            const int AT_ONCE = 200;
            for (int at = 0; at < paths.Count; at += AT_ONCE)
            {
                int batch = Math.Min(paths.Count - at, AT_ONCE);
                var arguments = new List<string>(command.Length + batch);
                arguments.AddRange(command);
                for (int i = at; i < at + batch; i++)
                    arguments.Add(Relative(paths[i]));

                GitResult result = GitProcess.Run(Root, arguments);
                if (!result.Ok)
                    return Said(result);
            }

            return null;
        }
        finally
        {
            InvalidateInFlight(Root);
        }
    }

    // Everything git said about something that failed, its errors or failing that what it wrote out
    private static string Said(GitResult result) => GitProcess.FormatSaid(result.Output.AsSpan(), result.Error.AsSpan());
}
