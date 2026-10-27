using Horizon.UI.Scripting;

namespace Hex.Core;

/// <summary>
/// The bits of the reference counts over declarations (Visual Studio's code lens) that don't need an editor. Which line
/// a count goes over, which kinds of thing get one, and which of the places a server says are really uses.
/// </summary>
internal static class HexCodeLens
{
    /// <summary>The most a count says as a number, past it it's 99+.</summary>
    public const int MOST_SHOWN = 99;

    /// <summary>
    /// The line a count goes over, from the line the name is on and the line the server says the whole of it starts on.
    /// csharp-ls starts a declaration at the blank line and the doc comment over it, so those are walked down past, and
    /// a name said on its own line (a code lens) has its attributes walked up over, Visual Studio puts its count over
    /// those too.
    /// </summary>
    public static int LensLine(Func<int, string> lineAt, int lineCount, int nameLine, int startLine)
    {
        if (lineCount <= 0)
            return 0;

        nameLine = Math.Clamp(nameLine, 0, lineCount - 1);
        int line = Math.Clamp(Math.Min(startLine, nameLine), 0, nameLine);
        while (line < nameLine && IsBlankOrComment(lineAt(line)))
            line++;
        while (line > 0 && IsAttribute(lineAt(line - 1)))
            line--;
        return line;
    }

    /// <summary>Whether a symbol of a kind (as LanguageServer.SymbolKindOf names it) gets a count over it, types and their members do.</summary>
    public static bool Counts(string kind) => kind is "class" or "struct" or "interface" or "enum" or "enum-member" or "method" or "property"
        or "field" or "constant" or "constructor" or "event" or "function" or "operator";

    /// <summary>
    /// Whether a place a server says uses a symbol really does. Not the declaration itself, and for a property not the
    /// rest of its own line, csharp-ls says its get and set use it.
    /// </summary>
    public static bool IsUse(LanguageServerPlace place, string path, string kind, int line, int character)
    {
        if (!PlatformPaths.Same(place.Path, path))
            return true;
        if (place.Line == line && place.Character == character)
            return false;
        return !(kind is "property" or "event" && place.Line == line);
    }

    // Whether a line has nothing on it but a comment, or nothing at all
    private static bool IsBlankOrComment(string text)
    {
        ReadOnlySpan<char> trimmed = text.AsSpan().Trim();
        return trimmed.IsEmpty || trimmed.StartsWith("//") || trimmed.StartsWith("/*") || trimmed.StartsWith("*") || trimmed.StartsWith("#region") || trimmed.StartsWith("#endregion");
    }

    // Whether a line is an attribute ([Fact], [Obsolete("old")]) or a decorator (@Override), what goes over a declaration
    private static bool IsAttribute(string text)
    {
        ReadOnlySpan<char> trimmed = text.AsSpan().Trim();
        return (trimmed.Length > 2 && trimmed[0] == '[' && trimmed[^1] == ']') || (trimmed.Length > 1 && trimmed[0] == '@' && char.IsLetter(trimmed[1]));
    }
}
