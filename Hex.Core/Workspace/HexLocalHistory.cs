using System.Security.Cryptography;
using System.Text;

namespace Hex.Core;

/// <summary>A version of a file kept by local history, when it was saved and the copy of it.</summary>
internal readonly record struct HexKeptVersion(DateTime When, string Copy, long Bytes);

/// <summary>
/// Local history, a copy of a file every time it's saved, kept apart from it, for going back to what it was an hour ago
/// whether or not git has it. Under a folder of its own a folder each file, named by a hash of its path (with the path
/// in a file inside, to be read), a copy each save named by when. A save the same as the last one isn't kept again, and
/// the oldest go past so many a file or so many days. A file too big isn't kept at all (a log, a dump), and once all of
/// it together is past so much the oldest copies of any file go, so it never eats the disk.
/// </summary>
internal static class HexLocalHistory
{
    public const int MOST_VERSIONS = 50;
    public static readonly TimeSpan MostAge = TimeSpan.FromDays(30);

    /// <summary>The biggest file kept, in characters, and the most all the copies together take, in bytes.</summary>
    public const int MOST_CHARACTERS = 4 * 1024 * 1024;
    public const long MOST_BYTES = 256L * 1024 * 1024;

    private const string PATH_FILE = "path.txt", COPY_EXTENSION = ".copy", STAMP = "yyyyMMdd-HHmmss-fff";

    /// <summary>Keeps a copy of a file as it was saved, unless the last one kept is the same. Never throws, local history going wrong mustn't stop a save.</summary>
    public static void Keep(string root, string path, string text, DateTime? when = null, long mostBytes = MOST_BYTES)
    {
        if (text.Length > MOST_CHARACTERS)
            return;

        try
        {
            string folder = FolderOf(root, path);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, PATH_FILE), Path.GetFullPath(path));

            List<HexKeptVersion> kept = Versions(root, path);
            if (kept.Count > 0 && File.ReadAllText(kept[0].Copy) == text)
                return;

            DateTime stamp = when ?? DateTime.Now;
            File.WriteAllText(Path.Combine(folder, stamp.ToString(STAMP) + COPY_EXTENSION), text);
            Prune(root, path, stamp);
            PruneAll(root, mostBytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Whether any version of a file is kept, for keeping what it was before the first save of it.</summary>
    public static bool Has(string root, string path) =>
        Directory.Exists(FolderOf(root, path)) && Directory.EnumerateFiles(FolderOf(root, path), "*" + COPY_EXTENSION).Any();

    /// <summary>The versions kept of a file, the newest first.</summary>
    public static List<HexKeptVersion> Versions(string root, string path)
    {
        string folder = FolderOf(root, path);
        var versions = new List<HexKeptVersion>();
        if (!Directory.Exists(folder))
            return versions;

        foreach (string copy in Directory.EnumerateFiles(folder, "*" + COPY_EXTENSION))
        {
            if (StampOf(copy) is { } when)
                versions.Add(new HexKeptVersion(when, copy, new FileInfo(copy).Length));
        }

        versions.Sort((a, b) => b.When.CompareTo(a.When));
        return versions;
    }

    private static DateTime? StampOf(string copy) =>
        DateTime.TryParseExact(Path.GetFileNameWithoutExtension(copy), STAMP, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime when) ? when : null;

    // The oldest let go of, past the most a file has or the oldest it's kept for
    private static void Prune(string root, string path, DateTime now)
    {
        List<HexKeptVersion> kept = Versions(root, path);
        for (int i = 0; i < kept.Count; i++)
        {
            if (i >= MOST_VERSIONS || now - kept[i].When > MostAge)
                File.Delete(kept[i].Copy);
        }
    }

    // All the copies of every file together past the most they take, the oldest of any go until they're under it
    private static void PruneAll(string root, long mostBytes)
    {
        var all = new List<(DateTime When, string Copy, long Bytes)>();
        long total = 0;
        foreach (string copy in Directory.EnumerateFiles(root, "*" + COPY_EXTENSION, SearchOption.AllDirectories))
        {
            long bytes = new FileInfo(copy).Length;
            total += bytes;
            if (StampOf(copy) is { } when)
                all.Add((when, copy, bytes));
        }

        if (total <= mostBytes)
            return;

        all.Sort((a, b) => a.When.CompareTo(b.When));
        foreach (var (_, copy, bytes) in all)
        {
            if (total <= mostBytes)
                break;
            File.Delete(copy);
            total -= bytes;
        }
    }

    // A file's folder, by a hash of its whole path (paths compare without case where the system does)
    private static string FolderOf(string root, string path)
    {
        string full = Path.GetFullPath(path);
        if (PlatformPaths.Comparison == StringComparison.OrdinalIgnoreCase)
            full = full.ToLowerInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(full));
        return Path.Combine(root, Convert.ToHexString(hash, 0, 8).ToLowerInvariant());
    }
}
