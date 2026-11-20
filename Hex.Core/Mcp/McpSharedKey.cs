namespace Hex.Core;

/// <summary>
/// What a shared MCP server is known by: its name, the root of the git worktree it's for, and the command line that
/// starts it. Agents that say the same of all three share one process, a different worktree (or none, a plain folder) or
/// another command line gets one of its own, so two checkouts of a repository never see each other's code.
/// </summary>
/// <param name="Name">What the agent's config calls it, roslyn.</param>
/// <param name="Root">The worktree root it runs in and is told of as its one root (roots/list).</param>
/// <param name="Command">The program, as the config said it.</param>
/// <param name="Arguments">What it's started with.</param>
internal sealed record McpSharedKey(string Name, string Root, string Command, IReadOnlyList<string> Arguments)
{
    /// <summary>The key as one text, the same for the same server whatever case Windows has its paths in.</summary>
    public string Id { get; } = string.Join('\0', [Name, OperatingSystem.IsWindows() ? Root.ToUpperInvariant() : Root, Command, .. Arguments]);

    /// <summary>
    /// The root of the git worktree a folder is in, the nearest folder up from it with a .git (a folder in a clone, a
    /// file in a worktree git made), or the folder itself when none is, as a whole path without a separator at its end.
    /// </summary>
    public static string RootOf(string folder)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        for (DirectoryInfo? at = new(full); at is not null; at = at.Parent)
        {
            string git = Path.Combine(at.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return Path.TrimEndingDirectorySeparator(at.FullName);
        }

        return full;
    }

    /// <summary>The root as a file URI, how roots/list says it.</summary>
    public string RootUri => new Uri(Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');

    public override string ToString() => $"{Name} for {Root}";
}
