namespace Hex.Core;

// The tokens of a D file as far as telling its names apart goes. Comments of all three kinds (/+ +/ nesting), texts of
// every kind and character literals are skipped over whole, numbers are one token, a name is a name or a keyword, and
// everything else is one character of punctuation, which is all the reading after this needs.
public static partial class DSemantics
{
    private enum DToken : byte
    {
        Name,
        Keyword,
        Punctuation,
        Literal
    }

    // One token, where it is (line and column from 0) and what it says, the character for punctuation
    private readonly record struct Piece(DToken Kind, int Line, int Column, string Text)
    {
        public bool Is(char punctuation) => Kind == DToken.Punctuation && Text[0] == punctuation;
        public bool IsWord(string word) => Kind == DToken.Keyword && Text == word;
    }

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "alias", "align", "asm", "assert", "auto", "body", "bool", "break", "byte", "case", "cast", "catch", "cdouble",
        "cent", "cfloat", "char", "class", "const", "continue", "creal", "dchar", "debug", "default", "delegate", "delete",
        "deprecated", "do", "double", "else", "enum", "export", "extern", "false", "final", "finally", "float", "for", "foreach",
        "foreach_reverse", "function", "goto", "idouble", "if", "ifloat", "immutable", "import", "in", "inout", "int", "interface",
        "invariant", "ireal", "is", "lazy", "long", "macro", "mixin", "module", "new", "nothrow", "null", "out", "override",
        "package", "pragma", "private", "protected", "public", "pure", "real", "ref", "return", "scope", "shared", "short",
        "static", "struct", "super", "switch", "synchronized", "template", "this", "throw", "true", "try", "typeid", "typeof",
        "ubyte", "ucent", "uint", "ulong", "union", "unittest", "ushort", "version", "void", "wchar", "while", "with",
        "__traits", "__gshared", "__FILE__", "__LINE__", "__MODULE__", "__FUNCTION__"
    };

    // The words that are a type on their own, the basic types and the aliases object declares, a declaration can start with one
    private static readonly HashSet<string> TypeWords = new(StringComparer.Ordinal)
    {
        "bool", "byte", "ubyte", "short", "ushort", "int", "uint", "long", "ulong", "cent", "ucent", "char", "wchar", "dchar",
        "float", "double", "real", "ifloat", "idouble", "ireal", "cfloat", "cdouble", "creal", "void", "auto",
        "string", "wstring", "dstring", "size_t", "ptrdiff_t"
    };

    private static List<Piece> Lex(string text)
    {
        var pieces = new List<Piece>(text.Length / 4);
        int line = 0, lineStart = 0, i = 0;

        // Moves along to a place, counting the lines passed
        void To(int end)
        {
            for (; i < end && i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    lineStart = i + 1;
                }
            }
        }

        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\n' || char.IsWhiteSpace(c))
            {
                To(i + 1);
                continue;
            }

            int start = i, column = i - lineStart, startLine = line;
            char next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                int end = text.IndexOf('\n', i);
                To(end < 0 ? text.Length : end);
                continue;
            }

            if (c == '/' && next == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                To(end < 0 ? text.Length : end + 2);
                continue;
            }

            if (c == '/' && next == '+')
            {
                int depth = 0, at = i;
                while (at < text.Length)
                {
                    if (at + 1 < text.Length && text[at] == '/' && text[at + 1] == '+') { depth++; at += 2; }
                    else if (at + 1 < text.Length && text[at] == '+' && text[at + 1] == '/') { at += 2; if (--depth == 0) break; }
                    else at++;
                }
                To(at);
                continue;
            }

            // r"..." and `...` take no escapes, "..." does, 'x' is a character
            if (c == '`' || (c == 'r' && next == '"'))
            {
                int open = c == '`' ? i : i + 1, end = text.IndexOf(text[open], open + 1);
                To(end < 0 ? text.Length : end + 1);
                pieces.Add(new Piece(DToken.Literal, startLine, column, string.Empty));
                continue;
            }

            if (c == '"' || c == '\'')
            {
                int at = i + 1;
                while (at < text.Length && text[at] != c)
                    at += text[at] == '\\' ? 2 : 1;
                To(Math.Min(text.Length, at + 1));
                pieces.Add(new Piece(DToken.Literal, startLine, column, string.Empty));
                continue;
            }

            if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(next)))
            {
                int at = i + 1;
                while (at < text.Length && (char.IsAsciiLetterOrDigit(text[at]) || text[at] == '_' || (text[at] == '.' && at + 1 < text.Length && char.IsAsciiDigit(text[at + 1]))))
                    at++;
                To(at);
                pieces.Add(new Piece(DToken.Literal, startLine, column, string.Empty));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int at = i + 1;
                while (at < text.Length && (char.IsLetterOrDigit(text[at]) || text[at] == '_'))
                    at++;
                string word = text[start..at];
                To(at);
                pieces.Add(new Piece(Keywords.Contains(word) ? DToken.Keyword : DToken.Name, startLine, column, word));
                continue;
            }

            To(i + 1);
            pieces.Add(new Piece(DToken.Punctuation, startLine, column, c.ToString()));
        }

        return pieces;
    }
}
