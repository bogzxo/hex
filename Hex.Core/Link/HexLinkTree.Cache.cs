using System.Text;

namespace Hex.Core;

internal sealed partial class HexLinkTree
{
    private readonly record struct HashStamp(long Length, long Written, long Created, string Hash, string? Text, string? Eol);

    private const int CacheVersion = 2;
    private const int MaximumCacheBytes = 256 * 1024 * 1024;
    private readonly string? cacheFile;
    private readonly Lock scan = new();
    private bool cacheLoaded;
    private long computedHashes, cachedHashes, modeChanges, cachedModes;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> rememberedModes = new(PlatformPaths.Comparer);

    internal bool? Mode(string full) => OperatingSystem.IsWindows()
        ? rememberedModes.TryGetValue(full, out bool stored) ? stored : null
        : File.GetUnixFileMode(full).HasFlag(UnixFileMode.UserExecute);

    internal void RememberMode(string full, bool? executable)
    {
        if (OperatingSystem.IsWindows() && executable is { } mode &&
            (!rememberedModes.TryGetValue(full, out bool before) || before != mode))
        {
            rememberedModes[full] = mode;
            Interlocked.Increment(ref modeChanges);
            InvalidateSnapshot();
        }
    }

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
            int version = reader.ReadInt32();
            if (version is not (1 or CacheVersion))
                return;
            int count = reader.ReadInt32();
            if (count is < 0 or > MOST_FILES)
                return;
            var entries = new Dictionary<string, HashStamp>(count, StringComparer.Ordinal);
            var modes = new Dictionary<string, bool>(PlatformPaths.Comparer);
            for (int i = 0; i < count; i++)
            {
                string relative = ReadCacheText(reader, 4 * HexLinkFiles.MOST_PATH) ?? string.Empty;
                long length = reader.ReadInt64(), written = reader.ReadInt64(), created = reader.ReadInt64();
                string? hash = ReadCacheText(reader, 64), text = ReadCacheText(reader, 64), eol = ReadCacheText(reader, 4);
                if (!Plain(relative) || length < 0 || hash is not { Length: 64 } || !hash.All(char.IsAsciiHexDigit) ||
                    (text is not null && (text.Length != 64 || !text.All(char.IsAsciiHexDigit))) ||
                    (eol is not null && eol is not ("lf" or "crlf" or "none")))
                    return;
                string full = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
                entries.Add(full, new HashStamp(length, written, created, hash, text, eol));
                if (version >= 2 && reader.ReadByte() is byte mode && mode != 0)
                    modes[full] = mode == 2;
            }
            if (stream.Position == stream.Length)
            {
                foreach (var (full, value) in entries)
                    hashes.TryAdd(full, value);
                if (OperatingSystem.IsWindows())
                {
                    foreach (var (full, mode) in modes)
                        rememberedModes.TryAdd(full, mode);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An absent, partially written or obsolete cache is only a cold start.
        }
    }

    private void SaveCache(IReadOnlyList<HexLinkTreeFile> files)
    {
        long changes = ComputedHashes, modes = Interlocked.Read(ref modeChanges);
        if (cacheFile is null || (cachedHashes == changes && cachedModes == modes))
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
                    writer.Write((byte)(file.Executable switch { true => 2, false => 1, null => 0 }));
                    count++;
                }
                stream.Position = sizeof(int);
                writer.Write(count);
            }
            File.Move(temp, cacheFile, overwrite: true);
            cachedHashes = changes;
            cachedModes = modes;
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
