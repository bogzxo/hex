using Horizon.UI.Scripting;

namespace Hex.Core;

// A language server's symbols for a file, better than the shapes the outline goes by. The editor asks the server as a
// file's opened and saved and hands the answer over here with the file's length and time when it asked. They're kept
// for as long as the file stays that length and time, and go out to the cache on disk with the rest.
internal sealed partial class HexCodeIndex
{
    private readonly Dictionary<string, (long Length, long Written, IReadOnlyList<HexSymbol> Symbols)> serverSymbols = new(StringComparer.OrdinalIgnoreCase);

    // Whether some server symbols came in that the next snapshot has to have, under the lock
    private bool symbolsMoved;

    /// <summary>
    /// What a language server said a file declares, at the length and time the file was when it was asked. Taken into
    /// the index at once when the index has the file at that length and time, kept for the look that reads it otherwise.
    /// </summary>
    public void TakeServerSymbols(string path, long length, long written, IReadOnlyList<HexSymbol> symbols)
    {
        if (symbols.Count == 0)
            return;

        long changedTo = -1;
        lock (looking)
        {
            path = Path.GetFullPath(path);
            serverSymbols[path] = (length, written, symbols);

            HexIndexSnapshot was = snapshot;
            if (was.File(path) is { } file && file.Length == length && file.Written == written)
            {
                HexIndexedFile[] files = [.. was.Files.Select(each => ReferenceEquals(each, file) ? file.WithServerSymbols(symbols) : each)];
                snapshot = new HexIndexSnapshot(was.Generation + 1, was.Roots, files);
                changedTo = snapshot.Generation;
                cacheDirty = true;
            }
            else
            {
                symbolsMoved = true;
            }
        }

        if (changedTo >= 0)
            Changed?.Invoke(changedTo);
    }

    /// <summary>
    /// Symbols out of a language server's documentSymbol answer, each with what it's in, the tree flattened. The kinds
    /// are the server's own words in lower case (Method, Class), the ones the outline says too where they mean the same.
    /// </summary>
    public static List<HexSymbol> FromServer(IEnumerable<LanguageServerSymbol> roots)
    {
        var symbols = new List<HexSymbol>();
        void Walk(LanguageServerSymbol symbol, string container, int depth)
        {
            // csharp-ls puts everything inside the file and its namespace, which the outline never says and which would
            // bury a class's members two levels down, so those are walked through without being symbols themselves
            if (symbol.Kind.ToLowerInvariant() is "file" or "namespace" or "module" or "package")
            {
                foreach (LanguageServerSymbol child in symbol.Children)
                    Walk(child, container, depth);
                return;
            }

            // csharp-ls names a method with its parameters, Check(JsonObject, JsonObject), which nobody searches by
            int bracket = symbol.Name.IndexOf('(');
            string name = bracket > 0 ? symbol.Name[..bracket].Trim() : symbol.Name;
            var made = new HexSymbol(name, KindOf(symbol.Kind), container, symbol.Line, symbol.Character, depth);
            symbols.Add(made);
            foreach (LanguageServerSymbol child in symbol.Children)
                Walk(child, made.Full, depth + 1);
        }

        foreach (LanguageServerSymbol root in roots)
            Walk(root, string.Empty, 0);
        symbols.Sort((a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column));
        return symbols;
    }

    // A server's kind as the outline names the same thing
    private static string KindOf(string kind) => kind.ToLowerInvariant() switch
    {
        "constructor" or "function" or "operator" => "method",
        "enummember" or "constant" => "field",
        "typeparameter" => "variable",
        "module" or "package" => "namespace",
        var same => same
    };

    // A file with the server's symbols when they're for the length and time it is. Under the lock
    private HexIndexedFile ServerSymbolsFor(HexIndexedFile file)
    {
        if (file.FromServer || !serverSymbols.TryGetValue(file.Path, out var said))
            return file;
        return said.Length == file.Length && said.Written == file.Written ? file.WithServerSymbols(said.Symbols) : file;
    }
}
