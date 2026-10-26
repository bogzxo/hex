using Horizon.HIDL.Lexing;
using Horizon.HIDL.Parsing;

namespace Hex.Core;

/// <summary>
/// The functions a file of HIDL declares by name, read off its tokens for the inlay hints and signature help.
/// <c>func name(a, b)</c>, and a let or a const holding <c>func(a, b)</c>, <c>(a, b) =&gt;</c> or <c>x =&gt;</c>. A
/// parameter with a default (<c>size = vec(16)</c>) is the name as far as a hint goes, and the whole of it for a signature.
/// </summary>
internal static class HidlFunctions
{
    /// <summary>Every function the tokens declare by name and the tokens of its parameters' names, the last one of a name wins.</summary>
    public static Dictionary<string, List<Token>> Declared(Token[] tokens)
    {
        var functions = new Dictionary<string, List<Token>>(StringComparer.Ordinal);
        for (int i = 0; i + 2 < tokens.Length; i++)
        {
            if (Declares(tokens, i) is (string name, List<Token> parameters))
                functions[name] = parameters;
        }

        return functions;
    }

    /// <summary>
    /// The parameters of the function some code declares by a name as they're written, defaults and all, null when it
    /// doesn't declare one of that name.
    /// </summary>
    public static string[]? Signature(string code, string name)
    {
        Token[] tokens = Lexer.Tokenize(code, lenient: true);
        for (int i = 0; i + 2 < tokens.Length; i++)
        {
            if (Declares(tokens, i) is not (string declared, List<Token> parameters) || declared != name)
                continue;

            return [.. parameters.Select(parameter => Written(code, tokens, parameter))];
        }

        return null;
    }

    // The function declared at a token, func name(...), or let name = and a func(...), an (a, b) => or an x =>
    private static (string Name, List<Token> Parameters)? Declares(Token[] tokens, int i)
    {
        string name;
        int value;
        if (tokens[i].Type == TokenType.Function && tokens[i + 1].Type == TokenType.Identifier)
            (name, value) = (tokens[i + 1].Value, i + 2);
        else if (tokens[i].Type is TokenType.Let or TokenType.Const && i + 3 < tokens.Length && tokens[i + 1].Type == TokenType.Identifier
            && tokens[i + 2].Type == TokenType.Equals)
            (name, value) = (tokens[i + 1].Value, i + 3);
        else
            return null;

        // func( and func name( after the =, the bracket is after them
        if (tokens[value].Type == TokenType.Function)
            value += value + 1 < tokens.Length && tokens[value + 1].Type == TokenType.Identifier ? 2 : 1;

        if (HIDLDeclarations.IsArrowParameter(tokens, value))
            return (name, [tokens[value]]);

        return HIDLDeclarations.OpensParameters(tokens, value) ? (name, HIDLDeclarations.Parameters(tokens, value)) : null;
    }

    // A parameter as it's written, from its name to the comma or the bracket that ends it
    private static string Written(string code, Token[] tokens, Token parameter)
    {
        int at = Array.FindIndex(tokens, token => token.Index == parameter.Index);
        int depth = 0;
        for (int i = at + 1; i < tokens.Length; i++)
        {
            switch (tokens[i].Type)
            {
                case TokenType.OpenParenthesis or TokenType.OpenBracket or TokenType.OpenBrace:
                    depth++;
                    break;
                case TokenType.CloseParenthesis when depth == 0:
                case TokenType.Comma when depth == 0:
                case TokenType.Arrow or TokenType.EndOfFile:
                    return code[parameter.Index..tokens[i].Index].Trim();
                case TokenType.CloseParenthesis or TokenType.CloseBracket or TokenType.CloseBrace:
                    depth--;
                    break;
            }
        }

        return parameter.Value;
    }
}
