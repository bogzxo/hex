namespace Hex.Core;

/// <summary>
/// The text files a sync last agreed on, kept on the disk by the hash of their bytes so a merge still has its common
/// base after Hex restarts or the in-memory cache (<see cref="HexLinkMerge.CachedBase"/>) has let it go. Without the
/// base two edits of the same file can only be compared as two whole files, one conflict from top to bottom; with it
/// each region both sides touched is one conflict and the rest merges.
/// <para>
/// It's content addressed: the file name is the SHA-256 of what's in it, so a base is never overwritten (the same name
/// is the same bytes), is only believed when its bytes still hash to its name, and the sync's state only has to keep
/// hashes to find them again. Only text of at most <see cref="HexLinkMerge.CACHE_FILE_BYTES"/> is kept, the most the
/// merge looks at anyway, and the folder is held under <see cref="MOST_BYTES"/> by letting the oldest go. A base that
/// can't be written, read or is gone costs a two-way comparison, never a file's content, so every error here is
/// swallowed the way the state file's are.
/// </para>
/// </summary>
internal sealed class HexLinkMergeBases(string folder)
{
    /// <summary>The most the folder keeps in all, over which the oldest bases are deleted.</summary>
    public const long MOST_BYTES = 64L * 1024 * 1024;

    private readonly Lock gate = new();

    /// <summary>Keeps <paramref name="bytes"/> when it's text small enough to merge and isn't kept yet. Returns whether it's there afterwards.</summary>
    public bool Add(byte[] bytes)
    {
        if (bytes.Length > HexLinkMerge.CACHE_FILE_BYTES || HexLinkFiles.Decode(bytes) is null)
            return false;
        string hash = HexLinkFiles.Hash(bytes);
        try
        {
            lock (gate)
            {
                string full = Path.Combine(folder, hash);
                if (File.Exists(full))
                    return true;
                Directory.CreateDirectory(folder);
                HexLinkFiles.WriteAtomic(full, bytes);
                Trim();
                return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The bytes kept under <paramref name="hash"/>, null when there are none or they aren't what the name says.</summary>
    public byte[]? Get(string hash)
    {
        if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))
            return null;
        try
        {
            string full = Path.Combine(folder, hash.ToLowerInvariant());
            if (!File.Exists(full) || new FileInfo(full).Length > HexLinkMerge.CACHE_FILE_BYTES)
                return null;
            byte[] bytes = File.ReadAllBytes(full);
            return HexLinkFiles.Hash(bytes) == hash.ToLowerInvariant() ? bytes : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Deletes every base whose hash isn't in <paramref name="keep"/>, the ones nothing in the sync's state points at any more.</summary>
    public void Prune(IEnumerable<string> keep)
    {
        var kept = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
        try
        {
            lock (gate)
            {
                if (!Directory.Exists(folder))
                    return;
                foreach (string full in Directory.EnumerateFiles(folder))
                {
                    if (kept.Contains(Path.GetFileName(full)))
                        continue;
                    try
                    {
                        File.Delete(full);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // Left for the next prune
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Held under MOST_BYTES by deleting the oldest written first, under the gate
    private void Trim()
    {
        var files = new DirectoryInfo(folder).EnumerateFiles().ToList();
        long total = files.Sum(each => each.Length);
        foreach (FileInfo each in files.OrderBy(each => each.LastWriteTimeUtc))
        {
            if (total <= MOST_BYTES)
                break;
            try
            {
                long size = each.Length;
                each.Delete();
                total -= size;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
