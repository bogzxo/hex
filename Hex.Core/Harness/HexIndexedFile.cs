namespace Hex.Core;

/// <summary>
/// Something a file declares as the harness keeps it, its name, what kind of thing it is (class, method, field and so
/// on, the outline's icon without the symbol- in front), what it's inside of and where. Lines and columns from 0.
/// </summary>
internal sealed record HexSymbol(string Name, string Kind, string Container, int Line, int Column, int Depth)
{
    /// <summary>The name with what it's in in front, McpSchema.Check.</summary>
    public string Full => Container.Length == 0 ? Name : $"{Container}.{Name}";

    /// <summary>Whether it's a type of some sort rather than a member or a value, what the map lists for a folder.</summary>
    public bool IsType => Kind is "class" or "interface" or "struct" or "enum" or "namespace";

    /// <summary>
    /// Symbols out of outline entries, what each is inside of worked out from how deep it is. An entry is inside the last
    /// one before it that was less deep.
    /// </summary>
    public static HexSymbol[] Of(IEnumerable<OutlineEntry> entries)
    {
        if (entries is ICollection<OutlineEntry> { Count: 0 })
            return [];

        var symbols = new List<HexSymbol>();
        var open = new List<(int Depth, string Full)>();
        foreach (OutlineEntry entry in entries)
        {
            // An outline line with no name (an operator, a shape the regular expressions half matched) is nothing to find
            if (string.IsNullOrWhiteSpace(entry.Name))
                continue;

            while (open.Count > 0 && open[^1].Depth >= entry.Depth)
                open.RemoveAt(open.Count - 1);

            var symbol = new HexSymbol(entry.Name, KindOf(entry.Icon), open.Count > 0 ? open[^1].Full : string.Empty, entry.Line, entry.Column, entry.Depth);
            symbols.Add(symbol);
            open.Add((entry.Depth, symbol.Full));
        }

        return symbols.Count == 0 ? [] : symbols.ToArray();
    }

    // The kinds made so far, by the outline's icon or as the cache on disk says them. There are a dozen or so, and every
    // symbol of the workspace had its own copy of one
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> kinds = new(StringComparer.Ordinal);

    /// <summary>The kind of an outline icon (symbol-method is method), one string shared by every symbol of that kind.</summary>
    public static string KindOf(string icon) =>
        kinds.GetOrAdd(icon, static each => each.Replace("symbol-", string.Empty));

    /// <summary>A kind as the cache on disk said it, the one string kept for it.</summary>
    public static string SharedKind(string kind) => kinds.GetOrAdd(kind, kind);
}

/// <summary>
/// A file of the workspace as the harness has it, kept until its length or the time it was written changes. What it
/// declares is known from the start, its text is read the first time something needs it (a file that came out of the
/// cache on disk hasn't been read at all). Nothing of it changes once it's made, so any thread can read it.
/// </summary>
internal sealed class HexIndexedFile
{
    // The extensions seen so far, lower case with the dot. There are a few dozen across the whole workspace, shared
    // rather than allocated per file on every walk or cache load
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> extensions = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> extensionsLookup =
        extensions.GetAlternateLookup<ReadOnlySpan<char>>();

    // Strong lines only for synthetic/in-memory files (editor buffers, held reads, unreadable stubs); disk-backed files
    // keep a weak reference so burst queries reuse them while the GC can reclaim cold file text
    private readonly string[]? inMemoryLines;
    private readonly Func<string[]>? readLines;
    private WeakReference<string[]>? cachedLines;

    public string Path { get; }

    /// <summary>Where it is inside the workspace's folders, forward slashes, what an agent is shown.</summary>
    public string Relative { get; }

    /// <summary>Its extension in lower case with the dot, ".cs".</summary>
    public string Extension { get; }

    public long Length { get; }
    public long Written { get; }
    public int LineCount { get; }
    public IReadOnlyList<HexSymbol> Symbols { get; }

    /// <summary>Whether the symbols are a language server's rather than the outline's shapes.</summary>
    public bool FromServer { get; }

    /// <summary>The lines, read from the disk on demand and held weakly so cold files don't stay pinned in memory.</summary>
    public string[] Lines
    {
        get
        {
            if (inMemoryLines is not null)
                return inMemoryLines;

            if (Volatile.Read(ref cachedLines)?.TryGetTarget(out string[]? hit) == true)
                return hit;

            string[] loaded = readLines is not null ? readLines() : HexSearch.ReadLines(Path);
            Volatile.Write(ref cachedLines, new WeakReference<string[]>(loaded));
            return loaded;
        }
    }

