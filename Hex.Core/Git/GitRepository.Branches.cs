namespace Hex.Core.Git;

// Branches and tags worked on the way the terminal would, merged in, rebased onto, renamed, deleted and tagged, and
// single commits picked over or taken back. Every one returns what git said when it went wrong and null when it didn't,
// the same as Switch and Commit. A merge, a rebase, a cherry-pick or a revert that stops on conflicts goes wrong too, and
// leaves git's files behind for GitMerge to find, so the caller asks Operation afterwards to tell the two apart.
internal sealed partial class GitRepository
{
    /// <summary>Merges a branch into the one checked out, git's own message for it and no editor to stop in.</summary>
    public string? MergeBranch(string name) => Did(["merge", "--no-edit", name]);

    /// <summary>The branch checked out rebased onto another. Never interactive, so there's nothing to edit on the way.</summary>
    public string? RebaseOnto(string name) => Did(["-c", "core.editor=true", "rebase", name]);

    /// <summary>
    /// Deletes a local branch. Without force git refuses one that isn't merged anywhere yet and says it's not fully merged,
    /// which is the caller's to ask about before trying again with force.
    /// </summary>
    public string? DeleteBranch(string name, bool force) => Did(["branch", force ? "-D" : "-d", name]);

    /// <summary>Gives a local branch a new name, the one checked out staying checked out under it.</summary>
    public string? RenameBranch(string from, string to) => Did(["branch", "-m", from, to]);

    /// <summary>Tags where HEAD is, a lightweight tag with no message, an annotated one with.</summary>
    public string? CreateTag(string name, string? message = null) => message is { Length: > 0 }
        ? Did(["tag", "-a", name, "-m", message])
        : Did(["tag", name]);

    /// <summary>Deletes a tag here, the remotes keep theirs.</summary>
    public string? DeleteTag(string name) => Did(["tag", "-d", name]);

    /// <summary>Every tag, the newest first, with when it was made and the subject of what it points at.</summary>
    public IReadOnlyList<GitTag> Tags()
    {
        GitResult result = GitProcess.Run(Root, ["for-each-ref", "refs/tags", "--sort=-creatordate", "--format=%(refname:short)%00%(creatordate:relative)%00%(subject)"]);
        if (!result.Ok)
            return [];

        var tags = new List<GitTag>();
        foreach (string line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Split('\0');
            if (fields.Length >= 3)
                tags.Add(new GitTag(fields[0], fields[1], fields[2].TrimEnd('\r')));
        }

        return tags;
    }

    /// <summary>A commit from somewhere else made again on the branch checked out.</summary>
    public string? CherryPick(string commit) => Did(["-c", "core.editor=true", "cherry-pick", commit]);

    /// <summary>A new commit that takes back what another one did, git's own message for it.</summary>
    public string? Revert(string commit) => Did(["-c", "core.editor=true", "revert", "--no-edit", commit]);

    /// <summary>
    /// The commits of a revision, the newest first, at most so many. Anything git log takes, a branch, HEAD, or a range
    /// like HEAD..feature for what a branch has that this one hasn't.
    /// </summary>
    public IReadOnlyList<GitLogCommit> Log(string revision = "HEAD", int most = 200)
    {
        GitResult result = GitProcess.Run(Root, ["log", $"--max-count={most}", "--format=%H%x00%an%x00%at%x00%s", revision, "--"]);
        if (!result.Ok)
            return [];

        var commits = new List<GitLogCommit>();
        foreach (string line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Split('\0');
            if (fields.Length < 4)
                continue;

            long.TryParse(fields[2], out long seconds);
            commits.Add(new GitLogCommit(fields[0], fields[1], DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime(), fields[3].TrimEnd('\r')));
        }

        return commits;
    }

    /// <summary>How many files are in conflict, what a merge or a rebase that stopped left to settle.</summary>
    public int ConflictCount()
    {
        GitResult result = GitProcess.Run(Root, ["diff", "--name-only", "--diff-filter=U"]);
        return result.Ok ? result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length : 0;
    }

    /// <summary>
    /// Commits what's staged, the hooks skipped (--no-verify) or a Signed-off-by line added (--signoff) when asked for. What
    /// git said when it went wrong, null when it didn't.
    /// </summary>
    public string? Commit(string message, bool noVerify, bool signOff)
    {
        // Whitespace only, the same as a plain commit, a Markdown heading isn't a comment
        var arguments = new List<string> { "commit", "--file=-", "--cleanup=whitespace" };
        if (noVerify)
            arguments.Add("--no-verify");
        if (signOff)
            arguments.Add("--signoff");

        GitResult result = GitProcess.Run(Root, arguments, message);
        return result.Ok ? null : Said(result);
    }

    // Runs something of git's, what it said when it went wrong
    private string? Did(string[] arguments)
    {
        GitResult result = GitProcess.Run(Root, arguments);
        return result.Ok ? null : Said(result);
    }
}

/// <summary>A tag, when it was made (said the way git says it, 3 days ago) and the subject of the commit it's on.</summary>
internal sealed record GitTag(string Name, string When, string Subject);

/// <summary>A commit of a branch's log, who made it, when and its summary.</summary>
internal sealed record GitLogCommit(string Hash, string Author, DateTimeOffset When, string Subject)
{
    public string Short => Hash[..Math.Min(8, Hash.Length)];
}
