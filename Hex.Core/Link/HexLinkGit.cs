using System.Text;
using Hex.Core.Git;

namespace Hex.Core;

/// <summary>
/// A repository handed from one machine to another over a link, by git's own means and no server between them. The
/// host writes a bundle (<c>git bundle create --all</c>, less what the asker says it has), which goes over in chunks;
/// the asker checks it with <c>git bundle verify</c> and clones it, or fetches it into <c>refs/remotes/&lt;device&gt;/*</c>
/// of a clone it has, so work goes back and forth by ordinary merges. A patch is the other way round: the working
/// tree's diff against HEAD, offered, and applied on the other side once <c>git apply --check</c> says it fits.
/// Everything here runs git and waits, so it's never for the editor's thread.
/// </summary>
internal static class HexLinkGit
{
    /// <summary>How much of a bundle goes in a chunk, in bytes before base64.</summary>
    public const int CHUNK = 256 * 1024;

    /// <summary>The most commits an asker may say it has.</summary>
    public const int MOST_HAVE = 512;

    // A bundle can take a while for a big repository
    private static readonly TimeSpan PATIENCE = TimeSpan.FromMinutes(10);

    /// <summary>Why git can't be run here, null when it can.</summary>
    public static string? Why() => HexHost.NoProcesses("git") ?? (GitProcess.Installed ? null : "git isn't installed");

    /// <summary>A bundle written, where, how big and the refs in it (name and commit).</summary>
    public sealed record Made(string File, long Size, IReadOnlyList<(string Name, string Head)> Refs);

