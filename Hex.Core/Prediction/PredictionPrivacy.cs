namespace Hex.Core;

/// <summary>
/// Whether a file may be sent to a model at all: it has to be inside one of the folders open, no bigger than a
/// megabyte, and match none of the patterns (the usual ones for files that hold secrets, the user's, a folder's, and
/// what files_exclude hides). Patterns are HexGlob's, matched against the path inside its folder and every folder on the
/// way to it.
/// </summary>
internal sealed class PredictionPrivacy
{
    /// <summary>The largest file sent, a megabyte.</summary>
    public const long MOST_BYTES = 1024 * 1024;

    /// <summary>The files never sent unless the user takes them out of the setting: keys, certificates, env files and the like.</summary>
    public static IReadOnlyList<string> UsualExclude { get; } =
    [
        "**/.env", "**/.env.*", "**/*.pem", "**/*.key", "**/*.pfx", "**/*.p12", "**/id_rsa*", "**/id_ed25519*",
        "**/*secret*", "**/*credential*", "**/.npmrc", "**/.pypirc", "**/.netrc", "**/*.kdbx",
    ];

    private readonly HexGlob[] globs;
    private readonly string[] roots;

    public PredictionPrivacy(IReadOnlyList<string> patterns, IReadOnlyList<string> roots)
    {
        globs = HexGlob.All(patterns);
        this.roots = [.. roots.Where(root => root.Length > 0).Select(Folder)];
    }

    /// <summary>Whether the file at a path, of so many bytes, may be sent.</summary>
    public bool Allowed(string path, long size)
    {
        if (size > MOST_BYTES || path.Length == 0)
            return false;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        foreach (string root in roots)
        {
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                continue;
            return !Excluded(full[root.Length..].Replace('\\', '/'));
        }
        return false;
    }

    // Whether the path inside its folder, or a folder on the way to it, matches a pattern
    private bool Excluded(string relative)
    {
        if (relative.Length == 0)
            return true;

        string[]? split = null;
        foreach (HexGlob glob in globs)
        {
            if (glob.Matches(relative, false, ref split))
                return true;
        }

        for (int slash = relative.IndexOf('/'); slash > 0; slash = relative.IndexOf('/', slash + 1))
        {
            string folder = relative[..slash];
            string[]? parts = null;
            foreach (HexGlob glob in globs)
            {
                if (glob.Matches(folder, true, ref parts))
                    return true;
            }
        }
        return false;
    }

    // A folder's full path ending in a separator, so that C:\src doesn't hold C:\src2
    private static string Folder(string root)
    {
        string full = Path.GetFullPath(root);
        return Path.EndsInDirectorySeparator(full) ? full : full + Path.DirectorySeparatorChar;
    }
}
