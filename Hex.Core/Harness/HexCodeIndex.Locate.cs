namespace Hex.Core;

/// <summary>
/// A declaration found by its name for a tool that goes on to ask a language server about it or to change it: its file,
/// the symbol, the line and column of the name itself (from 0), the lines it takes up with its comments (from 0), and
/// where else the name is declared when it's more than one place.
/// </summary>
internal sealed record HexLocated(HexIndexedFile File, HexSymbol Symbol, int Line, int Column, int First, int Last, string[] Others)
{
    /// <summary>The other places, said in a line for an answer, empty for none.</summary>
    public string AlsoSaid => Others.Length == 0 ? string.Empty
        : $"{Others.Length} more declare {Symbol.Name}, {string.Join(", ", Others.Take(5))}{(Others.Length > 5 ? " ..." : string.Empty)}, say path to pick one.";
}

// A name to a place, what the language-server tools (hex_calls, hex_implementations, hex_rename) start from so an agent
// can say "QueueMcp" rather than open a file and put a caret on it, and what hex_edit_symbol replaces. The file's text as
// it's on the disk now, not as it was indexed, so a file changed a moment ago still has the right lines.
internal sealed partial class HexCodeIndex
{
    /// <summary>
    /// The best declaration of a name (Name or Container.Name), in one file when a path's said. Null with why not in
    /// <paramref name="wrong"/>.
    /// </summary>
    public HexLocated? Locate(string symbol, string? path, out string? wrong)
    {
        HexIndexSnapshot index = Current();
        wrong = null;
        HexIndexedFile? file = path is { Length: > 0 } ? FileOf(index, path) : null;
        if (path is { Length: > 0 } && file is null)
        {
            wrong = NoFile(index, path);
            return null;
        }

        List<(HexIndexedFile File, HexSymbol Symbol, int Score)> fits = [.. FindSymbols(index, symbol)
            .Where(each => each.Score >= 90 && (file is null || ReferenceEquals(each.File, file)))];
        if (fits.Count == 0 && InContainer(index, symbol, file) is { } member)
            return member;
        if (fits.Count == 0)
        {
            wrong = $"Nothing called {symbol} is declared{(file is null ? string.Empty : $" in {file.Relative}")}. hex_symbols finds names by part of them.";
            return null;
        }

        // The best of the same kind and name before any other, the rest said as others
        var (found, best, _) = fits[0];
        string[] others = [.. fits.Skip(1).Select(each => $"{each.File.Relative}:{each.Symbol.Line + 1}")];
        return LocateIn(Fresh(found), best, others);
    }

    /// <summary>
    /// Container.Name for a member the outline doesn't list (an interface's method with no body, an abstract one), found
    /// by its name as a whole word on a line inside its container's declaration that isn't a comment. Null when it isn't.
    /// </summary>
    private static HexLocated? InContainer(HexIndexSnapshot index, string symbol, HexIndexedFile? only)
    {
        (string? container, string name) = SplitContainer(symbol.Trim());
        if (container is null || name.Length == 0)
            return null;

        foreach (var (file, type, score) in FindSymbols(index, container))
        {
            if (score < 90 || !type.IsType || (only is not null && !ReferenceEquals(file, only)))
                continue;
            HexIndexedFile fresh = Fresh(file);
            HexSymbol owner = fresh.Symbols.Where(each => each.Name == type.Name && each.Kind == type.Kind).MinBy(each => Math.Abs(each.Line - type.Line)) ?? type;
            var (_, last) = Extent(fresh, owner);
            string[] lines = fresh.Lines;
            for (int line = owner.Line + 1; line <= last && line < lines.Length; line++)
            {
                string text = lines[line].TrimStart();
                if (text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith('*') || text.StartsWith('#'))
                    continue;
                int column = NameColumn(lines[line], name, 0);
                if (column < lines[line].Length && lines[line].AsSpan(column).StartsWith(name, StringComparison.Ordinal)
                    && (column == 0 || !IsWordChar(lines[line][column - 1])) && (column + name.Length >= lines[line].Length || !IsWordChar(lines[line][column + name.Length])))
                {
                    var member = new HexSymbol(name, "member", owner.Full, line, column, owner.Depth + 1);
                    var (first, end) = Extent(fresh, member);
                    return new HexLocated(fresh, member, line, column, first, end, []);
                }
            }
        }

        return null;
    }

    /// <summary>A file read again from the disk when it's changed since it was indexed, so its lines and symbols are now's.</summary>
    internal static HexIndexedFile Fresh(HexIndexedFile file)
    {
        try
        {
            var info = new FileInfo(file.Path);
            if (!info.Exists || (info.Length == file.Length && info.LastWriteTimeUtc.Ticks == file.Written && file.Lines.Length == file.LineCount))
                return file;
            return new HexIndexedFile(file.Path, file.Relative, info.Length, info.LastWriteTimeUtc.Ticks, File.ReadAllLines(file.Path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return file;
        }
    }

    /// <summary>A symbol of a file, found again in it as it is (the nearest of the same name and kind), with its name's column and extent.</summary>
    internal static HexLocated LocateIn(HexIndexedFile file, HexSymbol was, string[] others)
    {
        HexSymbol symbol = file.Symbols.Where(each => each.Name == was.Name && each.Kind == was.Kind).MinBy(each => Math.Abs(each.Line - was.Line)) ?? was;
        var (first, last) = Extent(file, symbol);
        return new HexLocated(file, symbol, symbol.Line, NameColumn(LineOf(file, symbol.Line), symbol.Name, symbol.Column), first, last, others);
    }

    /// <summary>
    /// Where a name is on a line as a whole word, the place nearest a column it's said to be at, the first letter that
    /// isn't a space when it's not on it at all. A server asked at a declaration's line start (its modifiers) answers
    /// for nothing, it's asked on the name.
    /// </summary>
    internal static int NameColumn(string line, string name, int near = 0)
    {
        int best = -1;
        if (name.Length > 0)
        {
            for (int at = line.IndexOf(name, StringComparison.Ordinal); at >= 0; at = line.IndexOf(name, at + 1, StringComparison.Ordinal))
            {
                bool starts = at == 0 || !IsWordChar(line[at - 1]);
                bool ends = at + name.Length >= line.Length || !IsWordChar(line[at + name.Length]);
                if (starts && ends && (best < 0 || Math.Abs(at - near) < Math.Abs(best - near)))
                    best = at;
            }
        }

        if (best >= 0)
            return best;
        int text = 0;
        while (text < line.Length && char.IsWhiteSpace(line[text]))
            text++;
        return text;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
