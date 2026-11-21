using System.Text;

namespace Hex.Core;

internal sealed partial class HexLinkTree
{
    private readonly record struct HashStamp(long Length, long Written, long Created, string Hash, string? Text, string? Eol);

    private const int CacheVersion = 1;
    private const int MaximumCacheBytes = 256 * 1024 * 1024;
    private readonly string? cacheFile;
    private readonly Lock scan = new();
    private bool cacheLoaded;
    private long computedHashes, cachedHashes;

    internal long ComputedHashes => Interlocked.Read(ref computedHashes);

    private void LoadCache()
    {
        if (cacheLoaded)
            return;
        cacheLoaded = true;
        if (cacheFile is null)
            return;

        try
        {
            using var stream = new FileStream(cacheFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaximumCacheBytes)
                return;
            using var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != CacheVersion)
                return;
            int count = reader.ReadInt32();
            if (count is < 0 or > MOST_FILES)
                return;
            var entries = new Dictionary<string, HashStamp>(count, StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                string relative = ReadCacheText(reader, 4 * HexLinkFiles.MOST_PATH) ?? string.Empty;
                long length = reader.ReadInt64(), written = reader.ReadInt64(), created = reader.ReadInt64();
                string? hash = ReadCacheText(reader, 64), text = ReadCacheText(reader, 64), eol = ReadCacheText(reader, 4);
                if (!Plain(relative) || length < 0 || hash is not { Length: 64 } || !hash.All(char.IsAsciiHexDigit) ||
                    (text is not null && (text.Length != 64 || !text.All(char.IsAsciiHexDigit))) ||
                    (eol is not null && eol is not ("lf" or "crlf" or "none")))
                    return;
                entries.Add(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)), new HashStamp(length, written, created, hash, text, eol));
            }
            if (stream.Position == stream.Length)
            {
                foreach (var (full, value) in entries)
                    hashes.TryAdd(full, value);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An absent, partially written or obsolete cache is only a cold start.
        }
    }

    private void SaveCache(IReadOnlyList<HexLinkTreeFile> files)
    {
        long changes = ComputedHashes;
        if (cacheFile is null || cachedHashes == changes)
            return;

        string temp = cacheFile + "." + Guid.NewGuid().ToString("N") + ".hexlink";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(CacheVersion);
                writer.Write(0);
                int count = 0;
                foreach (HexLinkTreeFile file in files)
                {
                    string relative = file.Path[(Folder.Name.Length + 1)..];
                    if (!hashes.TryGetValue(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)), out HashStamp entry) ||
                        entry.Hash != file.Hash)
                        continue;
                    WriteCacheText(writer, relative);
                    writer.Write(entry.Length);
                    writer.Write(entry.Written);
                    writer.Write(entry.Created);
                    WriteCacheText(writer, entry.Hash);
                    WriteCacheText(writer, entry.Text);
                    WriteCacheText(writer, entry.Eol);
                    count++;
                }
                stream.Position = sizeof(int);
                writer.Write(count);
            }
            File.Move(temp, cacheFile, overwrite: true);
            cachedHashes = changes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A cache failure does not affect the certified manifest.
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string? ReadCacheText(BinaryReader reader, int maximum)
    {
        int length = reader.ReadInt32();
        if (length == -1)
            return null;
        if (length < 0 || length > maximum)
            throw new InvalidDataException("Invalid Link tree cache field length.");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes);
    }

    private static void WriteCacheText(BinaryWriter writer, string? text)
    {
        byte[]? bytes = text is null ? null : Encoding.UTF8.GetBytes(text);
        writer.Write(bytes?.Length ?? -1);
        if (bytes is not null)
            writer.Write(bytes);
    }
}
