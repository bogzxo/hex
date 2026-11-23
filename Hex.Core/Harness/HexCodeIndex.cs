namespace Hex.Core;

/// <summary>
/// What the files of the workspace are, as they were the last time the index looked, never changed once it's made. A
/// query takes one and works on it however long it takes while the index moves on to the next.
/// </summary>
internal sealed class HexIndexSnapshot(long generation, string[] roots, HexIndexedFile[] files)
{
    private Dictionary<string, List<(HexIndexedFile File, HexSymbol Symbol)>>? byName;
    private Dictionary<string, HexIndexedFile>? byPath;

    /// <summary>Goes up by one whenever anything the index knows changed, what answers are kept by.</summary>
    public long Generation { get; } = generation;

    public string[] Roots { get; } = roots;

    /// <summary>Every file, in the order of their paths.</summary>
    public HexIndexedFile[] Files { get; } = files;

    /// <summary>Every declaration by its name in lower case, made the first time it's asked for.</summary>
    public Dictionary<string, List<(HexIndexedFile File, HexSymbol Symbol)>> ByName => byName ??= MakeByName();

    /// <summary>
    /// Whether a file is in a project's folder (a .csproj, package.json, Cargo.toml, ... of the index in its folder or one
    /// above it), what a declaration a language server has loaded is told from one in a loose copy by.
    /// </summary>
    public bool InProject(HexIndexedFile file)
    {
        projectFolders ??= new HashSet<string>(Files.Where(each => HexCodeIndex.IsProjectFile(each.Relative)).Select(each => Path.GetDirectoryName(each.Path) ?? string.Empty), StringComparer.OrdinalIgnoreCase);
        for (string? folder = Path.GetDirectoryName(file.Path); folder is { Length: > 0 }; folder = Path.GetDirectoryName(folder))
        {
            if (projectFolders.Contains(folder))
                return true;
            if (Roots.Any(root => root.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                break;
        }

        return false;
    }

    private HashSet<string>? projectFolders;

    /// <summary>A file by its whole path, case ignored the way Windows has it.</summary>
    public HexIndexedFile? File(string path)
    {
        byPath ??= Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        return byPath.GetValueOrDefault(path);
    }

    private Dictionary<string, List<(HexIndexedFile, HexSymbol)>> MakeByName()
    {
        var made = new Dictionary<string, List<(HexIndexedFile, HexSymbol)>>(StringComparer.Ordinal);
        foreach (HexIndexedFile file in Files)
        {
            foreach (HexSymbol symbol in file.Symbols)
            {
                string key = symbol.Name.ToLowerInvariant();
                if (!made.TryGetValue(key, out var all))
                    made[key] = all = [];
                all.Add((file, symbol));
            }
        }

        return made;
    }
}

/// <summary>A file that came, changed or went, at the generation the index saw it.</summary>
internal readonly record struct HexChange(long Generation, string Relative, char What)
{
    public const char ADDED = '+', CHANGED = '~', GONE = '-';
}

/// <summary>
/// The agent harness's index, every text file of the workspace with what it declares, kept in memory and brought up to
/// date when it's asked something and its last look is a couple of seconds old. Only a file whose length or time
/// changed is read again. Nothing of the editor's is in it, so it runs on any thread and the tests build one on a temp
/// folder. The queries are in partials by what they answer, the cache on disk and the log of changes too.
/// </summary>
internal sealed partial class HexCodeIndex
{
    private readonly Func<IReadOnlyList<string>> roots;
    private readonly Lock looking = new();
    private volatile HexIndexSnapshot snapshot = new(0, [], []);
    private long lastLook = long.MinValue;

    /// <summary>How long a look at the folders is good for, in milliseconds, before a query looks again.</summary>
    public int StaleAfter { get; init; } = 2000;

    /// <summary>What the harness was asked and what it handed back, for hex_harness_stats.</summary>
    public HexHarnessStats Stats { get; }

    /// <summary>Called off the lock with the new generation when a look found something changed.</summary>
    public event Action<long>? Changed;

    /// <param name="roots">The folders of the workspace, asked every look so a folder added is seen.</param>
    /// <param name="cacheFolder">Where the index is kept between runs, null for not at all.</param>
    public HexCodeIndex(Func<IReadOnlyList<string>> roots, string? cacheFolder = null)
    {
        this.roots = roots;
        this.cacheFolder = cacheFolder;
        Stats = new HexHarnessStats(cacheFolder is null ? null : Path.Combine(cacheFolder, "stats.tsv"));
    }

    private int touched;

    /// <summary>Something changed on the disk that the index can't wait two seconds to know of (a file an agent just wrote), the next look is made whatever the last one's age.</summary>
    public void Touch() => Volatile.Write(ref touched, 1);

    /// <summary>The index as it is now, looked at again first when the last look is stale (or it's forced).</summary>
    public HexIndexSnapshot Current(bool force = false)
    {
        Look(force);
        return snapshot;
    }

    /// <summary>The index as the last look left it, without looking, for the editor's thread where a walk would stall it.</summary>
    public HexIndexSnapshot Latest => snapshot;

    /// <summary>The generation of the last look, without looking.</summary>
    public long Generation => snapshot.Generation;

    /// <summary>
    /// The folders walked and every file whose length or time changed read again, the ones that didn't kept as they
    /// were. New files are read on every core at once, a cold index over a big repository is mostly that.
    /// </summary>
    public void Look(bool force = false)
    {
        long changedTo = -1;
        lock (looking)
        {
            // A file changed through the harness itself is in the very next answer, not when the last look is stale
            force |= Interlocked.Exchange(ref touched, 0) == 1;
            string[] now = [.. roots().Select(root => Path.GetFullPath(root).TrimEnd('\\', '/'))];
            HexIndexSnapshot was = snapshot;
            if (!force && Environment.TickCount64 - lastLook < StaleAfter && now.SequenceEqual(was.Roots, StringComparer.OrdinalIgnoreCase))
                return;

            lastLook = Environment.TickCount64;
            LoadCacheOnce(now);

            // The length and time of each file come with the walk, out of its folder's listing
            List<HexWalkedFile> paths = now.Length == 0 ? [] : HexSearch.TextFilesWithFactsUnder(now);
            var made = new HexIndexedFile[paths.Count];
            var toRead = new List<int>();
            for (int i = 0; i < paths.Count; i++)
            {
                var (path, length, written) = paths[i];
                if (was.File(path) is { } kept && kept.Length == length && kept.Written == written)
                    made[i] = ServerSymbolsFor(kept);
                else if (Cached(path, length, written) is { } fromDisk)
                    made[i] = ServerSymbolsFor(new HexIndexedFile(path, HexSearch.RelativeTo(now, path), length, written, fromDisk.Lines, fromDisk.Symbols, fromDisk.FromServer));
                else
                {
                    made[i] = new HexIndexedFile(path, HexSearch.RelativeTo(now, path), length, written, []);
                    toRead.Add(i);
                }
            }

            Parallel.ForEach(toRead, i =>
            {
                HexIndexedFile stub = made[i];
                made[i] = ServerSymbolsFor(HexIndexedFile.Read(stub.Path, stub.Relative, stub.Length, stub.Written));
            });

            bool rootsMoved = !now.SequenceEqual(was.Roots, StringComparer.OrdinalIgnoreCase);
            int logged = rootsMoved ? 0 : Log(was, made);
            if (logged > 0 || rootsMoved || symbolsMoved || made.Length != was.Files.Length)
            {
                symbolsMoved = false;
                snapshot = new HexIndexSnapshot(was.Generation + 1, now, made);
                changedTo = snapshot.Generation;
                cacheDirty = true;
            }

            SaveCacheNow(force: false);
        }

        if (changedTo >= 0)
            Changed?.Invoke(changedTo);
    }
}
