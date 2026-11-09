using Hex.Core;

namespace Hex.Dawn;

// Visual Studio's outlining, Collapse to Definitions (Ctrl+M Ctrl+O with its keys) folding the body of every member and
// leaving the types open, and Toggle All Outlining (Ctrl+M Ctrl+L) folding the lot or opening it again. They go by the
// outline the side shows, so a member is what it says is one.
internal sealed partial class DawnScene
{
    // How far under a member's name its opening brace is looked for, a signature broken over a few lines
    private const int BODY_LOOK = 8;

    /// <summary>Everything opened, then the body of every method, property and event folded.</summary>
    private void CollapseToDefinitions()
    {
        code.UnfoldAll();
        int lines = code.Document.LineCount;
        foreach (OutlineEntry entry in OutlineHere())
        {
            if (entry.Icon is not ("symbol-method" or "symbol-property" or "symbol-event"))
                continue;

            for (int line = entry.Line; line < lines && line <= entry.Line + BODY_LOOK; line++)
            {
                string text = code.Document.LineAt(line);

                // Folded on its brace, a bracket of the parameters folds them and not the body. A line that ends it
                // first (an abstract one, one with an arrow) has no body to fold
                if (text.Contains('{') && code.CanFold(line))
                {
                    if (!code.FoldedLines.Contains(line))
                        code.ToggleFold(line);
                    break;
                }

                if (text.TrimEnd().EndsWith(';') || text.Contains("=>", StringComparison.Ordinal))
                    break;
            }
        }
    }

    /// <summary>Everything opened when anything's folded, otherwise everything folded.</summary>
    private void ToggleAllOutlining()
    {
        if (code.FoldedLines.Count > 0)
            code.UnfoldAll();
        else
            code.FoldAll(1);
    }
}
