using System.Text;

namespace Hex.Core.Git;

/// <summary>
/// The last commit asked about and done things to, its message read, amended or undone, and the message a merge left
/// for the commit that finishes it. All git itself and waited for, so all for a background thread like the rest.
/// </summary>
internal static class GitCommits
{
    private static readonly string[] LastMessageArguments = ["log", "-1", "--format=%B"];
    private static readonly string[] AmendArguments = ["commit", "--amend", "--file=-", "--cleanup=whitespace"];
    private static readonly string[] VerifyParentArguments = ["rev-parse", "--verify", "--quiet", "HEAD~1"];
    private static readonly string[] UndoResetArguments = ["reset", "--soft", "HEAD~1"];
    private static readonly string[] MergeMsgPathArguments = ["rev-parse", "--git-path", "MERGE_MSG"];

    /// <summary>The whole message of the commit HEAD is on, null in a repository nobody committed to yet.</summary>
    public static string? LastMessage(this GitRepository repository)
    {
        return GitProcess.Run(repository.Root, LastMessageArguments, static (exit, output, _) =>
            exit == 0 ? output.TrimEnd().ToString() : null);
    }

    /// <summary>
    /// The last commit made again with a new message and whatever is staged now, nothing staged being the message alone.
    /// What git said when it went wrong, null when it didn't.
    /// </summary>
    public static string? Amend(this GitRepository repository, string message)
    {
        GitRepository.InvalidateInFlight(repository.Root);
        try
        {
            // Whitespace, the same as a commit, a Markdown heading isn't a comment
            GitResult result = GitProcess.Run(repository.Root, AmendArguments, message);
            return result.Ok ? null : Said(result);
        }
        finally
        {
            GitRepository.InvalidateInFlight(repository.Root);
        }
    }

    /// <summary>
    /// The last commit taken back with what it changed left staged, git reset --soft HEAD~1. Not the first commit, there's
    /// nothing before it to go back to. What went wrong, null when nothing did.
    /// </summary>
    public static string? UndoLastCommit(this GitRepository repository)
    {
        if (!repository.HasCommit())
            return "there's no commit to undo yet";
        if (!GitProcess.Run(repository.Root, VerifyParentArguments, static (exit, _, _) => exit == 0))
            return "that's the first commit, there's nothing before it to go back to";

        GitRepository.InvalidateInFlight(repository.Root);
        try
        {
            GitResult result = GitProcess.Run(repository.Root, UndoResetArguments);
            return result.Ok ? null : Said(result);
        }
        finally
        {
            GitRepository.InvalidateInFlight(repository.Root);
        }
    }

    /// <summary>
    /// The message git wrote for the commit that finishes a merge, its # lines (the conflicts it lists, the hints) taken
    /// out. Null when no merge is going on.
    /// </summary>
    public static string? MergeMessage(this GitRepository repository)
    {
        string? path = GitProcess.Run(repository.Root, MergeMsgPathArguments, (exit, output, _) =>
        {
            if (exit != 0)
                return null;
            ReadOnlySpan<char> trimmed = output.Trim();
            return trimmed.IsEmpty ? null : Path.GetFullPath(Path.Combine(repository.Root, trimmed.ToString()));
        });
        if (path is null || !File.Exists(path))
            return null;

        try
        {
            string raw = File.ReadAllText(path);
            var builder = new StringBuilder(raw.Length);
            ReadOnlySpan<char> remaining = raw.AsSpan();
            bool first = true;
            while (!remaining.IsEmpty)
            {
                int nl = remaining.IndexOf('\n');
                ReadOnlySpan<char> line = nl < 0 ? remaining : remaining[..nl];
                remaining = nl < 0 ? default : remaining[(nl + 1)..];
                if (line.Length > 0 && line[^1] == '\r')
                    line = line[..^1];
                if (line.Length > 0 && line[0] == '#')
                    continue;
                if (!first)
                    builder.Append('\n');
                builder.Append(line);
                first = false;
            }
            return builder.ToString().Trim();
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string Said(GitResult result) => GitProcess.FormatSaid(result.Output.AsSpan(), result.Error.AsSpan());
}
