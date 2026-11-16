using Horizon.UI;
using Horizon.Rendering;
using Horizon.UI.Components;

namespace Hex.Shell;

/// <summary>The same mode selector in both windows. Only the destination creates or switches a scene.</summary>
internal static class HexModeSwitch
{
    public static void SetScale(Button? destination, float scale)
    {
        if (destination?.Parent is not StackPanel row || scale <= 0)
            return;
        row.Spacing = 2.0f / scale;
        foreach (Button button in row.Children.OfType<Button>())
            button.LabelScale = 0.14f / scale;
    }

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
            button.Primary = accent && button.Selected;
    }

    public static bool RestoreFocus(UICompositor compositor, UIComponent? previous)
    {
        if (previous is null || previous.Module?.Compositor != compositor || !previous.IsShownInHierarchy || !previous.EnabledInHierarchy)
            return false;
        compositor.Focus = previous;
        return true;
    }

    public static Button Add(TitleBar bar, HexMode current, Action swap)
    {
        var row = bar.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 2.0f, Anchor = Origin.Right, Pivot = Origin.Right });
        Button? destination = null;
        foreach (HexMode mode in (ReadOnlySpan<HexMode>)[HexMode.Dawn, HexMode.Dusk])
        {
            bool active = mode == current;
            var button = row.Add(new Button(mode.ToString())
            {
                Icon = mode == HexMode.Dawn ? "code" : "terminal",
                Style = "button_flat",
                Selected = active,
                Animated = false,
                Tooltip = HexText.Get(active ? "modes.active" : "modes.switch", ("name", mode.ToString())),
                OnPressed = active ? null : swap
            });
            if (!active)
                destination = button;
        }
        SetScale(destination, bar.Module?.Compositor.Scale ?? 1.0f);
        return destination!;
    }
}
