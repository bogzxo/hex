using System.Numerics;
using System.Text;

using Horizon.Content;
using Horizon.HIDL;
using Horizon.HIDL.Lexing;
using Horizon.HIDL.Parsing;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Hex;

// The outline of a file of code, what it declares and the keys of its objects, by its tokens.
internal sealed partial class HexScene
{
    /* The outline of a file of code, what it declares and the keys of its objects, on the left */

    private readonly record struct OutlineEntry(string Name, int Line, int Column, int Depth, string Icon = "symbol-variable");

    /// <summary>
    /// What a file declares (its lets and consts) and the keys of the objects in those, a few levels down, by its tokens
    /// so a file that doesn't parse right now still has one.
    /// </summary>
    private static List<OutlineEntry> Outline(string text)
    {
        // The breadcrumbs ask on every move of the caret, the values on every click, the outline on every change. An
        // editor hands out the same string until its text changes, so the last one is kept, nobody changes the list
        if (ReferenceEquals(text, outlinedText) && outlined is not null)
            return outlined;

        outlinedText = text;
        return outlined = OutlineOf(text);
    }

    private static string? outlinedText;
    private static List<OutlineEntry>? outlined;

    private static List<OutlineEntry> OutlineOf(string text)
    {
        var entries = new List<OutlineEntry>();
        Token[] tokens = Lexer.Tokenize(text, lenient: true);
        int depth = 0;

        for (int i = 0; i < tokens.Length && entries.Count < 3000; i++)
        {
            Token token = tokens[i];
            switch (token.Type)
            {
                case TokenType.OpenBrace or TokenType.OpenBracket or TokenType.OpenParenthesis:
                    depth++;
                    break;
                case TokenType.CloseBrace or TokenType.CloseBracket or TokenType.CloseParenthesis:
                    depth = Math.Max(0, depth - 1);
                    break;

                case TokenType.Let or TokenType.Const when i + 1 < tokens.Length && tokens[i + 1].Type == TokenType.Identifier:
                    entries.Add(new OutlineEntry(tokens[i + 1].Value, tokens[i + 1].Line - 1, tokens[i + 1].Col - 1, depth, KindAfter(tokens, i + 3, tokens[i + 1].Value, "symbol-variable")));
                    break;

                case TokenType.Function when i + 1 < tokens.Length && tokens[i + 1].Type == TokenType.Identifier:
                    entries.Add(new OutlineEntry(tokens[i + 1].Value, tokens[i + 1].Line - 1, tokens[i + 1].Col - 1, depth, "symbol-method"));
                    break;

                case TokenType.Identifier when depth is > 0 and <= 4 && i + 1 < tokens.Length && tokens[i + 1].Type == TokenType.Colon:
                    entries.Add(new OutlineEntry(token.Value, token.Line - 1, token.Col - 1, depth, KindAfter(tokens, i + 2, token.Value, "symbol-field")));
                    break;
            }
        }

        return entries;
    }

    /// <summary>
    /// Helper method to tell what a declaration or a key is by the token its value starts with, an object, a list, a
    /// text, a number, a function and so on. A vector with colour in its name is a colour.
    /// </summary>
    private static string KindAfter(Token[] tokens, int at, string name, string otherwise)
    {
        if (at >= tokens.Length)
            return otherwise;

        Token value = tokens[at];
        return value.Type switch
        {
            // The lexer has the names the other way round, a { is an OpenBracket to it and a [ an OpenBrace
            TokenType.OpenBracket => "symbol-object",
            TokenType.OpenBrace => "symbol-array",
            TokenType.TextLiteral or TokenType.Quote => "symbol-string",
            TokenType.Number => "symbol-number",
            TokenType.Function => "symbol-method",
            TokenType.Null => "symbol-null",
            TokenType.Vector => name.Contains("colo", StringComparison.OrdinalIgnoreCase) ? "symbol-color" : "symbol-ruler",
            TokenType.Boolean => "symbol-boolean",
            TokenType.Identifier when value.Value == "vec" => name.Contains("colo", StringComparison.OrdinalIgnoreCase) ? "symbol-color" : "symbol-ruler",
            _ => otherwise
        };
    }
}
