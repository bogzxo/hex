namespace Hex.Core.Git;

internal sealed partial class GitRepository
{
    /// <summary>
    /// git's own unified diff of a file or a folder, what's staged (the index against HEAD) or what isn't (the working
    /// tree against the index), null when git wouldn't say. Read-only, nothing is staged or changed.
    /// </summary>
    /// <param name="path">A file or folder in the repository, the whole repository for null.</param>
    public string? Diff(string? path, bool staged, int context = 3)
    {
        List<string> arguments = ["-c", "core.quotepath=off", "diff", "--no-color", "--no-ext-diff", $"--unified={Math.Clamp(context, 0, 20)}"];
        if (staged)
            arguments.Add("--cached");
        arguments.Add("--");
        arguments.Add(path is null ? "." : Relative(path));
        GitResult result = GitProcess.Run(Root, arguments);
        return result.Ok ? result.Output : null;
    }
}
