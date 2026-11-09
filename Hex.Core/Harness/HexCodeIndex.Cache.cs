using System.Security.Cryptography;
using System.Text;

namespace Hex.Core;

// The index kept between runs, a file in Hex's cache folder for each set of workspace folders. What's in it is each
// file's path, length, time, line count and symbols, never its text, so a warm start reads nothing but the folders and
// a file is only read when a search or a read wants its lines. A file whose length or time moved is done again as if
// the cache had never had it. Written when the index changed, no more than every few seconds, and when Hex closes.
internal sealed partial class HexCodeIndex
{
    // What the file starts with, a cache of another format thrown away rather than misread
    private const string CACHE_MAGIC = "HEXIDX3";

    // The least time between two writes of the cache, in milliseconds
    private const int CACHE_EVERY = 5000;

    private readonly string? cacheFolder;
    private Dictionary<string, (long Length, long Written, int Lines, List<HexSymbol> Symbols, bool FromServer)>? cached;
    private string? cacheFile;
    private bool cacheDirty;
    private long cacheSaved;

    /// <summary>The file the index of a set of folders is kept in, by a hash of them so two workspaces don't share one.</summary>
    public static string CacheFileFor(string folder, IReadOnlyList<string> roots)
    {
        string joined = string.Join('\n', roots.Select(root => root.ToLowerInvariant()).Order(StringComparer.Ordinal));
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..16].ToLowerInvariant();
        return Path.Combine(folder, $"index-{hash}.bin");
    }

    /// <summary>How many files came out of the cache on disk the last time it was read, for the stats and the tests.</summary>
    public int FromCache { get; private set; }

    // Read once for a set of folders, again when they change. Under the lock
    private void LoadCacheOnce(string[] now)
    {
        if (cacheFolder is null || now.Length == 0)
            return;

        string file = CacheFileFor(cacheFolder, now);
        if (file == cacheFile)
            return;

        cacheFile = file;
        cached = [];
        FromCache = 0;
        try
        {
            if (!File.Exists(file))
                return;

            using var reader = new BinaryReader(File.OpenRead(file), Encoding.UTF8);
            if (reader.ReadString() != CACHE_MAGIC)
                return;

            int count = reader.ReadInt32();
            for (int f = 0; f < count; f++)
            {
                string path = reader.ReadString();
                long length = reader.ReadInt64(), written = reader.ReadInt64();
                int lines = reader.ReadInt32();
                bool fromServer = reader.ReadBoolean();
                int symbolCount = reader.ReadInt32();
                var symbols = new List<HexSymbol>(symbolCount);
                for (int s = 0; s < symbolCount; s++)
                    symbols.Add(new HexSymbol(reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()));
                cached[path] = (length, written, lines, symbols, fromServer);
            }
        }
        catch (Exception)
        {
            // A cache cut short or from a Hex that wrote it differently is no cache, the files are read instead
            cached.Clear();
        }
    }

    // What the cache had of a file, when it was the same length and time as now
    private (int Lines, List<HexSymbol> Symbols, bool FromServer)? Cached(string path, long length, long written)
    {
        if (cached is null || !cached.TryGetValue(path, out var kept) || kept.Length != length || kept.Written != written)
            return null;

        FromCache++;
        return (kept.Lines, kept.Symbols, kept.FromServer);
    }

    /// <summary>The index written to its cache file now, whatever the time since the last, as Hex closes.</summary>
    public void SaveCache()
    {
        lock (looking)
            SaveCacheNow(force: true);
    }

    // Under the lock. Written to a file beside it and moved over it, a Hex killed halfway leaves the old one whole
    private void SaveCacheNow(bool force)
    {
        if (cacheFile is null || !cacheDirty || (!force && Environment.TickCount64 - cacheSaved < CACHE_EVERY))
            return;

        HexIndexSnapshot now = snapshot;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            string writing = cacheFile + ".new";
            using (var writer = new BinaryWriter(File.Create(writing), Encoding.UTF8))
            {
                writer.Write(CACHE_MAGIC);
                writer.Write(now.Files.Length);
                foreach (HexIndexedFile file in now.Files)
                {
                    writer.Write(file.Path);
                    writer.Write(file.Length);
                    writer.Write(file.Written);
                    writer.Write(file.LineCount);
                    writer.Write(file.FromServer);
                    writer.Write(file.Symbols.Count);
                    foreach (HexSymbol symbol in file.Symbols)
                    {
                        writer.Write(symbol.Name);
                        writer.Write(symbol.Kind);
                        writer.Write(symbol.Container);
                        writer.Write(symbol.Line);
                        writer.Write(symbol.Column);
                        writer.Write(symbol.Depth);
                    }
                }
            }

            File.Move(writing, cacheFile, overwrite: true);
            cacheDirty = false;
            cacheSaved = Environment.TickCount64;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Another Hex writing it at the same moment, or a full disk, it's only a cache
        }
    }
}
