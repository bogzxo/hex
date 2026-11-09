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
    public static List<HexSymbol> Of(IEnumerable<OutlineEntry> entries)
    {
        var symbols = new List<HexSymbol>();
        var open = new List<(int Depth, string Full)>();
        foreach (OutlineEntry entry in entries)
        {
            // An outline line with no name (an operator, a shape the regular expressions half matched) is nothing to find
            if (string.IsNullOrWhiteSpace(entry.Name))
                continue;

            while (open.Count > 0 && open[^1].Depth >= entry.Depth)
                open.RemoveAt(open.Count - 1);

            var symbol = new HexSymbol(entry.Name, entry.Icon.Replace("symbol-", string.Empty), open.Count > 0 ? open[^1].Full : string.Empty, entry.Line, entry.Column, entry.Depth);
            symbols.Add(symbol);
            open.Add((entry.Depth, symbol.Full));
        }

        return symbols;
    }
}

/// <summary>
/// A file of the workspace as the harness has it, kept until its length or the time it was written changes. What it
/// declares is known from the start, its text is read the first time something needs it (a file that came out of the
/// cache on disk hasn't been read at all). Nothing of it changes once it's made, so any thread can read it.
/// </summary>
internal sealed class HexIndexedFile
{
    private readonly Lazy<string[]> lines;

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

    /// <summary>The lines, read from the disk the first time they're asked for.</summary>
    public string[] Lines => lines.Value;

    /// <summary>A file read now, its symbols the outline's.</summary>
    public HexIndexedFile(string path, string relative, long length, long written, string[] read)
        : this(path, relative, length, written, read.Length, OutlineOf(path, read), false, () => read)
    {
    }

    /// <summary>A file as it was kept, by the cache or by a language server's answer, its text read when it's needed.</summary>
    public HexIndexedFile(string path, string relative, long length, long written, int lineCount, IReadOnlyList<HexSymbol> symbols, bool fromServer, Func<string[]>? read = null)
    {
        Path = path;
        Relative = relative;
        Extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        Length = length;
        Written = written;
        LineCount = lineCount;
        Symbols = symbols;
        FromServer = fromServer;
        lines = new Lazy<string[]>(read ?? (() => HexSearch.ReadLines(path)), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The same file with a language server's symbols in place of the outline's.</summary>
    public HexIndexedFile WithServerSymbols(IReadOnlyList<HexSymbol> symbols) =>
        new(Path, Relative, Length, Written, LineCount, symbols, true, () => Lines);

    // What a file declares by the outline Hex draws for it, nothing for one the outline chokes on
    private static List<HexSymbol> OutlineOf(string path, string[] read)
    {
        if (read.Length == 0)
            return [];

        try
        {
            return HexSymbol.Of(HexOutline.Fresh(string.Join('\n', read), path));
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
