using System.Text;

using Horizon.UI.Scripting;

namespace Hex.Core.Notebooks;

/// <summary>
/// Text written with ANSI escapes (a Python traceback, what a program printed in colour) as the text alone and where its
/// colours are, as spans of the sixteen Ansi kinds an editor draws in the terminal's colours. Bold makes the eight plain
/// colours the bright ones, the way most terminals have it. Colours by number past sixteen, backgrounds and the rest of
/// what escapes say are dropped with the escapes, the text stays.
/// </summary>
internal static class AnsiText
{
    /// <summary>The text without its escapes, and the coloured pieces of it by line and column.</summary>
    public static (string Text, List<SemanticSpan> Colours) Read(string written)
    {
        var text = new StringBuilder(written.Length);
        var colours = new List<SemanticSpan>();
        int line = 0, column = 0, colour = -1, from = 0;
        bool bold = false;

        void Close()
        {
            if (colour >= 0 && column > from)
                colours.Add(new SemanticSpan(line, from, column - from, SyntaxKind.Ansi0 + Shown()));
            from = column;
        }

        int Shown() => bold && colour < 8 ? colour + 8 : colour;

        for (int i = 0; i < written.Length; i++)
        {
            char c = written[i];
            if (c == '\x1b' && i + 1 < written.Length && written[i + 1] == '[')
            {
                int end = i + 2;
                while (end < written.Length && written[end] is >= '0' and <= '9' or ';' or '?')
                    end++;
                if (end >= written.Length)
                    break;

                if (written[end] == 'm')
                {
                    Close();
                    Apply(written[(i + 2)..end], ref colour, ref bold);
                }

                i = end;
                continue;
            }

            if (c == '\n')
            {
                Close();
                text.Append('\n');
                line++;
                column = from = 0;
                continue;
            }

            text.Append(c);
            column++;
        }

        Close();
        return (text.ToString(), colours);
    }

    // Helper method for what one SGR escape says, its numbers one after the other
    private static void Apply(string said, ref int colour, ref bool bold)
    {
        string[] parts = said.Length == 0 ? ["0"] : said.Split(';');
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out int code))
                continue;

            switch (code)
            {
                case 0:
                    colour = -1;
                    bold = false;
                    break;
                case 1:
                    bold = true;
                    break;
                case 22:
                    bold = false;
                    break;
                case >= 30 and <= 37:
                    colour = code - 30;
                    break;
                case >= 90 and <= 97:
                    colour = code - 90 + 8;
                    break;
                case 39:
                    colour = -1;
                    break;
                case 38 when i + 2 < parts.Length && parts[i + 1] == "5":
                    if (int.TryParse(parts[i + 2], out int numbered) && numbered < 16)
                        colour = numbered;
                    i += 2;
                    break;
                case 38 when i + 4 < parts.Length && parts[i + 1] == "2":
                case 48 when i + 4 < parts.Length && parts[i + 1] == "2":
                    i += 4;
                    break;
                case 48 when i + 2 < parts.Length && parts[i + 1] == "5":
                    i += 2;
                    break;
            }
        }
    }
}
