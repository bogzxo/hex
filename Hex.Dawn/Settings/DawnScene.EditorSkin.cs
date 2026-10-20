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
                all.Add((skin, theme, $"{skin.Title}, {theme}"));
        }

        return all;
    }

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

        Vector4 box = skin.PanelColor with { W = 1.0f };
        treeScroll.Color = layersScroll.Color = codeScroll.Color = inspectorScroll.Color = box;
        running.ValuesTree.Color = sideTree.Color = box;

        // What the editor sits on, a little darker than its boxes, and the activity bar darker again, VS Code's
        Engine.Graphics.ClearColor = new Vector4(box.X * 0.65f, box.Y * 0.65f, box.Z * 0.65f, 1.0f);
        activity.Color = new Vector4(box.X * 0.5f, box.Y * 0.5f, box.Z * 0.5f, 1.0f);
        top.Color = activity.Color;

        // Not the panel under the editor, a tab panel's Color is what the tabs that aren't open are written in. It was
        // given the colour of the boxes here and OUTPUT was written in the colour of what it was written on
        bottom.Color = skin.TextColor with { W = 0.6f };

        // The status bar as the engine has it, the panel tinted with the accent and whichever text reads on it, the same
        // as Dusk's. Half the accent with white on it was a muddy brown in Horizon
        statusBar.Color = null;
        statusBar.TextColor = null;

        treeDirty = inspectorDirty = true;
    }
}
