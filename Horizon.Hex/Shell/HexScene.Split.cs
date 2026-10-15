using System.Numerics;

using Key = Silk.NET.Input.Key;

using Horizon.Rendering;
using Horizon.UI.Components;

namespace Horizon.Hex;

// Two files side by side, Ctrl+\ (VS Code's editor groups, two at the most). The moves and the characters, a layout's code
// and the texts it names, the shared file and the fighter's own. Both editors are in the code panel, one on its left and
// one on its right, and the document on screen (what the values, the outline and every command are about) is whichever
// side has the keyboard, a click into the other side makes that one it. A tab opened goes into the side that's on screen,
// "Open to the side" on a tab puts it in the other. Only code goes side by side, the designer, the table and an
// extension's view take the whole middle as they always did.
internal sealed partial class HexScene
{
    // The two sides while the middle is split, null for both when it isn't
    private HexDocument? splitLeft, splitRight;

    // The gap between the two
    private const float SPLIT_GAP = 4.0f;

    private bool Split => splitLeft is not null && splitRight is not null;

    private void BuildSplit()
    {
        shortcuts.Add(Key.BackSlash, ToggleSplit, control: true, whileTyping: true);
    }

    /// <summary>Ctrl+\, the file on screen put on the right of the one shown before it, or back to one side.</summary>
    private void ToggleSplit()
    {
        if (Split)
        {
            Unsplit();
            Say("one file in the middle again");
            return;
        }

        HexDocument? other = documents.LastOrDefault(open => open != document && open.IsText && open.Path is not null);
        if (other is null || !document.IsText)
        {
            Say("open another file of code to put next to this one", error: true);
            return;
        }

        splitLeft = other;
        splitRight = document;
        PlaceEditors();
        Say($"{other.Name} and {document.Name} side by side, Ctrl+\\ for one again");
    }

    /// <summary>A file opened in the side that isn't on screen, the middle split for it first if it wasn't.</summary>
    private void OpenToSide(HexDocument opened)
    {
        if (!opened.IsText || opened == document)
            return;

        HexDocument here = document;
        if (!Split)
        {
            splitLeft = here;
            splitRight = opened;
        }
        else if (here == splitLeft)
        {
            splitRight = opened;
        }
        else
        {
            splitLeft = opened;
        }

        Show(opened);
    }

    private void Unsplit()
    {
        splitLeft = splitRight = null;
        PlaceEditors();
    }

    /// <summary>
    /// The document that was on screen swapped for another, which side it goes in. One of the two already, it's that side
    /// that's on screen now, anything else goes in the side that was. Not code, the split ends.
    /// </summary>
    private void SplitFollows(HexDocument was, HexDocument shown)
    {
        if (!Split || shown == splitLeft || shown == splitRight)
            return;

        if (!shown.IsText || !documents.Contains(splitLeft!) || !documents.Contains(splitRight!))
        {
            splitLeft = splitRight = null;
            return;
        }

        if (was == splitLeft)
            splitLeft = shown;
        else
            splitRight = shown;
    }

    /// <summary>The editors in the code panel, the one on screen alone filling it, or the two of a split side by side.</summary>
    private void PlaceEditors()
    {
        foreach (var child in codeScroll.Children.ToArray())
        {
            if (child is CodeEditor)
                codeScroll.Remove(child);
        }

        if (!Split)
        {
            document.Editor.Fill = UIFill.Both;
            document.Editor.Anchor = Origin.Center;
            codeScroll.Add(document.Editor);
            return;
        }

        foreach (var (side, anchor) in new[] { (splitLeft!, Origin.Left), (splitRight!, Origin.Right) })
        {
            side.Editor.Fill = UIFill.Vertical;
            side.Editor.Anchor = anchor;
            codeScroll.Add(side.Editor);
        }

        FitSplit();
    }

    /// <summary>The two sides half the code panel each, as it's sized.</summary>
    private void FitSplit()
    {
        if (!Split)
            return;

        float half = MathF.Max(80.0f, (codeScroll.Size.X - SPLIT_GAP) * 0.5f);
        splitLeft!.Editor.Size = new Vector2(half, 0.0f);
        splitRight!.Editor.Size = new Vector2(half, 0.0f);
    }

    /// <summary>Every tick, a click into the side that isn't on screen makes it the one, and a side that was closed ends the split.</summary>
    private void UpdateSplit()
    {
        if (!Split)
            return;

        if (!documents.Contains(splitLeft!) || !documents.Contains(splitRight!))
        {
            HexDocument? left = documents.Contains(splitLeft!) ? splitLeft : documents.Contains(splitRight!) ? splitRight : null;
            Unsplit();
            if (left is not null && left != document)
                Show(left);
            return;
        }

        HexDocument other = document == splitLeft ? splitRight! : splitLeft!;
        if (compositor.Focus == other.Editor)
            Show(other);
    }
}
