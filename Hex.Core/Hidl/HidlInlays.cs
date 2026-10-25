using Horizon.HIDL.Lexing;
using Horizon.UI.Components;

namespace Hex.Core;

/// <summary>
/// HIDL's inlay hints, the name of the parameter each argument of a call goes to, for the functions a file declares
/// itself (func name(a, b), or let name = func(a, b)). An argument that's already a name the same as its parameter
/// goes without, it'd only say the same thing twice. Worked out from the tokens, so a file halfway through being typed
/// still gets what can be made out of it.
/// </summary>
public static class HidlInlays
{
    /// <summary>The hints of some HIDL, by the line and column each goes before.</summary>
    public static List<UICodeInlay> Of(string text)
    {
        Token[] all = Lexer.Tokenize(text, lenient: true);
        var tokens = new List<Token>(all.Length);
        foreach (Token token in all)
        {
            if (token.Type != TokenType.Comment)
                tokens.Add(token);
        }

        Dictionary<string, string[]> functions = Declared(tokens);
        var found = new List<UICodeInlay>();
        if (functions.Count == 0)
            return found;

        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            // A call is a name and a bracket, not the name of the function being declared nor a member of something
            if (tokens[i].Type != TokenType.Identifier || tokens[i + 1].Type != TokenType.OpenParenthesis
                || (i > 0 && tokens[i - 1].Type is TokenType.Function or TokenType.Dot)
                || !functions.TryGetValue(tokens[i].Value, out string[]? parameters))
                continue;

            CallArguments(tokens, i + 1, parameters, found);
        }

        return found;
    }

    // The functions the tokens declare and the names of their parameters
    private static Dictionary<string, string[]> Declared(List<Token> tokens)
    {
        var functions = new Dictionary<string, string[]>(StringComparer.Ordinal);
        for (int i = 0; i + 2 < tokens.Count; i++)
        {
            // func name(...) or let name = func(...)
            int open;
            string name;
            if (tokens[i].Type == TokenType.Function && tokens[i + 1].Type == TokenType.Identifier && tokens[i + 2].Type == TokenType.OpenParenthesis)
                (name, open) = (tokens[i + 1].Value, i + 2);
            else if (tokens[i].Type is TokenType.Let or TokenType.Const && i + 4 < tokens.Count && tokens[i + 1].Type == TokenType.Identifier
                && tokens[i + 2].Type == TokenType.Equals && tokens[i + 3].Type == TokenType.Function && tokens[i + 4].Type == TokenType.OpenParenthesis)
                (name, open) = (tokens[i + 1].Value, i + 4);
            else
                continue;

            var parameters = new List<string>();
            int at = open + 1;
            for (; at < tokens.Count && tokens[at].Type != TokenType.CloseParenthesis; at++)
            {
                if (tokens[at].Type == TokenType.Identifier)
                    parameters.Add(tokens[at].Value);
                else if (tokens[at].Type != TokenType.Comma)
                    break;
            }

            if (at < tokens.Count && tokens[at].Type == TokenType.CloseParenthesis && parameters.Count > 0)
                functions[name] = [.. parameters];
        }

        return functions;
    }

    // Each argument of a call from its open bracket, a hint before it with the parameter it goes to
    private static void CallArguments(List<Token> tokens, int open, string[] parameters, List<UICodeInlay> found)
    {
        int depth = 0, argument = 0;
        bool starting = true;
        for (int at = open + 1; at < tokens.Count && argument < parameters.Length; at++)
        {
            Token token = tokens[at];
            if (depth == 0 && token.Type == TokenType.CloseParenthesis)
                return;
            if (token.Type == TokenType.EndOfFile)
                return;

            if (starting)
            {
                starting = false;
                bool same = token.Type == TokenType.Identifier && token.Value == parameters[argument]
                    && at + 1 < tokens.Count && tokens[at + 1].Type is TokenType.Comma or TokenType.CloseParenthesis;
                if (!same)
                    found.Add(new UICodeInlay(token.Line - 1, token.Col - 1, parameters[argument] + ":", PadRight: true));
            }

            if (token.Type is TokenType.OpenParenthesis or TokenType.OpenBracket or TokenType.OpenBrace)
                depth++;
            else if (token.Type is TokenType.CloseParenthesis or TokenType.CloseBracket or TokenType.CloseBrace)
                depth--;
            else if (depth == 0 && token.Type == TokenType.Comma)
            {
                argument++;
                starting = true;
            }
        }
    }
}
