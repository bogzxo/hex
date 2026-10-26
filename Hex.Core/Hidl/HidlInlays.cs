using Horizon.HIDL.Lexing;
using Horizon.UI.Components;

namespace Hex.Core;

/// <summary>
/// HIDL's inlay hints, the name of the parameter each argument of a call goes to, for the functions a file declares
/// itself (func name(a, b), let name = func(a, b), let name = (a, b) =&gt; or let name = a =&gt;). A parameter with a default
/// (b = 2) still says its name. An argument that's already a name the same as its parameter goes without, it'd only say
/// the same thing twice. Worked out from the tokens, so a file halfway through being typed still gets what can be made
/// out of it.
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

        var functions = HidlFunctions.Declared([.. tokens]).ToDictionary(pair => pair.Key, pair => pair.Value.Select(parameter => parameter.Value).ToArray(), StringComparer.Ordinal);
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
