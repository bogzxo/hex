namespace Hex.Core.Git;

/// <summary>
/// A repository on the disk, found from a file or a folder in it, and what Hex asks of it. Every call runs git and
/// waits, so it's all for a background thread (HexGit in Dawn does the asking and brings the answers back).
/// </summary>
internal sealed partial class GitRepository
{
    /// <summary>The folder the working tree starts in, the one with .git in it.</summary>
    public string Root { get; }

    private GitRepository(string root) => Root = root;

    /// <summary>
    /// The repository a path is in, the nearest folder up from it with a .git (a folder, or a file in a submodule or a
    /// worktree), null for none or no git to ask.
    /// </summary>
    public static GitRepository? Find(string path)
    {
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
        GitResult result = GitProcess.Run(Root, ["status", "--porcelain=v2", "-z", "--branch", "--untracked-files=all", "--ignored=matching"]);
        return result.Ok ? GitStatus.Parse(Root, result.Output) : null;
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
        GitResult result = GitProcess.Run(Root, ["blame", "--porcelain", "--contents", "-", "--", Relative(path)], text);
        return result.Ok ? GitBlame.Parse(result.Output) : [];
    }

    /// <summary>Every branch here and on the remotes, the one checked out first.</summary>
    public IReadOnlyList<GitBranch> Branches()
    {
        GitResult result = GitProcess.Run(Root, ["for-each-ref", "--sort=-committerdate", "--format=%(HEAD)%00%(refname)%00%(refname:short)%00%(upstream:short)%00%(committerdate:relative)%00%(subject)", "refs/heads", "refs/remotes"]);
        return result.Ok ? GitBranch.Parse(result.Output) : [];
    }

    /// <summary>Checks a branch out, a remote one as a local branch following it. All of what git said when it went wrong, null when it didn't.</summary>
    public string? Switch(GitBranch branch)
    {
        GitResult result = branch.Remote
            ? GitProcess.Run(Root, ["switch", "--track", branch.Name])
            : GitProcess.Run(Root, ["switch", branch.Name]);
        return result.Ok ? null : Said(result);
    }

    /// <summary>Makes a branch where HEAD is and checks it out. All of what git said when it went wrong, null when it didn't.</summary>
    public string? CreateBranch(string name)
    {
        GitResult result = GitProcess.Run(Root, ["switch", "-c", name]);
        return result.Ok ? null : Said(result);
    }

    /* Changes, staged, unstaged, thrown away and committed */

    /// <summary>Stages files, all of what's changed in them (a deleted one's deletion). What git said when it went wrong, null when it didn't.</summary>
    public string? Stage(IReadOnlyList<string> paths) => Each(paths, ["add", "--all", "--"]);

    /// <summary>
    /// Takes files out of what the next commit takes, back to the last commit's, what's in the working tree left as it is.
    /// In a repository with no commit yet there's nothing to go back to, so they're taken out of the index altogether.
    /// </summary>
    public string? Unstage(IReadOnlyList<string> paths) => HasCommit()
        ? Each(paths, ["restore", "--staged", "--"])
        : Each(paths, ["rm", "--cached", "-r", "-q", "--"]);

    /// <summary>
    /// Throws away what's changed in files and not staged, back to what the index has. Only for files git knows, a new file
    /// nobody added is the caller's to throw away (to the trash, not gone for good).
    /// </summary>
    public string? Discard(IReadOnlyList<string> paths) => Each(paths, ["restore", "--worktree", "--"]);

    /// <summary>Commits what's staged with a message, which can be more than one line. What git said when it went wrong (a hook said no), null when it didn't.</summary>
    public string? Commit(string message)
    {
        // Whitespace only, not strip, which takes lines starting with a # for comments, a Markdown heading among them
        GitResult result = GitProcess.Run(Root, ["commit", "--file=-", "--cleanup=whitespace"], message);
        return result.Ok ? null : Said(result);
    }

    /// <summary>
    /// Puts a text into the index as a file's staged version, what the next commit takes of it, without touching the file
    /// on the disk. How a single change is staged, the index's text with that change made to it. git's own filters
    /// (line endings) are run on it on the way in, the same as for git add. What git said when it went wrong, null when it didn't.
    /// </summary>
    public string? StageText(string path, string text)
    {
        string relative = Relative(path);
        GitResult hashed = GitProcess.Run(Root, ["hash-object", "-w", "--stdin", $"--path={relative}"], text);
        if (!hashed.Ok)
            return Said(hashed);

        // The file's mode as the index has it (an executable script stays one), a plain file's for one it hasn't got
        GitResult listed = GitProcess.Run(Root, ["ls-files", "-s", "--", relative]);
        string mode = listed.Ok && listed.Output.Split(' ', 2)[0] is { Length: 6 } known ? known : "100644";

        GitResult updated = GitProcess.Run(Root, ["update-index", "--add", "--cacheinfo", $"{mode},{hashed.Output.Trim()},{relative}"]);
        return updated.Ok ? null : Said(updated);
    }

