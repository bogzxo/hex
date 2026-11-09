using Horizon.HIDL.Lexing;
using Horizon.HIDL.Parsing;

namespace Hex.Core;

/// <summary>
/// One statement at the top of a layout's code, from its first token to its semicolon. A declaration of a component
/// (<c>let less = compositor.button({ ... })</c>) knows where its kind and its braces are and the keys written in them,
/// a statement about one (<c>more.on_pressed = func() { ... }</c>) whose it is, a call of the compositor which call.
/// </summary>
/// <param name="Declares">The name a let or const declares, null for anything else.</param>
/// <param name="Kind">Where the kind of a component is written, unknown when it isn't compositor.something().</param>
/// <param name="Arguments">From the ( of the compositor's call to its ), unknown when it isn't one.</param>
/// <param name="Braces">From the { of the properties to their }, unknown when they aren't written as an object right there.</param>
/// <param name="Subject">The name a statement starts with when it's that name dot something, null otherwise.</param>
/// <param name="Call">What of the compositor's a statement calls (design, hidden_layers), null for anything else.</param>
internal sealed record HexStatement(int Start, int End, string? Declares, SourceSpan Kind, SourceSpan Arguments, SourceSpan Braces,
    IReadOnlyList<HexEntry> Entries, string? Subject, string? Call);

/// <summary>
/// Where things are in the code of a layout, read out of its tokens and nothing run. What <see cref="HexLayoutEdits"/>
/// finds the places to change in, the statements at the top of it and the uses of a name.
/// </summary>
internal static class HexLayoutText
{
    /// <summary>Every statement at the top of the code, in the order they're written.</summary>
    public static List<HexStatement> Statements(string text)
    {
        Token[] tokens = Lexer.Tokenize(text, lenient: true);
        var statements = new List<HexStatement>();
        var code = new List<Token>();

        int i = 0;
        while (i < tokens.Length && tokens[i].Type != TokenType.EndOfFile)
        {
            if (tokens[i].Type is TokenType.Comment or TokenType.Directive)
            {
                i++;
                continue;
            }

            // To the semicolon at the top, or to the next let when somebody left one out
            code.Clear();
            int depth = 0, j = i;
            for (; j < tokens.Length && tokens[j].Type != TokenType.EndOfFile; j++)
            {
                Token at = tokens[j];
                if (at.Type is TokenType.Comment or TokenType.Directive)
                    continue;
                if (depth <= 0 && code.Count > 0 && at.Type is TokenType.Let or TokenType.Const)
                    break;

                code.Add(at);
                if (at.Type is TokenType.OpenParenthesis or TokenType.OpenBracket or TokenType.OpenBrace)
                    depth++;
                else if (at.Type is TokenType.CloseParenthesis or TokenType.CloseBracket or TokenType.CloseBrace)
                    depth--;
                else if (at.Type == TokenType.Semicolon && depth <= 0)
                {
                    j++;
                    break;
                }
            }

            if (code.Count > 0)
                statements.Add(Read(text, code, tokens, i, j));
            i = Math.Max(j, i + 1);
        }

        return statements;
    }

    // What one statement is, out of its tokens without the comments
    private static HexStatement Read(string text, List<Token> code, Token[] all, int from, int to)
    {
        int start = code[0].Index, end = code[^1].Index + code[^1].Length;
        string? declares = null, subject = null, call = null;
        SourceSpan kind = default, arguments = default, braces = default;
        List<HexEntry> entries = [];

        bool let = code[0].Type is TokenType.Let or TokenType.Const;
        if (let && code.Count > 1 && code[1].Type == TokenType.Identifier)
            declares = code[1].Value;

        // compositor.kind( right after the =, or a statement that is a call of the compositor's
        int callAt = let ? 3 : 0;
        if (code.Count > callAt + 3 && (!let || code[2].Type == TokenType.Equals)
            && code[callAt].Value == "compositor" && code[callAt + 1].Type == TokenType.Dot && code[callAt + 3].Type == TokenType.OpenParenthesis)
        {
            Token named = code[callAt + 2];
            if (let)
                kind = Span(named.Index, named.Index + named.Length);
            else
                call = named.Value;

            int close = Matching(code, callAt + 3);
            if (close > 0)
            {
                arguments = Span(code[callAt + 3].Index, code[close].Index + 1);
                if (code[callAt + 4].Type == TokenType.OpenBracket)
                {
                    int closeBrace = Matching(code, callAt + 4);
                    if (closeBrace > 0)
                    {
                        braces = Span(code[callAt + 4].Index, code[closeBrace].Index + 1);
                        entries = Entries(all, from, to, code[callAt + 4].Index, code[closeBrace].Index);
                    }
                }
            }
        }

        if (!let && code.Count > 1 && code[0].Type == TokenType.Identifier && code[1].Type == TokenType.Dot && code[0].Value != "compositor")
            subject = code[0].Value;

        return new HexStatement(start, end, declares, kind, arguments, braces, entries, subject, call);
    }

