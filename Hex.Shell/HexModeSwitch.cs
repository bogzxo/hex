using Horizon.UI;
using Horizon.Rendering;
using Horizon.UI.Components;

namespace Hex.Shell;

/// <summary>The same mode selector in both windows. Only the destination creates or switches a scene.</summary>
internal static class HexModeSwitch
{
    // How big the faces' names are written, the title bar's menus' size. They grow with the interface size like every word
    private const float LABEL_TEXT = 0.14f;

    /// <summary>
    /// The selector dressed for a skin. In a flush look (Solstice) the mode that's on is filled with the accent, which is
    /// the mode's own colour there, orange in Dawn and violet in Dusk. Every other look keeps the quiet pill.
    /// </summary>
    public static void Dress(Button? destination, Horizon.UI.Skinning.UISkin skin)
    {
        if (destination?.Parent is not StackPanel row)
            return;
        bool accent = skin.CornerRadius > 0.0f && HexLooks.Flush(skin);
        foreach (Button button in row.Children.OfType<Button>())
        {
            button.Primary = accent && button.Selected;
            // On a dark look the words on the accent are the ground's colour, white on Solstice's orange was 3.6:1 and
            // the dark ground on it 4.9:1. A light look's white on its deeper accent reads already
            System.Numerics.Vector4? ink = button.Primary && !skin.Palette.LightField ? skin.Palette.Field with { W = 1.0f } : null;
            button.LabelColor = ink;
            button.IconColor = ink;
        }
    }

    public static bool RestoreFocus(UICompositor compositor, UIComponent? previous)
    {
        if (previous is null || previous.Module?.Compositor != compositor || !previous.IsShownInHierarchy || !previous.EnabledInHierarchy)
            return false;
        compositor.Focus = previous;
        return true;
    }

    /// <summary>
    /// The faces on the title bar, Dawn, Noon and Dusk, the one showing marked and the others taking you there through
    /// <paramref name="pick"/>. Hands back a button of the row, which is what the row is found by.
    /// </summary>
    public static Button Add(TitleBar bar, HexFace current, Action<HexFace> pick)
    {
        var row = bar.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 2.0f, Anchor = Origin.Right, Pivot = Origin.Right });
        foreach (HexFace face in (ReadOnlySpan<HexFace>)[HexFace.Dawn, HexFace.Noon, HexFace.Dusk])
        {
            // No Agentic with No AI on, the row is the editor and the terminal
            if (face == HexFace.Noon && Hex.Core.HexAi.Off)
                continue;
            row.Add(new Button(face.Name()) { Icon = face.Icon(), Style = "button_flat", Animated = false, LabelScale = LABEL_TEXT });
        }
        Button first = (Button)row.Children[0];
        Select(first, current, pick);
        return first;
    }

    /// <summary>The face showing marked on the row, the rest made to take you to theirs. Dawn and Noon trade places without a new scene.</summary>
    public static void Select(Button? anyInRow, HexFace current, Action<HexFace> pick)
    {
        if (anyInRow?.Parent is not StackPanel row)
            return;

        foreach (Button button in row.Children.OfType<Button>())
        {
            // Known by the name it shows, which isn't the enum's own for the agents' face
            if (Enum.GetValues<HexFace>().Where(each => each.Name() == button.Label).Select(each => (HexFace?)each).FirstOrDefault() is not { } face)
                continue;
            bool active = face == current;
            button.Selected = active;
            button.Tooltip = HexText.Get(active ? "modes.active" : "modes.switch", ("name", face.Name()));
            button.OnPressed = active ? null : () => pick(face);
        }
    }
}