    /* A file's history */

    /// <summary>
    /// The commits that changed a file, the newest first, followed through renames, each with the path the file had in it.
    /// At most so many, a file of a long life has thousands.
    /// </summary>
    public IReadOnlyList<GitCommitOfFile> FileLog(string path, int most = 200)
    {
        GitResult result = GitProcess.Run(Root, ["log", "--follow", $"--max-count={most}", "--format=%x01%H%x00%an%x00%at%x00%s", "--name-only", "--", Relative(path)]);
        if (!result.Ok)
            return [];

        var commits = new List<GitCommitOfFile>();
        foreach (string record in result.Output.Split('\u0001', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] lines = record.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            string[] fields = lines[0].Split('\0');
            if (fields.Length < 4 || lines.Length < 2)
                continue;

            long.TryParse(fields[2], out long seconds);
            commits.Add(new GitCommitOfFile(fields[0], fields[1], DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime(), fields[3], lines[1].Trim()));
        }

        return commits;
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
        string last = url.Trim().TrimEnd('/', '\\');
        int cut = Math.Max(last.LastIndexOf('/'), Math.Max(last.LastIndexOf('\\'), last.LastIndexOf(':')));
        last = last[(cut + 1)..];
        if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            last = last[..^4];
        return last.Length > 0 ? last : "repository";
    }

    /* The remotes, fetched, pulled and pushed */

    // Going over the network takes as long as it takes, a big fetch on a slow line well past the usual patience
    private static readonly TimeSpan Network = TimeSpan.FromMinutes(5);

    /// <summary>Whether the repository has a remote at all, nothing to fetch from or push to without one.</summary>
    public bool HasRemote() => GitProcess.Run(Root, ["remote"]).Output.Trim().Length > 0;

    /// <summary>What the remotes have that this hasn't, fetched without touching a branch. What git said when it went wrong, null when it didn't.</summary>
    public string? Fetch() => Fetch(unattended: false);

    /// <summary>The same fetched in the background, never stopping to ask anybody anything to sign in.</summary>
    public string? FetchUnattended() => Fetch(unattended: true);

    private string? Fetch(bool unattended)
    {
        GitResult result = GitProcess.Run(Root, ["fetch", "--all", "--prune", "--quiet"], patience: Network, unattended: unattended);
        return result.Ok ? null : Said(result);
    }

    /// <summary>
    /// The branch brought up to date with what it follows. Mode is how, merge, rebase or ff_only, anything else (default)
    /// the way git pull is set up to. What went wrong, null when it didn't.
    /// </summary>
    public string? Pull(string mode = "default")
    {
        string[] arguments = PullFlag(mode) is { } flag ? ["pull", "--quiet", flag] : ["pull", "--quiet"];
        GitResult result = GitProcess.Run(Root, arguments, patience: Network);
        return result.Ok ? null : Said(result);
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
        if (hasUpstream)
        {
            GitResult pushed = GitProcess.Run(Root, ["push", "--quiet"], patience: Network);
            return pushed.Ok ? null : Said(pushed);
        }

        string remote = GitProcess.Run(Root, ["remote"]).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "origin";
        GitResult published = GitProcess.Run(Root, ["push", "--quiet", "--set-upstream", remote, branch], patience: Network);
        return published.Ok ? null : Said(published);
    }

    /// <summary>Whether HEAD is on a commit, false in a repository nobody committed to yet.</summary>
    public bool HasCommit() => GitProcess.Run(Root, ["rev-parse", "--verify", "--quiet", "HEAD"]).Ok;

    // Runs a command on some paths, as many at a time as fit on a command line
    private string? Each(IReadOnlyList<string> paths, string[] command)
    {
        const int AT_ONCE = 200;
        for (int at = 0; at < paths.Count; at += AT_ONCE)
        {
            var arguments = new List<string>(command);
            for (int i = at; i < Math.Min(paths.Count, at + AT_ONCE); i++)
                arguments.Add(Relative(paths[i]));

            GitResult result = GitProcess.Run(Root, arguments);
            if (!result.Ok)
                return Said(result);
        }

        return null;
    }

    // Everything git said about something that failed, its errors or failing that what it wrote out
    private static string Said(GitResult result) => (result.Error.Trim().Length > 0 ? result.Error : result.Output).Trim();
}
