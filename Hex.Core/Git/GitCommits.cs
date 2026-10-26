namespace Hex.Core.Git;

/// <summary>
/// The last commit asked about and done things to, its message read, amended or undone, and the message a merge left
/// for the commit that finishes it. All git itself and waited for, so all for a background thread like the rest.
/// </summary>
internal static class GitCommits
{
    /// <summary>The whole message of the commit HEAD is on, null in a repository nobody committed to yet.</summary>
    public static string? LastMessage(this GitRepository repository)
    {
        GitResult result = GitProcess.Run(repository.Root, ["log", "-1", "--format=%B"]);
        return result.Ok ? result.Output.TrimEnd() : null;
    }

    /// <summary>
    /// The last commit made again with a new message and whatever is staged now, nothing staged being the message alone.
    /// What git said when it went wrong, null when it didn't.
    /// </summary>
    public static string? Amend(this GitRepository repository, string message)
    {
        // Whitespace, the same as a commit, a Markdown heading isn't a comment
        GitResult result = GitProcess.Run(repository.Root, ["commit", "--amend", "--file=-", "--cleanup=whitespace"], message);
        return result.Ok ? null : Said(result);
    }

    /// <summary>
    /// The last commit taken back with what it changed left staged, git reset --soft HEAD~1. Not the first commit, there's
    /// nothing before it to go back to. What went wrong, null when nothing did.
    /// </summary>
    public static string? UndoLastCommit(this GitRepository repository)
    {
        if (!repository.HasCommit())
            return "there's no commit to undo yet";
        if (!GitProcess.Run(repository.Root, ["rev-parse", "--verify", "--quiet", "HEAD~1"]).Ok)
            return "that's the first commit, there's nothing before it to go back to";

        GitResult result = GitProcess.Run(repository.Root, ["reset", "--soft", "HEAD~1"]);
        return result.Ok ? null : Said(result);
    }

    /// <summary>
    /// The message git wrote for the commit that finishes a merge, its # lines (the conflicts it lists, the hints) taken
    /// out. Null when no merge is going on.
    /// </summary>
    public static string? MergeMessage(this GitRepository repository)
    {
        GitResult where = GitProcess.Run(repository.Root, ["rev-parse", "--git-path", "MERGE_MSG"]);
        if (!where.Ok)
            return null;

        // Relative to the root unless it's in another folder altogether (a worktree's)
        string path = Path.GetFullPath(Path.Combine(repository.Root, where.Output.Trim()));
        if (!File.Exists(path))
            return null;

        try
        {
            IEnumerable<string> lines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n').Where(line => !line.StartsWith('#'));
            return string.Join('\n', lines).Trim();
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string Said(GitResult result) => (result.Error.Trim().Length > 0 ? result.Error : result.Output).Trim();
}
