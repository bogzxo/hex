using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Horizon.Hex;

// What the editor is set to (HexSettings), the dialog it is set in, and the edges of its panels that can be dragged.
internal sealed partial class HexScene
{
    // How wide a row of the settings is, what it is called and what it is set with
    private const float SETTING_NAME_WIDTH = 210.0f, SETTING_VALUE_WIDTH = 250.0f;

    // The least and the most the panels can be dragged to, in the editor's units
    private const float MIN_SIDE = 180.0f, MAX_SIDE = 640.0f;
    private const float MIN_LAYERS = 40.0f, MAX_LAYERS = 360.0f;

    // How near an edge of what is selected in the canvas the pointer has hold of it, inside and out, in the layout's units
    private const float HANDLE_INSIDE = 7.0f, HANDLE_OUTSIDE = 5.0f;

    private HexSettings settings = null!;
    private ScrollPanel layersScroll = null!;

    // The skin of the editor that its colours were last taken from, see ColourChrome
    private UISkin? colouredFor;

    // The sizes the layout gives the panels, which is what resetting them goes back to
    private float usualLeft, usualRight, usualLayers;

    // What is being dragged bigger in the canvas, null for nothing
    private UIComponent? resizing;

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
        Grip(treeScroll, UIResize.Right, new Vector2(MIN_SIDE, 0.0f), new Vector2(MAX_SIDE, 0.0f), size => settings.LeftWidth = size.X);
        Grip(inspectorScroll, UIResize.Left, new Vector2(MIN_SIDE, 0.0f), new Vector2(MAX_SIDE, 0.0f), size => settings.RightWidth = size.X);
        Grip(layersScroll, UIResize.Top, new Vector2(0.0f, MIN_LAYERS), new Vector2(0.0f, MAX_LAYERS), size => settings.LayersHeight = size.Y);
    }

    private void Grip(UIComponent panel, UIResize edge, Vector2 least, Vector2 most, Action<Vector2> keep)
    {
        panel.Resizable = edge;
        panel.MinSize = least;
        panel.MaxSize = most;
        panel.GripSize = 2.0f;
        panel.GripReach = 8.0f;
        panel.OnResized = keep;
        panel.OnResizeEnded = _ => SaveSettings();
    }

    /// <summary>Helper method to give the panels the sizes the settings have for them, the layout's own where they have none.</summary>
    private void ApplyPanelSizes()
    {
        treeScroll.Size = treeScroll.Size with { X = settings.LeftWidth > 0.0f ? Math.Clamp(settings.LeftWidth, MIN_SIDE, MAX_SIDE) : usualLeft };
        inspectorScroll.Size = inspectorScroll.Size with { X = settings.RightWidth > 0.0f ? Math.Clamp(settings.RightWidth, MIN_SIDE, MAX_SIDE) : usualRight };
        layersScroll.Size = layersScroll.Size with { Y = settings.LayersHeight > 0.0f ? Math.Clamp(settings.LayersHeight, MIN_LAYERS, MAX_LAYERS) : usualLayers };

        left.Visible = !settings.SideBarHidden;
        right.Visible = !settings.PropertiesHidden;
    }

    private void SaveSettings()
    {
        if (settings.Save() is { } problem)
            Say(problem, error: true);
    }

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

        // What the editor sits on, a little darker than its boxes, and the activity bar darker again, VS Code's
        Engine.Graphics.ClearColor = new Vector4(box.X * 0.65f, box.Y * 0.65f, box.Z * 0.65f, 1.0f);
        activity.Color = new Vector4(box.X * 0.5f, box.Y * 0.5f, box.Z * 0.5f, 1.0f);
        top.Color = activity.Color;
        bottom.Color = box;

        // The status bar in the accent, dark enough for light words on it whichever theme
        Vector4 accent = skin.AccentColor;
        statusBar.Color = new Vector4(accent.X * 0.5f, accent.Y * 0.5f, accent.Z * 0.5f, 1.0f);
        statusBar.TextColor = new Vector4(0.93f, 0.95f, 0.98f, 1.0f);

        treeDirty = inspectorDirty = true;
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

        Selector Choice(string key, string[] choices, int chosen, Action<int> picked)
        {
            var selector = made.Get<Selector>(key);
            selector.Options = choices;
            selector.Index = chosen;
            selector.OnChanged = _ => picked(selector.Index);
            settingControls[key] = selector;
            return selector;
        }

        void Switch(string key, bool on, Action<bool> flipped)
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

        // The editor itself, in any skin there is, every theme of every one of them
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

        Choice("size", [.. Scales.Select(DescribeScale)], Math.Max(0, Array.IndexOf(Scales, compositor.Scale)), index => SetScale(Scales[index], remember: true));

        float[] codeZooms = [0.8f, 0.9f, 1.0f, 1.1f, 1.25f, 1.5f, 1.75f, 2.0f];
        Choice("code_zoom", [.. codeZooms.Select(zoom => $"{zoom * 100:0}%")], Math.Max(0, Array.FindIndex(codeZooms, zoom => MathF.Abs(zoom - settings.CodeZoom) < 0.03f)),
            index => ZoomCode(codeZooms[index] / settings.CodeZoom));

        var panels = made.Get<Button>("panels");
        panels.OnPressed = ResetPanels;
        settingControls["panels"] = panels;

        // The designer. What the layouts are shown in is the game's setting and not the editor's own. The theme has a row
        // of its own that is only there when the skin has themes, side by side they ran off the window
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

        Choice("snap", [.. HexSettings.Grids.Select(grid => grid > 0.0f ? $"every {grid:0}" : "off")], Math.Max(0, Array.IndexOf(HexSettings.Grids, settings.Snap)),
            index =>
            {
                settings.Snap = HexSettings.Grids[index];
                SaveSettings();
            });
        Switch("handles", settings.CanvasHandles, on => settings.CanvasHandles = on);

        // Files
        Switch("reopen", settings.ReopenLast, on => settings.ReopenLast = on);
        made.Get<Symbol>("extensions_icon").Color = compositor.Skin?.AccentColor;

        settingsPages.Select(SETTINGS_EDITOR);
    }

    /// <summary>Takes the settings window down, the x of it or Escape.</summary>
    private void CloseSettings()
    {
        if (settingsWindow is { } window)
            window.Parent?.Remove(window);

        settingsWindow = null;
        settingsPages = null;
        settingControls.Clear();
    }

    private void ResetPanels()
    {
        settings.LeftWidth = settings.RightWidth = settings.LayersHeight = settings.PanelHeight = 0.0f;
        bottom.Size = bottom.Size with { Y = 180.0f };
        ApplyPanelSizes();
        SaveSettings();
        Say("the panels are the size they come at");
    }

    /* Dragging what is selected in the canvas bigger */

    /// <summary>
    /// Helper method to let the one thing that is selected be dragged bigger by its edges, the tabs on them are the
    /// compositor's to draw. Returns whether the pointer is busy with that, in which case it is nobody else's.
    /// </summary>
    private bool UpdateCanvasHandles(UIPointer pointer, Vector2 inLayout, bool overCanvas, bool pressed)
    {
        if (resizing is { } held)
        {
            if (pointer.Down)
            {
                if (held.DragResize(inLayout, settings.Snap))
                    codeDirty = true;
            }
            else
            {
                held.EndResize();
                resizing = null;

                // The numbers in the inspector are from before the drag
                inspectorDirty = true;
                stage.HighlightLitGrips = UIResize.None;
            }

            return true;
        }

        UIComponent? only = settings.CanvasHandles && document.Selection.Count == 1 ? document.Selected : null;
        stage.HighlightGrips = only is null ? UIResize.None : UIResize.All;

        UIResize on = only is not null && overCanvas ? only.EdgesAt(inLayout, UIResize.All, HANDLE_INSIDE, HANDLE_OUTSIDE) : UIResize.None;
        stage.HighlightLitGrips = on;

        if (!pressed || on == UIResize.None)
            return false;

        only!.BeginResize(on, inLayout);
        stage.HighlightLitGrips = on;
        resizing = only;
        return true;
    }

    /// <summary>Helper method to have a move land on the grid, if there is one. What is left over stays with the pointer.</summary>
    private Vector2 Snapped(Vector2 moved)
    {
        if (settings.Snap <= 0.0f)
            return moved;

        return new Vector2(MathF.Truncate(moved.X / settings.Snap), MathF.Truncate(moved.Y / settings.Snap)) * settings.Snap;
    }
}