    /// <summary>
    /// Every branch and tag of a repository in a bundle in the temp folder, less the commits of <paramref name="have"/>
    /// this repository knows (the asker's, so only what's new goes). Null when there's nothing new; throws with git's
    /// reason when it fails. The file is the caller's to delete.
    /// </summary>
    public static Made? Bundle(string repo, IReadOnlyList<string> have, CancellationToken cancel = default, bool includeHead = false)
    {
        if (Why() is { } why)
            throw new InvalidOperationException(why);

        var arguments = new List<string> { "bundle", "create", "-q" };
        string file = Path.Combine(Path.GetTempPath(), $"hexlink-{Guid.NewGuid():N}.bundle");
        arguments.Add(file);
        arguments.Add("--all");
        if (includeHead)
            arguments.Add("HEAD");
        foreach (string known in Known(repo, have, cancel))
            arguments.Add("^" + known);

        try
        {
            GitResult made = GitProcess.Run(repo, arguments, patience: PATIENCE, unattended: true, cancel: cancel);
            // Everything the asker has, git refuses to write a bundle of nothing.
            if (!made.Ok && made.Error.Contains("empty bundle", StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(file);
                return null;
            }
            if (!made.Ok)
                throw new InvalidOperationException($"git bundle failed: {ErrorOf(made)}");

            return new Made(file, new FileInfo(file).Length, Refs(file, cancel));
        }
        catch
        {
            TryDelete(file);
            throw;
        }
    }

    // The commits of what the asker has that this repository knows, the rest (the asker's own work) can't be left out
    private static List<string> Known(string repo, IReadOnlyList<string> have, CancellationToken cancel)
    {
        string[] asked = [.. have.Where(IsCommitId).Distinct(StringComparer.OrdinalIgnoreCase).Take(MOST_HAVE)];
        if (asked.Length == 0)
            return [];

        GitResult checkedOut = GitProcess.Run(repo, ["cat-file", "--batch-check"], string.Join('\n', asked.Select(sha => sha + "^{commit}")) + "\n", cancel: cancel);
        if (!checkedOut.Ok)
            return [];

        var known = new List<string>();
        foreach (string line in checkedOut.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = line.Split(' ');
            if (parts.Length >= 2 && parts[1] == "commit" && IsCommitId(parts[0]))
                known.Add(parts[0]);
        }

        return known;
    }

    /// <summary>Whether a string is a whole commit id (SHA-1 or SHA-256 in hex), the only thing taken as one from a peer.</summary>
    public static bool IsCommitId(string said) => said.Length is 40 or 64 && said.All(char.IsAsciiHexDigit);

    /// <summary>The refs in a bundle, read without a repository.</summary>
    public static List<(string Name, string Head)> Refs(string bundle, CancellationToken cancel = default)
    {
        var refs = new List<(string, string)>();
        GitResult heads = GitProcess.Run(Path.GetDirectoryName(bundle) ?? ".", ["bundle", "list-heads", bundle], cancel: cancel);
        if (!heads.Ok)
            return refs;
        foreach (string line in heads.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int space = line.IndexOf(' ');
            if (space > 0 && IsCommitId(line[..space]))
                refs.Add((line[(space + 1)..], line[..space]));
        }

        return refs;
    }

    /// <summary>
    /// Whether a bundle that came over is whole and fits, null when it does, why when it doesn't. In a repository it's
    /// verified against it (what it needs is there); without one its heads have to be readable.
    /// </summary>
    public static string? Verify(string bundle, string? repo, CancellationToken cancel = default)
    {
        if (Why() is { } why)
            return why;
        if (repo is null)
            return GitProcess.Run(Path.GetDirectoryName(bundle) ?? ".", ["bundle", "list-heads", bundle], cancel: cancel) is { Ok: false } bad ? $"It isn't a bundle git can read, {bad.Why}" : null;
        GitResult verified = GitProcess.Run(repo, ["bundle", "verify", "-q", bundle], patience: PATIENCE, cancel: cancel);
        return verified.Ok ? null : $"It doesn't fit the repository, {verified.Why}";
    }

    /// <summary>A bundle cloned into a new folder, with nothing set as its remote. The reason when it failed.</summary>
    public static string? Clone(string bundle, string into)
    {
        if (Verify(bundle, null) is { } bad)
            return bad;
        GitResult cloned = GitProcess.Run(Path.GetDirectoryName(Path.GetFullPath(into)) ?? ".", ["clone", "-q", bundle, into], patience: PATIENCE);
        if (!cloned.Ok)
            return $"git clone failed, {cloned.Why}";
        // The bundle was a temp file, a remote pointing at it would be a remote pointing at nothing
        GitProcess.Run(into, ["remote", "remove", "origin"]);
        return null;
    }

    /// <summary>
    /// A bundle's branches fetched into a repository as <c>refs/remotes/&lt;device&gt;/*</c>, the device named by
    /// <see cref="Slug"/>. The reason when it failed.
    /// </summary>
    public static string? Fetch(string bundle, string repo, string device)
    {
        if (Verify(bundle, repo) is { } bad)
            return bad;
        GitResult fetched = GitProcess.Run(repo, ["fetch", "-q", bundle, $"+refs/heads/*:refs/remotes/{Slug(device)}/*"], patience: PATIENCE);
        return fetched.Ok ? null : $"git fetch failed, {fetched.Why}";
    }

    /// <summary>The commits a repository has at the tips of its refs, what it tells a host it has.</summary>
    public static List<string> Haves(string repo, CancellationToken cancel = default)
    {
        GitResult head = GitProcess.Run(repo, ["rev-parse", "--verify", "--quiet", "HEAD"], cancel: cancel);
        GitResult refs = GitProcess.Run(repo, ["for-each-ref", "--format=%(objectname)", "refs/heads", "refs/remotes", "refs/tags"], cancel: cancel);
        return [.. (head.Output + "\n" + refs.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsCommitId).Distinct(StringComparer.OrdinalIgnoreCase).Take(MOST_HAVE)];
    }

    internal static string ErrorOf(GitResult git) =>
        git.Error.Length > 0 ? git.Error.Trim() : git.Output.Length > 0 ? git.Output.Trim() : $"Git exited with code {git.Exit}.";


    /// <summary>A device's name as a ref's folder, lower case letters, digits and dashes, "device" for one with none of those.</summary>
    public static string Slug(string device)
    {
        var slug = new StringBuilder();
        foreach (char c in device.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
                slug.Append(c);
            else if (slug.Length > 0 && slug[^1] != '-')
                slug.Append('-');
            if (slug.Length >= 40)
                break;
        }

        string made = slug.ToString().Trim('-');
        return made.Length > 0 ? made : "device";
    }

    /// <summary>
    /// The working tree's changes against HEAD as a diff git applies, binary files left out (a diff of them can't be
    /// applied without their bytes). Untracked files aren't in it, as with git diff. Throws with git's reason.
    /// </summary>
    public static string Diff(string repo)
    {
        if (Why() is { } why)
            throw new InvalidOperationException(why);

        GitResult counted = GitProcess.Run(repo, ["diff", "HEAD", "--numstat", "--no-renames", "--no-ext-diff"]);
        if (!counted.Ok)
            throw new InvalidOperationException($"git diff failed, {counted.Why}");

        var arguments = new List<string> { "diff", "HEAD", "--no-color", "--no-ext-diff", "--no-renames", "--", "." };
        foreach (string line in counted.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('\t', 3);
            if (parts.Length == 3 && parts[0] == "-" && parts[1] == "-")
                arguments.Add($":(exclude,literal){parts[2].TrimEnd('\r')}");
        }

        GitResult diff = GitProcess.Run(repo, arguments);
        return diff.Ok ? diff.Output : throw new InvalidOperationException($"git diff failed, {diff.Why}");
    }

    /// <summary>A diff applied to a repository's working tree if git says it fits, null when it was, why when it wasn't.</summary>
    public static string? Apply(string repo, string diff)
    {
        if (Why() is { } why)
            return why;
        if (diff.Length == 0)
            return "The diff is empty.";
        GitResult check = GitProcess.Run(repo, ["apply", "--check", "-"], diff);
        if (!check.Ok)
            return $"It doesn't apply, {check.Why}";
        GitResult applied = GitProcess.Run(repo, ["apply", "-"], diff);
        return applied.Ok ? null : $"git apply failed, {applied.Why}";
    }

    public static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (Exception) { }
    }
}
