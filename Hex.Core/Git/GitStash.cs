namespace Hex.Core.Git;

/// <summary>A stash, its place in the list (0 the newest), its name as git writes it (stash@{0}), what it says and when it was made.</summary>
internal readonly record struct GitStashEntry(int Index, string Name, string Message, DateTimeOffset When)
{
    /// <summary>The stash's message without the "On main" git puts in front, the message typed for it or git's WIP line.</summary>
    public string Short => Message.IndexOf(": ", StringComparison.Ordinal) is var colon and > 0 && Message.StartsWith("On ", StringComparison.Ordinal)
        ? Message[(colon + 2)..]
        : Message;

    /// <summary>The entries of git stash list, as <see cref="GitStash.List"/> asks for them.</summary>
    public static IReadOnlyList<GitStashEntry> Parse(string output)
    {
        var entries = new List<GitStashEntry>();
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.TrimEnd('\r').Split('\0');
            if (fields.Length < 3)
                continue;

            // stash@{3}, the number between the braces
            string name = fields[0];
            int open = name.IndexOf('{'), close = name.IndexOf('}');
            if (open < 0 || close <= open || !int.TryParse(name.AsSpan(open + 1, close - open - 1), out int index))
                continue;

            long.TryParse(fields[2], out long seconds);
            entries.Add(new GitStashEntry(index, name, fields[1], DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime()));
        }

        return entries;
    }
}

/// <summary>
/// The stash, changes put away and the working tree back to HEAD, then brought back later. All of it git stash itself, so a
/// stash made here is the terminal's and the other way round. Every call runs git and waits, so it's all for the background.
/// </summary>
internal static class GitStash
{
    /// <summary>Every stash, the newest first. Empty for none or git not saying.</summary>
    public static IReadOnlyList<GitStashEntry> List(this GitRepository repository)
    {
        GitResult result = GitProcess.Run(repository.Root, ["stash", "list", "--format=%gd%x00%s%x00%ct"]);
        return result.Ok ? GitStashEntry.Parse(result.Output) : [];
    }

    /// <summary>
    /// Puts what's changed away in a new stash, the files nobody added too when asked, with a message or git's own when
    /// it's empty. What git said when it went wrong (nothing to stash among it), null when it didn't.
    /// Not Push, which is the remote push of GitRepository and wins over an extension with the same arguments.
    /// </summary>
    public static string? PushStash(this GitRepository repository, string? message, bool includeUntracked)
    {
        var arguments = new List<string> { "stash", "push" };
        if (includeUntracked)
            arguments.Add("--include-untracked");
        if (!string.IsNullOrWhiteSpace(message))
        {
            arguments.Add("-m");
            arguments.Add(message.Trim());
        }

        GitResult result = GitProcess.Run(repository.Root, arguments);
        if (!result.Ok)
            return Said(result);

        // git says it had nothing to save and still exits 0, which isn't a stash made
        return result.Output.Contains("No local changes to save", StringComparison.Ordinal) ? result.Output.Trim() : null;
    }

    /// <summary>Brings a stash's changes back and keeps the stash. What git said when it went wrong (a conflict), null when it didn't.</summary>
    public static string? Apply(this GitRepository repository, int index) => Stash(repository, "apply", index);

    /// <summary>Brings a stash's changes back and drops it, kept when they didn't come back cleanly. What git said when it went wrong, null when it didn't.</summary>
    public static string? Pop(this GitRepository repository, int index) => Stash(repository, "pop", index);

    /// <summary>Throws a stash away. What git said when it went wrong, null when it didn't.</summary>
    public static string? Drop(this GitRepository repository, int index) => Stash(repository, "drop", index);

    /// <summary>What a stash changed as a patch, the files nobody added that it took among it, or what git said when it went wrong.</summary>
    public static (string Patch, string? Failed) Show(this GitRepository repository, int index)
    {
        GitResult result = GitProcess.Run(repository.Root, ["stash", "show", "-p", "--include-untracked", $"stash@{{{index}}}"]);
        return result.Ok ? (result.Output, null) : (string.Empty, Said(result));
    }

    private static string? Stash(GitRepository repository, string verb, int index)
    {
        GitResult result = GitProcess.Run(repository.Root, ["stash", verb, "--quiet", $"stash@{{{index}}}"]);
        return result.Ok ? null : Said(result);
    }

    // Everything git said about something that failed, its errors or failing that what it wrote out
    internal static string Said(GitResult result) => (result.Error.Trim().Length > 0 ? result.Error : result.Output).Trim();
}
