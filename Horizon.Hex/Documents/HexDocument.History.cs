using System.Numerics;
using System.Text;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Hex;

// Going back and forth through the changes to a layout, and the entrances of what is in it.
internal sealed partial class HexDocument
{
    /* Undoing */

    /// <summary>
    /// Notes how the layout is now as something to come back to, if it has changed since the last time. The
    /// editor calls this whenever a change has come to rest, so dragging something across the canvas is one
    /// step back rather than a hundred.
    /// </summary>
    /// <returns>Whether there was anything new to note.</returns>
    public bool Record()
    {
        string now = GenerateBody();
        if (now == recorded)
            return false;

        undone.Add(recorded);
        if (undone.Count > MAX_HISTORY)
            undone.RemoveAt(0);

        // What was undone before this change was undone from a layout that is no more.
        redone.Clear();
        recorded = now;
        return true;
    }

    /// <summary>Forgets every step there was to go back to. The layout as it is now is where it starts.</summary>
    public void ResetHistory()
    {
        undone.Clear();
        redone.Clear();
        recorded = GenerateBody();
    }

    /// <summary>Takes the last change back.</summary>
    /// <returns>Whether there was one.</returns>
    public bool Undo() => Step(undone, redone);

    /// <summary>Makes the change that was last taken back again.</summary>
    /// <returns>Whether there was one.</returns>
    public bool Redo() => Step(redone, undone);

    private bool Step(List<string> from, List<string> to)
    {
        // A change that hasn't come to rest yet is a step of its own, and the one that is taken back first.
        if (from == undone)
            Record();

        if (from.Count == 0)
            return false;

        to.Add(recorded);
        recorded = from[^1];
        from.RemoveAt(from.Count - 1);

        // Whatever was selected is selected again if it is still there, components are found by their names.
        var selected = selection.Select(names.NameOf).OfType<string>().ToList();

        Load(recorded, Path, keepHistory: true);

        foreach (string name in selected)
        {
            if (Find(name) is { } found)
                selection.Add(found);
        }

        // Going back and forth is not the time for everything to make its entrance again.
        SettleIntros();
        return true;
    }

    /* Entrances */

    /// <summary>Plays the entrance of everything in the layout that has one, the way loading it in a program does.</summary>
    public void PlayIntros()
    {
        SettleIntros();
        Module.Root.PlayIntro();
    }

    /// <summary>Stops whatever is still on its way in and shows everything where it belongs.</summary>
    public void SettleIntros() => Settle(Module.Root);

    private static void Settle(UIComponent component)
    {
        component.Tweens.KillAll();
        component.VisualOffset = System.Numerics.Vector2.Zero;
        component.VisualScale = System.Numerics.Vector2.One;
        component.Opacity = 1.0f;

        foreach (var child in component.Children)
            Settle(child);
    }
}
