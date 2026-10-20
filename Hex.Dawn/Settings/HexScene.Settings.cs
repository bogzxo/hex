using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// What the editor is set to (HexSettings), read as it starts and saved as it changes. The window it's set in is
// SettingsPage, its skin is in EditorSkin, the edges of its panels in Sides.
internal sealed partial class HexScene
{
    private HexSettings settings = null!;
    /// <summary>
    /// Helper method to read the settings and put them in force, the theme of the editor, the sizes its panels were
    /// left at, and to give the panels the edges they are dragged by. Once, as the editor is made.
    /// </summary>
    private void LoadSettings()
    {
        // The editor testing itself, or only checking layouts, comes up the same every time and keeps nothing
        bool kept = !options.SelfTest && options.Check.Length == 0;
        settings = new HexSettings(kept ? Horizon.UI.Components.TerminalFiles.Settings() : null, Path.Combine(AppContext.BaseDirectory, HexSettings.FILE));

        layersScroll = layout.Get<ScrollPanel>("layers_scroll");
        usualLeft = treeScroll.Size.X;
        usualRight = inspectorScroll.Size.X;
        usualLayers = layersScroll.Size.Y;

        ApplyPanelSizes(instantly: true);
        UseEditorSkin(settings.EditorSkin, settings.EditorTheme, save: false);

        // The edges between the parts of the editor, a tab on each to be found by. The hold is nearly all on the
        // outside, in the gap to the next panel, the inside of these edges is where their scroll bars are
        SideGrip(treeScroll, UIResize.Right, left: true);
        SideGrip(inspectorScroll, UIResize.Left, left: false);
        Grip(layersScroll, UIResize.Top, new Vector2(0.0f, MIN_LAYERS), new Vector2(0.0f, MAX_LAYERS), size => settings.LayersHeight = size.Y);
    }

    private void SaveSettings()
    {
        if (settings.Save() is { } problem)
            Say(problem, error: true);
    }

    private void AddSettingsCommands()
    {
        commands.Add("File/settings", "Settings...", OpenSettings, "Ctrl+,", "settings-gear", HexKeyUse.Bound);
    }
}
