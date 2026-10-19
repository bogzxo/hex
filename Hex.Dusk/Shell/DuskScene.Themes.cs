using System.Numerics;

using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dusk;

// The themes, which are the flat skin's (Assets/uix/flat/themes, a palette each), the window drawn in one and the
// terminals' sixteen colours worked out of its palette (TerminalPalette.FromPalette).
internal sealed partial class DuskScene
{
    /// <summary>The themes there are, by name with their titles, the skin's usual one first.</summary>
    private static List<(string Name, string Title)> Themes()
    {
        var themes = new List<(string, string)>();
        UISkinChoice? flat = UISkins.Find(SKIN_NAME);
        if (flat is null)
            return themes;

        foreach (string theme in flat.Themes)
            themes.Add((theme, TitleOfTheme(theme)));

        return themes;
    }

    private static string TitleOfTheme(string theme)
    {
        UISkinChoice? flat = UISkins.Find(SKIN_NAME);
        if (flat is not null)
        {
            foreach (UIThemeInfo info in UISkin.ThemesOf(flat.Directory, flat.File))
            {
                if (info.Name == theme)
                    return info.Title;
            }
        }

        return theme.Length == 0 ? "Dark" : char.ToUpperInvariant(theme[0]) + theme[1..];
    }

    /// <summary>A theme the skin has, or its usual one.</summary>
    private static string ThemeOrUsual(string theme)
    {
        UISkinChoice? flat = UISkins.Find(SKIN_NAME);
        return flat is not null && flat.Has(theme) ? theme : flat?.UsualTheme ?? theme;
    }

    /// <summary>Draws the window and the terminals in another theme, and keeps it.</summary>
    private void UseTheme(string theme)
    {
        theme = ThemeOrUsual(theme);
        settings.Theme = theme;
        compositor.SetSkin(SKIN_DIRECTORY, SKIN_FILE, theme);
        host.SaveSettings();
        Say($"{TitleOfTheme(theme)}");
        host.Raise("on_theme", new Horizon.HIDL.Runtime.StringValue(theme));
    }

    /// <summary>The theme after the one that is on, round to the first past the last.</summary>
    private void NextTheme()
    {
        var themes = Themes();
        if (themes.Count == 0)
            return;

        int index = themes.FindIndex(theme => theme.Name == settings.Theme);
        UseTheme(themes[(index + 1) % themes.Count].Name);
    }

    /// <summary>The terminals' colours out of the skin that is on, and the dark behind everything.</summary>
    private void ApplyTheme()
    {
        if (compositor.Skin is not { } skin)
            return;

        panel.Palette = TerminalPalette.FromPalette(skin.Palette);
        panel.ApplyPalette();
        ColourWindow();
    }

    private void ColourWindow()
    {
        if (compositor.Skin is { } skin)
            Engine.Graphics.ClearColor = skin.PanelColor with { W = 1.0f };
    }

}
