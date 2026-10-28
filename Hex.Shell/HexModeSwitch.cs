using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Shell;

/// <summary>The same mode selector in both windows. Only the destination creates or switches a scene.</summary>
internal static class HexModeSwitch
{
    public static bool RestoreFocus(UICompositor compositor, UIComponent? previous)
    {
        if (previous?.Module?.Compositor != compositor)
            return false;
        for (UIComponent? at = previous; at is not null; at = at.Parent)
            if (!at.Visible || !at.Enabled || at.IsOnHiddenLayer)
                return false;
        compositor.Focus = previous;
        return true;
    }

    public static Button Add(TitleBar bar, HexMode current, Action swap)
    {
        var row = bar.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 2.0f });
        Button? destination = null;
        foreach (HexMode mode in (ReadOnlySpan<HexMode>)[HexMode.Dawn, HexMode.Dusk])
        {
            bool active = mode == current;
            var button = row.Add(new Button(mode.ToString())
            {
                Icon = mode == HexMode.Dawn ? "code" : "terminal",
                Style = "button_flat",
                Selected = active,
                Indicator = UIResize.Bottom,
                Animated = false,
                Tooltip = HexText.Get(active ? "modes.active" : "modes.switch", ("name", mode.ToString())),
                OnPressed = active ? null : swap
            });
            if (!active)
                destination = button;
        }
        return destination!;
    }
}
