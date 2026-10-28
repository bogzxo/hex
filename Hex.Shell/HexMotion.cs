using Horizon.UI.Components;

namespace Hex.Shell;

/// <summary>Shared UI motion also governs the terminal's explicit tweens, without rewriting its saved preferences.</summary>
internal static class HexMotion
{
    public static void Apply(TerminalPanel panel, TerminalSettings settings, float speed)
    {
        bool moving = settings.Animations && speed > 0.0f;
        panel.AnimationTime = moving ? settings.AnimationTime / speed : 0.0f;
        panel.CursorGlide = moving ? settings.CursorGlide : 0.0f;
        foreach (TerminalTab tab in panel.Tabs)
        {
            foreach (TerminalView view in tab.Views)
            {
                view.Animated = moving;
                view.CursorGlide = panel.CursorGlide;
            }
        }
    }
}
