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

    private readonly record struct OutlineEntry(string Name, int Line, int Column, int Depth);

    /// <summary>
    /// What a file declares (its lets and consts) and the keys of the objects in those, a few levels down, by its tokens
    /// so a file that doesn't parse right now still has one.
    /// </summary>
    private static List<OutlineEntry> Outline(string text)
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
                    entries.Add(new OutlineEntry(tokens[i + 1].Value, tokens[i + 1].Line - 1, tokens[i + 1].Col - 1, depth));
                    break;

                case TokenType.Identifier when depth is > 0 and <= 4 && i + 1 < tokens.Length && tokens[i + 1].Type == TokenType.Colon:
                    entries.Add(new OutlineEntry(token.Value, token.Line - 1, token.Col - 1, depth));
                    break;
            }
        }

        return entries;
    }

    private void RebuildOutline()
    {
        treeTitle.Text = Said("side.outline_of", ("file", document.Name.ToUpperInvariant()));

        List<OutlineEntry> entries = Outline(code.Text);
        var items = layout.Populate(tree, entries.Count);

        for (int i = 0; i < items.Count; i++)
        {
            OutlineEntry entry = entries[i];
            var row = items[i].Get<Button>("row");
            var caption = items[i].Get<Label>("caption");

            caption.Text = entry.Depth == 0 ? $"$(symbol-variable) {entry.Name}" : $"$(symbol-field) {entry.Name}";
            caption.Position = new Vector2(10 + entry.Depth * 14, 0);
            caption.Color = entry.Depth == 0 ? null : DimColor;
            row.OnPressed = () =>
            {
                code.GoTo(entry.Line, entry.Column);
                code.Focus();
            };
        }

        if (entries.Count == 0)
            Hint(tree, "nothing declared in here");

        RebuildLayers();
    }
}
