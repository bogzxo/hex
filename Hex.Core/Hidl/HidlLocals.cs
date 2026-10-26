using Horizon.HIDL.Lexing;
using Horizon.HIDL.Parsing;

namespace Hex.Core;

/// <summary>
/// Which declaration a name means where it's used, the nearest one whose scope it's in. A parameter is in scope over
/// its function's body (an arrow's expression too), a name of a for over the loop's body, and a let or a const from
/// where it's made to the end of the block it's made in. Worked out from the tokens, so a half typed file still gets
/// an answer, and it's only a guess where the brackets don't add up.
/// </summary>
internal static class HidlLocals
{
    /// <summary>The declaration of a name nearest to where it's used (one based line and column), null for none in scope.</summary>
    public static Token? Nearest(Token[] tokens, string name, int line, int column)
    {
        int use = TokenAt(tokens, line, column);
        if (use < 0)
            return null;

        Token? best = null;
        int bestAt = -1;
        for (int i = 0; i < use; i++)
        {
            foreach (var (declared, from, to) in DeclaredAt(tokens, i, name))
            {
                if (declared.Value != name || declared.Index > tokens[use].Index || use < from || use > to)
                    continue;

                // The one made last before the use is the one nearest round it
                if (declared.Index > bestAt)
                    (best, bestAt) = (declared, declared.Index);
            }
        }

        return best;
    }

    // What the token at a place declares of a name and the token span it's in scope over. The scope is only worked
    // out for a declaration of the name, the rest are skipped cheaply
    private static IEnumerable<(Token Name, int From, int To)> DeclaredAt(Token[] tokens, int i, string name)
    {
        switch (tokens[i].Type)
        {
            case TokenType.OpenParenthesis when HIDLDeclarations.OpensParameters(tokens, i):
            {
                if (HIDLDeclarations.Parameters(tokens, i).Find(each => each.Value == name) is not { Value: not null } parameter)
                    break;

                int body = Close(tokens, i) + 1;
                if (body < tokens.Length && tokens[body].Type == TokenType.Arrow)
                    body++;
                yield return (parameter, i, BodyEnd(tokens, body));
                break;
            }

            // x => without brackets
            case TokenType.Identifier when tokens[i].Value == name && HIDLDeclarations.IsArrowParameter(tokens, i):
                yield return (tokens[i], i, BodyEnd(tokens, i + 2));
                break;

            case TokenType.For:
            {
                if (HIDLDeclarations.LoopNames(tokens, i).Find(each => each.Value == name) is not { Value: not null } loop)
                    break;

                yield return (loop, i, BodyEnd(tokens, Close(tokens, i + 1) + 1));
                break;
            }

            case TokenType.Let or TokenType.Const:
            {
                if (HIDLDeclarations.Declared(tokens, i).Find(each => each.Value == name) is not { Value: not null } made)
                    break;

                yield return (made, i, BlockEnd(tokens, i));
                break;
            }
        }
    }

    // The token a one based line and column is on, an identifier there, -1 for none
    private static int TokenAt(Token[] tokens, int line, int column)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            Token token = tokens[i];
            if (token.Line == line && token.Type == TokenType.Identifier && column >= token.Col && column <= token.Col + token.Length)
                return i;
            if (token.Line > line)
                break;
        }

        return -1;
    }

    // The bracket that closes the one at a place, the last token when nothing does
    private static int Close(Token[] tokens, int open)
    {
        int depth = 0;
        for (int i = open; i < tokens.Length; i++)
        {
            if (tokens[i].Type is TokenType.OpenParenthesis or TokenType.OpenBracket or TokenType.OpenBrace)
                depth++;
            else if (tokens[i].Type is TokenType.CloseParenthesis or TokenType.CloseBracket or TokenType.CloseBrace && --depth == 0)
                return i;
        }

        return tokens.Length - 1;
    }

    // Where a body starting at a place ends, a block at its closing brace, one statement or an arrow's expression at
    // the semicolon or comma after it or the bracket round it
    private static int BodyEnd(Token[] tokens, int at)
    {
        while (at < tokens.Length && tokens[at].Type == TokenType.Comment)
            at++;
        if (at >= tokens.Length)
            return tokens.Length - 1;
        if (tokens[at].Type == TokenType.OpenBracket)
            return Close(tokens, at);

        int depth = 0;
        for (int i = at; i < tokens.Length; i++)
        {
            switch (tokens[i].Type)
            {
                case TokenType.OpenParenthesis or TokenType.OpenBracket or TokenType.OpenBrace:
                    depth++;
                    break;
                case TokenType.CloseParenthesis or TokenType.CloseBracket or TokenType.CloseBrace when --depth < 0:
                    return i;
                case TokenType.Semicolon or TokenType.Comma when depth == 0:
                    return i;
            }
        }

        return tokens.Length - 1;
    }

    // The brace that closes the block a place is in, the end of the file at the top
    private static int BlockEnd(Token[] tokens, int at)
    {
        int depth = 0;
        for (int i = at + 1; i < tokens.Length; i++)
        {
            if (tokens[i].Type is TokenType.OpenParenthesis or TokenType.OpenBracket or TokenType.OpenBrace)
                depth++;
            else if (tokens[i].Type is TokenType.CloseParenthesis or TokenType.CloseBracket or TokenType.CloseBrace && --depth < 0)
                return i;
        }

        return tokens.Length - 1;
    }
}
