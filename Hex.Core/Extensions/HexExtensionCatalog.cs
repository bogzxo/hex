using System.IO.Compression;
using System.Text.RegularExpressions;

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

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepoName();

    /// <summary>Whether a source is a GitHub repository (owner/name) rather than a folder.</summary>
    public static bool IsRepo(string source) => !Directory.Exists(source) && RepoName().IsMatch(source.Trim());

    /// <summary>The page of a source, its repository on GitHub, null for a folder.</summary>
    public static string? PageOf(string source) => IsRepo(source) ? $"https://github.com/{source.Trim()}" : null;

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
                throw new IOException($"{source} is neither a folder nor a repository on GitHub (owner/name)");
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

    /// <summary>The folders of a catalog that are extensions, their extension.hor in them, by name.</summary>
    public static IEnumerable<string> Extensions(string catalog) =>
        Directory.Exists(catalog)
            ? Directory.GetDirectories(catalog).Where(folder => File.Exists(Path.Combine(folder, FILE))).Order(StringComparer.OrdinalIgnoreCase)
            : [];

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

    // Every folder with an extension.hor copied over, nothing else of the repository (its README, its tests)
    private static void CopyExtensions(string from, string to)
    {
        foreach (string folder in Extensions(from))
            Install(folder, Path.Combine(to, Path.GetFileName(folder)));
    }
}