    // The keys written between a { and its }, comments and all skipped, each with where its value is
    private static List<HexEntry> Entries(Token[] all, int from, int to, int open, int close)
    {
        var entries = new List<HexEntry>();
        int i = from;
        while (i < to && all[i].Index != open)
            i++;
        i++;

        int depth = 0;
        Token? key = null;
        int valueStart = -1, valueEnd = -1;

        void Finish()
        {
            if (key is { } named && valueStart >= 0 && valueEnd > valueStart)
                entries.Add(new HexEntry(named.Value, Span(named.Index, named.Index + named.Length), Span(valueStart, valueEnd)));
            key = null;
            valueStart = valueEnd = -1;
        }

        for (; i < to && all[i].Index < close; i++)
        {
            Token at = all[i];
            if (at.Type == TokenType.Comment)
                continue;

            if (depth == 0 && at.Type == TokenType.Comma)
            {
                Finish();
                continue;
            }

            // A key is a word or a text with a colon after it, at the top of the braces
            if (depth == 0 && key is null && valueStart < 0 && NextCode(all, i + 1, to) is var colon and >= 0 && all[colon].Type == TokenType.Colon)
            {
                key = at;
                i = colon;
                continue;
            }

            if (key is not null && valueStart < 0)
                valueStart = at.Index;
            valueEnd = at.Index + at.Length;

            if (at.Type is TokenType.OpenParenthesis or TokenType.OpenBracket or TokenType.OpenBrace)
                depth++;
            else if (at.Type is TokenType.CloseParenthesis or TokenType.CloseBracket or TokenType.CloseBrace)
                depth--;
        }

        Finish();
        return entries;
    }

    // Where the next token that isn't a comment is, -1 for none before the end
    private static int NextCode(Token[] all, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            if (all[i].Type != TokenType.Comment)
                return i;
        }

        return -1;
    }

    // The bracket that closes the one at an index, -1 if none does
    private static int Matching(List<Token> code, int open)
    {
        int depth = 0;
        for (int i = open; i < code.Count; i++)
        {
            if (code[i].Type is TokenType.OpenParenthesis or TokenType.OpenBracket or TokenType.OpenBrace)
                depth++;
            else if (code[i].Type is TokenType.CloseParenthesis or TokenType.CloseBracket or TokenType.CloseBrace && --depth == 0)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Where a name is used as a variable, the declaration of it too. Not a key of an object (<c>{ panel: 1 }</c>) and not
    /// a member of something (<c>this.panel</c>), which only look the same.
    /// </summary>
    public static List<SourceSpan> Uses(string text, string name)
    {
        Token[] tokens = Lexer.Tokenize(text, lenient: true);
        var uses = new List<SourceSpan>();
        Token previous = default;
        for (int i = 0; i < tokens.Length; i++)
        {
            Token at = tokens[i];
            if (at.Type == TokenType.Comment)
                continue;

            if (at.Type == TokenType.Identifier && at.Value == name && previous.Type is not (TokenType.Dot or TokenType.QuestionDot)
                && !(NextCode(tokens, i + 1, tokens.Length) is var next and >= 0 && tokens[next].Type == TokenType.Colon && previous.Type is TokenType.OpenBracket or TokenType.Comma))
                uses.Add(Span(at.Index, at.Index + at.Length));

            previous = at;
        }

        return uses;
    }

    /// <summary>A place in a text by where it starts and ends, the line and column left out (nothing here needs them).</summary>
    public static SourceSpan Span(int from, int to) => new(1, 1, from, to - from);
}