    /// <summary>The lines if they're already in memory (in-memory file or still live in the weak cache), without reading the disk.</summary>
    internal bool TryGetCachedLines(out string[] lines)
    {
        if (inMemoryLines is not null)
        {
            lines = inMemoryLines;
            return true;
        }

        if (Volatile.Read(ref cachedLines)?.TryGetTarget(out string[]? hit) == true)
        {
            lines = hit;
            return true;
        }

        lines = [];
        return false;
    }

    /// <summary>Whether this file's lines come from memory or a custom reader rather than straight off the disk.</summary>
    internal bool HasInMemoryLines => inMemoryLines is not null || readLines is not null;

    /// <summary>A file read now, its symbols the outline's, keeping the given lines in memory.</summary>
    public HexIndexedFile(string path, string relative, long length, long written, string[] read)
        : this(path, relative, length, written, read.Length, read.Length == 0 ? [] : OutlineOf(path, string.Join('\n', read)), false, read)
    {
    }

    /// <summary>A file read from the disk now, its symbols the outline's, no lines for one that can't be read or isn't text.</summary>
    public static HexIndexedFile Read(string path, string relative, long length, long written)
    {
        if (HexSearch.ReadText(path) is not { } text)
            return new HexIndexedFile(path, relative, length, written, []);

        int lineCount = text.AsSpan().Count('\n') + 1;
        return new HexIndexedFile(path, relative, length, written, lineCount, OutlineOf(path, text), false);
    }

    /// <summary>A file as it was kept, by the cache or by a language server's answer, its text read when it's needed.</summary>
    public HexIndexedFile(string path, string relative, long length, long written, int lineCount, IReadOnlyList<HexSymbol> symbols, bool fromServer, Func<string[]>? read = null)
        : this(path, relative, length, written, lineCount, Compact(symbols), fromServer, inMemoryLines: null, readLines: read, cachedLines: null)
    {
    }

    private HexIndexedFile(string path, string relative, long length, long written, int lineCount, HexSymbol[] symbols, bool fromServer, string[]? inMemoryLines, Func<string[]>? readLines = null, WeakReference<string[]>? cachedLines = null)
    {
        Path = path;
        Relative = relative;
        Extension = InternExtension(path);
        Length = length;
        Written = written;
        LineCount = lineCount;
        Symbols = symbols;
        FromServer = fromServer;
        this.inMemoryLines = inMemoryLines;
        this.readLines = readLines;
        this.cachedLines = cachedLines;
    }

    /// <summary>The same file with a language server's symbols in place of the outline's.</summary>
    public HexIndexedFile WithServerSymbols(IReadOnlyList<HexSymbol> symbols) =>
        new(Path, Relative, Length, Written, LineCount, Compact(symbols), true, inMemoryLines, readLines, Volatile.Read(ref cachedLines));

    internal static string InternExtension(string path)
    {
        ReadOnlySpan<char> ext = System.IO.Path.GetExtension(path.AsSpan());
        if (ext.IsEmpty)
            return string.Empty;
        if (extensionsLookup.TryGetValue(ext, out string? hit))
            return hit;

        Span<char> lower = ext.Length <= 32 ? stackalloc char[ext.Length] : new char[ext.Length];
        ext.ToLowerInvariant(lower);
        if (extensionsLookup.TryGetValue(lower, out hit))
            return hit;

        string made = lower.ToString();
        return extensions.GetOrAdd(made, made);
    }

    private static HexSymbol[] Compact(IReadOnlyList<HexSymbol> symbols) =>
        symbols.Count == 0 ? [] : symbols is HexSymbol[] array ? array : [.. symbols];

    // What a file declares by the outline Hex draws for it, nothing for one the outline chokes on
    private static HexSymbol[] OutlineOf(string path, string text)
    {
        try
        {
            return HexSymbol.Of(HexOutline.Fresh(text, path));
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>The language as an agent would say it, by the extension.</summary>
    public string Language => LanguageOf(Extension);

    public static string LanguageOf(string extension) => extension switch
    {
        ".cs" => "C#",
        ".hor" => "HIDL",
        ".d" or ".di" => "D",
        ".slang" or ".hlsl" => "shader",
        ".py" => "Python",
        ".js" or ".mjs" or ".jsx" => "JavaScript",
        ".ts" or ".tsx" => "TypeScript",
        ".md" or ".markdown" => "Markdown",
        ".json" => "JSON",
        ".yaml" or ".yml" => "YAML",
        "" => "text",
        _ => extension[1..]
    };
}
