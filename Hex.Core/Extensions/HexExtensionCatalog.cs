using System.IO.Compression;
using System.Text.RegularExpressions;
using Hex.Core.Git;

namespace Hex.Core;

/// <summary>
/// The extensions that can be installed, a GitHub repository with a folder an extension (bogzxo/hex-extensions), or a
/// folder on this machine laid out the same for writing them and for the self test. A sync is the repository's main
/// branch downloaded as one zip (codeload, which isn't the API and its sixty an hour) and every folder of it with an
/// extension.hor kept in a folder of Hex's, what the settings list and an install copies from. Every call is for the
/// background, and throws when GitHub can't be reached or says no.
/// </summary>
internal static partial class HexExtensionCatalog
{
    public const string FILE = "extension.hor";
    public static bool IsLegacyMarkdownPreview(string folder) => Path.GetFileName(folder).Equals("markdown_preview", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepoName();

    /// <summary>Whether a source is a GitHub repository (owner/name) rather than a folder.</summary>
    public static bool IsRepo(string source) => !Directory.Exists(source) && RepoName().IsMatch(source.Trim());

    /// <summary>
    /// Whether a source is a git repository by its URL (https, ssh or git@host:path, cloned with the git on the PATH),
    /// rather than a GitHub owner/name or a folder.
    /// </summary>
    public static bool IsGit(string source)
    {
        string tidy = source.Trim();
        if (Directory.Exists(tidy)) return false;
        return tidy.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || tidy.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || tidy.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) || tidy.StartsWith("git://", StringComparison.OrdinalIgnoreCase)
            || tidy.StartsWith("git@", StringComparison.OrdinalIgnoreCase) || tidy.EndsWith(".git", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What sort of source it is, for the list of them, GitHub, git or a folder.</summary>
    public static HexExtensionSourceKind KindOf(string source) =>
        IsGit(source) ? HexExtensionSourceKind.Git : IsRepo(source) ? HexExtensionSourceKind.GitHub : HexExtensionSourceKind.Folder;

    /// <summary>Whether a source could be one at all, a folder that's there, owner/name or a git URL.</summary>
    public static bool IsSource(string source) => Directory.Exists(source.Trim()) || IsRepo(source) || IsGit(source);

    /// <summary>The page of a source, its repository on GitHub or the web page of a git URL, null for a folder or ssh.</summary>
    public static string? PageOf(string source)
    {
        string tidy = source.Trim();
        if (IsRepo(tidy)) return $"https://github.com/{tidy}";
        if (IsGit(tidy) && tidy.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return tidy.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? tidy[..^4] : tidy;
        return null;
    }

    /// <summary>A name for a source's own folder among the synced ones, its letters and digits and a hash so two never meet.</summary>
    public static string FolderNameOf(string source)
    {
        string tidy = source.Trim().Replace('\\', '/').TrimEnd('/');
        string readable = new([.. tidy.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')]);
        readable = readable.Trim('-');
        if (readable.Length > 40) readable = readable[^40..].TrimStart('-');
        uint hash = 2166136261;
        foreach (char c in tidy.ToLowerInvariant()) hash = (hash ^ c) * 16777619;
        return $"{readable}-{hash:x8}";
    }

    /// <summary>
    /// The catalog brought up to date in <paramref name="into"/>, a folder an extension and nothing else. What was there is
    /// only replaced once the new one is all there, a sync that fails half way leaves the last one as it was.
    /// </summary>
    public static async Task Sync(string source, string into, CancellationToken cancel = default)
    {
        source = source.Trim();
        string staging = into + ".new";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);

        try
        {
            if (Directory.Exists(source))
            {
                CopyExtensions(source, staging);
            }
            else if (IsGit(source))
            {
                await CloneInto(source, staging, cancel).ConfigureAwait(false);
            }
            else if (RepoName().IsMatch(source))
            {
                string zip = Path.Combine(Path.GetTempPath(), $"hex-extensions-{Guid.NewGuid():N}.zip");
                string unpacked = zip[..^4];
                try
                {
                    await HexReleases.Download($"https://codeload.github.com/{source}/zip/refs/heads/main", zip, cancel: cancel).ConfigureAwait(false);
                    ZipFile.ExtractToDirectory(zip, unpacked);

                    // GitHub puts the lot in a folder named after the repository and the branch
                    string[] tops = Directory.GetDirectories(unpacked);
                    CopyExtensions(tops.Length == 1 ? tops[0] : unpacked, staging);
                }
                finally
                {
                    File.Delete(zip);
                    if (Directory.Exists(unpacked))
                        Directory.Delete(unpacked, recursive: true);
                }
            }
            else
            {
                throw new IOException($"{source} is neither a folder, a repository on GitHub (owner/name) nor a git URL");
            }

            if (Directory.Exists(into))
                Directory.Delete(into, recursive: true);
            Directory.Move(staging, into);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>
    /// The folders of a catalog that are extensions, their extension.hor in them, by name. With No AI on the agent
    /// systems aren't among them, there's nothing for them to be installed into.
    /// </summary>
    public static IEnumerable<string> Extensions(string catalog) =>
        Directory.Exists(catalog)
            ? Directory.GetDirectories(catalog).Where(folder => !IsLegacyMarkdownPreview(folder) && File.Exists(Path.Combine(folder, FILE))
                && !(HexAi.Off && IsAgentSystem(folder))).Order(StringComparer.OrdinalIgnoreCase)
            : [];

    /// <summary>
    /// Whether the extension in a folder is an agent system (Claude Code, Codex, Antigravity), known by its file saying
    /// who its agent is with hex.agent. Read as text rather than run, an extension No AI leaves out never runs at all.
    /// </summary>
    public static bool IsAgentSystem(string folder)
    {
        try
        {
            string file = Path.Combine(folder, FILE);
            return File.Exists(file) && File.ReadAllText(file).Contains("hex.agent(", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// An extension of the catalog put in a folder, installed or brought up to date. Its files are written over the ones
    /// there (an extension that is running is watching them) and the ones it doesn't have any more taken out.
    /// </summary>
    public static void Install(string from, string to)
    {
        Directory.CreateDirectory(to);
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(from, file);
            string target = Path.Combine(to, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            kept.Add(relative);
        }

        foreach (string file in Directory.GetFiles(to, "*", SearchOption.AllDirectories))
        {
            if (!kept.Contains(Path.GetRelativePath(to, file)))
                File.Delete(file);
        }
    }

    /// <summary>Whether a version is newer than another, 1.10.0 after 1.9.2, a v in front or not.</summary>
    public static bool IsNewer(string version, string than)
    {
        if (Version.TryParse(Tidy(version), out Version? one) && Version.TryParse(Tidy(than), out Version? other))
            return one > other;
        return version.Length > 0 && !string.Equals(version, than, StringComparison.OrdinalIgnoreCase) && string.CompareOrdinal(version, than) > 0;
    }

    // 1.0 and 1 are versions to System.Version only with a dot, and a v in front isn't one at all
    private static string Tidy(string version)
    {
        string tidy = version.Trim().TrimStart('v', 'V');
        int cut = tidy.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0)
            tidy = tidy[..cut];
        return tidy.Contains('.') ? tidy : tidy + ".0";
    }

    // A git repository cloned shallow into a folder of its own and its extensions copied over, the clone thrown away after.
    // git does the asking for a password itself, an ssh key or the credential manager, and a clone that can't stops it
    private static async Task CloneInto(string url, string staging, CancellationToken cancel)
    {
        string clone = Path.Combine(Path.GetTempPath(), $"hex-extensions-{Guid.NewGuid():N}");
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("git")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string argument in (string[])["clone", "--depth", "1", "--quiet", url.Trim(), clone])
                start.ArgumentList.Add(argument);
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";

            using var git = GitProcess.StartEnabled(start) ?? throw new IOException(GitProcess.Enabled
                ? "git couldn't be started, is it installed and on the PATH?" : GitProcess.DisabledReason);
            Task<string> said = git.StandardError.ReadToEndAsync(cancel);
            await git.StandardOutput.ReadToEndAsync(cancel).ConfigureAwait(false);
            await git.WaitForExitAsync(cancel).ConfigureAwait(false);
            if (git.ExitCode != 0)
                throw new IOException($"git couldn't clone {url.Trim()}: {(await said.ConfigureAwait(false)).Trim()}");

            CopyExtensions(clone, staging);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new IOException("git isn't installed or isn't on the PATH, a git URL needs it");
        }
        finally
        {
            if (Directory.Exists(clone))
            {
                // git makes its pack files read only, which Directory.Delete won't take out
                foreach (string file in Directory.GetFiles(clone, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(clone, recursive: true);
            }
        }
    }

    // Every folder with an extension.hor copied over, nothing else of the repository (its README, its tests)
    private static void CopyExtensions(string from, string to)
    {
        foreach (string folder in Extensions(from))
            Install(folder, Path.Combine(to, Path.GetFileName(folder)));
    }
}

/// <summary>What a place extensions come from is.</summary>
internal enum HexExtensionSourceKind
{
    /// <summary>A repository on GitHub, owner/name, its main branch downloaded as a zip.</summary>
    GitHub,

    /// <summary>A git repository by its URL, cloned with git.</summary>
    Git,

    /// <summary>A folder on this machine.</summary>
    Folder
}
