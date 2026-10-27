using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// What the editor itself is drawn in, any skin there is in any of its themes, and the colours of its own layout taken
// from that skin.
internal sealed partial class DawnScene
{
    // The skin of the editor that its colours were last taken from, see ColourChrome
    private UISkin? colouredFor;

    /// <summary>Helper method to draw the editor itself in another theme of the flat skin.</summary>
    private void UseEditorTheme(string theme, bool save) => UseEditorSkin(TOOL_SKIN_NAME, theme, save);

    /// <summary>
    /// Helper method to draw the editor itself in any skin there is, in one of its themes (null for its usual), however
    /// obnoxious. A skin that isn't there any more is the flat one.
    /// </summary>
    private void UseEditorSkin(string name, string? theme, bool save)
    {
        UISkinChoice? skin = UISkins.Find(name);
        if (skin is null)
        {
            skin = UISkins.Find(TOOL_SKIN_NAME);
            theme = null;
        }

        if (theme is not null && skin is not null && !skin.Has(theme))
            theme = skin.UsualTheme;

        settings.EditorSkin = skin?.Name ?? TOOL_SKIN_NAME;
        settings.EditorTheme = theme ?? skin?.UsualTheme ?? string.Empty;

        if (skin is null)
            compositor.SetSkin(TOOL_SKIN_DIRECTORY, TOOL_SKIN, theme);
        else
            compositor.SetSkin(skin.Directory, skin.File, theme);

        if (save)
        {
            SaveSettings();
            Say(theme is null ? $"the editor is in {skin?.Title}" : $"the editor is in {skin?.Title}, {theme}");
        }
    }

    /// <summary>Every skin there is in every theme of it, the flat one first, as the dropdown of the editor's skin lists them.</summary>
    private static List<(UISkinChoice Skin, string? Theme, string Text)> EditorSkins()
    {
        var all = new List<(UISkinChoice, string?, string)>();
        foreach (UISkinChoice skin in UISkins.Available.OrderBy(skin => skin.Name == TOOL_SKIN_NAME ? 0 : 1))
        {
            if (skin.Themes.Count == 0)
                all.Add((skin, null, skin.Title));

            foreach (string theme in skin.Themes)
                all.Add((skin, theme, EditorSkinText(skin, theme)));
        }

        return all;
    }

    /// <summary>
    /// What a theme of a skin the editor can be drawn in is called in the settings. The editor's own skin's by the theme
    /// alone, "Horizon", others by the theme and the skin. It said "Flat, horizon", and Flat is a look of Hex's as well.
    /// </summary>
    private static string EditorSkinText(UISkinChoice skin, string theme)
    {
        string named = theme.Replace('_', ' ');
        named = named.Length > 0 ? char.ToUpperInvariant(named[0]) + named[1..] : named;
        return skin.Name == TOOL_SKIN_NAME ? Titled(theme) : $"{named}, {skin.Title}";
    }

    // A theme of the editor's own skin by every word of its name, Visual Studio and not Visual studio
    private static string Titled(string theme) =>
        string.Join(' ', theme.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(word => char.ToUpperInvariant(word[0]) + word[1..]));

    /// <summary>
    /// Helper method to take the colours the editor's layout has written into it (the boxes of its lists, what is
    /// behind everything) from its skin once that has changed, a layout can only say a colour, not "whatever the
    /// skin has for a panel". Every update, it does nothing until the skin is another one.
    /// </summary>
    private void ColourChrome()
    {
        if (compositor.Skin is not { } skin || ReferenceEquals(skin, colouredFor))
            return;

        colouredFor = skin;

        // Not the panel under the editor, a tab panel's Color is what the tabs that aren't open are written in. It was
        // given the colour of the boxes here and OUTPUT was written in the colour of what it was written on
        bottom.Color = skin.TextColor with { W = 0.6f };

        // The status bar the colour of the title bar and the activity bar (DressChrome), so the window has one frame round
        // the editor and not three colours of one. The accent tinted it olive in Horizon, and half the accent was a muddy brown
        statusBar.TextColor = null;

        // The field of light in the theme's colours, and the frame dressed in them, glass or solid, see DawnScene.Materials
        glow.UseSkin(skin);
        DressChrome();
    }
}
