using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Horizon.Hex;

// What the editor is set to (HexSettings), read as it starts and saved as it changes, and the settings window it's
// set in, a page of Hex's own and a tab for every extension with something to set. Its skin is in EditorSkin, the
// edges of its panels in Sides.
internal sealed partial class HexScene
{
    // How wide a row of the settings is, what it is called and what it is set with
    private const float SETTING_NAME_WIDTH = 210.0f, SETTING_VALUE_WIDTH = 250.0f;

    private HexSettings settings = null!;
    /// <summary>
    /// Helper method to read the settings and put them in force, the theme of the editor, the sizes its panels were
    /// left at, and to give the panels the edges they are dragged by. Once, as the editor is made.
    /// </summary>
    private void LoadSettings()
    {
        // The editor testing itself, or only checking layouts, comes up the same every time and keeps nothing
        bool kept = !options.SelfTest && options.Check.Length == 0;
        settings = new HexSettings(kept ? Path.Combine(AppContext.BaseDirectory, HexSettings.FILE) : null);

        layersScroll = layout.Get<ScrollPanel>("layers_scroll");
        usualLeft = treeScroll.Size.X;
        usualRight = inspectorScroll.Size.X;
        usualLayers = layersScroll.Size.Y;

        ApplyPanelSizes();
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

    /* The settings, a window of their own in the editor with a page for each part of it */

    private const int SETTINGS_EDITOR = 0, SETTINGS_DESIGNER = 1, SETTINGS_FILES = 2, SETTINGS_EXTENSIONS = 3;

    private const string SETTINGS_LAYOUT = "settings.hor";

    // The window while it is up, its pages, and what sets each setting by a name of its own, for the self test to find
    private WindowPanel? settingsWindow;
    private TabPanel? settingsPages;
    private readonly Dictionary<string, UIComponent> settingControls = [];

    /// <summary>
    /// Ctrl+, the settings, a window in the editor that is dragged about by its title bar and bigger by its corner, with a
    /// page each for the editor, the designer, files and extensions. It is settings.hor, the code only finds what sets
    /// each setting by its name and gives it its choices. Up already, it is brought to the front.
    /// </summary>
    private void OpenSettings()
    {
        if (settingsWindow is { Visible: true } open)
        {
            open.BringToFront();
            return;
        }

        settingControls.Clear();
        UILayout made = layout.Instantiate(SETTINGS_LAYOUT, chrome.Root);
        var window = settingsWindow = made.Get<WindowPanel>("window");
        window.OnClosed = CloseSettings;
        window.Color = compositor.Skin is { } skin ? skin.PanelColor with { W = 1.0f } : null;
        settingsPages = made.Get<TabPanel>("pages");

        FillEditorPage(made);
        FillDesignerPage(made);

        // Files
        SettingSwitch(made, "reopen", settings.ReopenLast, on => settings.ReopenLast = on);
        made.Get<Symbol>("extensions_icon").Color = compositor.Skin?.AccentColor;
        FillExtensionsPage(made);
        AddExtensionTabs(made);

        settingsPages.Select(SETTINGS_EDITOR);
    }

    /// <summary>A selector of the settings given its choices, which one is chosen and what picking another does.</summary>
    private Selector SettingChoice(UILayout made, string key, string[] choices, int chosen, Action<int> picked)
    {
        var selector = made.Get<Selector>(key);
        selector.Options = choices;
        selector.Index = chosen;
        selector.OnChanged = _ => picked(selector.Index);
        settingControls[key] = selector;
        return selector;
    }

    /// <summary>A toggle of the settings that says on or off, saved as it's flipped.</summary>
    private void SettingSwitch(UILayout made, string key, bool on, Action<bool> flipped)
    {
        var toggle = made.Get<ToggleButton>(key);
        toggle.State = on;
        toggle.Label = Said(on ? "settings.on" : "settings.off");
        toggle.OnPressed = () =>
        {
            toggle.Label = Said(toggle.State ? "settings.on" : "settings.off");
            flipped(toggle.State);
            SaveSettings();
        };
        settingControls[key] = toggle;
    }

    /// <summary>The editor's page, the skin it's drawn in (every theme of every skin there is), how big, the code's zoom, its panels.</summary>
    private void FillEditorPage(UILayout made)
    {
        var editorSkins = EditorSkins();
        var skinDropdown = made.Get<Dropdown>("theme");
        skinDropdown.Options = [.. editorSkins.Select(choice => choice.Text)];
        int wearing = editorSkins.FindIndex(choice => choice.Skin.Name == settings.EditorSkin && (choice.Theme ?? string.Empty) == settings.EditorTheme);
        skinDropdown.Value = editorSkins[Math.Max(0, wearing)].Text;
        skinDropdown.OnChanged = chosen =>
        {
            int index = editorSkins.FindIndex(choice => choice.Text == chosen);
            if (index >= 0)
                UseEditorSkin(editorSkins[index].Skin.Name, editorSkins[index].Theme, save: true);
        };
        settingControls["theme"] = skinDropdown;

        SettingChoice(made, "size", [.. Scales.Select(DescribeScale)], Math.Max(0, Array.IndexOf(Scales, compositor.Scale)), index => SetScale(Scales[index], remember: true));

        float[] codeZooms = [0.8f, 0.9f, 1.0f, 1.1f, 1.25f, 1.5f, 1.75f, 2.0f];
        SettingChoice(made, "code_zoom", [.. codeZooms.Select(zoom => $"{zoom * 100:0}%")], Math.Max(0, Array.FindIndex(codeZooms, zoom => MathF.Abs(zoom - settings.CodeZoom) < 0.03f)),
            index => ZoomCode(codeZooms[index] / settings.CodeZoom));

        var panels = made.Get<Button>("panels");
        panels.OnPressed = ResetPanels;
        settingControls["panels"] = panels;
    }

    /// <summary>
    /// The designer's page. What the layouts are shown in is the game's setting and not the editor's own. The theme has a
    /// row of its own that is only there when the skin has themes, side by side they ran off the window.
    /// </summary>
    private void FillDesignerPage(UILayout made)
    {
        var skins = made.Get<Selector>("skin");
        var layoutThemes = made.Get<Selector>("layout_theme");
        var themeRow = made.Get<UIComponent>("layout_theme_row");
        settingControls["skin"] = skins;
        settingControls["layout_theme"] = layoutThemes;

        bool showingSkin = false;
        void ShowSkin()
        {
            showingSkin = true;
            UISkinChoice current = UISkins.Current;
            skins.Options = [.. UISkins.Available.Select(skin => skin.Title)];
            skins.Index = Math.Max(0, UISkins.Available.ToList().FindIndex(skin => skin.Name == current.Name));

            themeRow.Visible = current.Themes.Count > 0;
            if (current.Themes.Count > 0)
            {
                layoutThemes.Options = [.. current.Themes];
                layoutThemes.Index = Math.Max(0, current.Themes.ToList().IndexOf(UISkins.Theme ?? current.UsualTheme ?? current.Themes[0]));
            }

            showingSkin = false;
        }

        skins.OnChanged = _ =>
        {
            if (showingSkin || (uint)skins.Index >= (uint)UISkins.Available.Count)
                return;

            // Another skin comes up in its usual theme
            UseSkin(UISkins.Available[skins.Index], null);
            ShowSkin();
        };
        layoutThemes.OnChanged = _ =>
        {
            if (showingSkin || UISkins.Current.Themes.Count == 0)
                return;

            UseSkin(UISkins.Current, UISkins.Current.Themes[Math.Clamp(layoutThemes.Index, 0, UISkins.Current.Themes.Count - 1)]);
        };
        ShowSkin();

        SettingChoice(made, "snap", [.. HexSettings.Grids.Select(grid => grid > 0.0f ? $"every {grid:0}" : "off")], Math.Max(0, Array.IndexOf(HexSettings.Grids, settings.Snap)),
            index =>
            {
                settings.Snap = HexSettings.Grids[index];
                SaveSettings();
            });
        SettingSwitch(made, "handles", settings.CanvasHandles, on => settings.CanvasHandles = on);
    }

    /// <summary>Takes the settings window down, the x of it or Escape.</summary>
    private void CloseSettings()
    {
        if (settingsWindow is { } window)
            window.Parent?.Remove(window);

        settingsWindow = null;
        settingsPages = null;
        settingControls.Clear();
        ForgetExtensionTabs();
    }

}
