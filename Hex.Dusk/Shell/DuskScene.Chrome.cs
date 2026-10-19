using System.Numerics;

using Horizon.Engine;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;

namespace Hex.Dusk;

// The window round the panel. How big it draws itself, the panel fitted between the bars, the menus, the status bar
// and the title.
internal sealed partial class DuskScene
{
    // How big the menus in the title bar are written, at a scale of one
    private const float MENU_TEXT = 0.14f;
    private static readonly float[] Scales = [0.75f, 1.0f, 1.25f, 1.5f, 1.75f, 2.0f];

    private void SetScale(float scale)
    {
        scale = Scales.MinBy(known => MathF.Abs(known - scale));
        compositor.Scale = scale;

        // The title bar is the window's and the size of the system's whatever the rest is drawn at
        menu.TextScale = MENU_TEXT / scale;
        settings.Scale = scale;
    }

    /// <summary>The panel gets whatever the window has between the title bar and the status bar.</summary>
    private void FitChrome()
    {
        UIRect screen = chrome.Root.Bounds;
        if (screen.IsEmpty || top.Bounds.IsEmpty)
            return;

        float height = MathF.Floor(screen.Height - root.Padding.Total.Y - top.Bounds.Height - statusBar.Bounds.Height);
        float width = MathF.Floor(screen.Width - root.Padding.Total.X);
        var wanted = new Vector2(MathF.Max(200.0f, width), MathF.Max(120.0f, height));
        if (panel.Size != wanted)
            panel.Size = wanted;
    }

    /* The panel */

    /// <summary>What Dusk wants of the panel on top of what the host set up, its title and the tab's menu.</summary>
    private void BuildPanel()
    {
        panel.WorkingDirectory = options.StartIn;
        panel.OnChanged += PanelChanged;
        panel.OnTabMenu = ShowTabMenu;
    }

    /// <summary>The font a folder of Assets/fonts holds, the first .ttf in it, null for the skin's.</summary>
    private static UIFont? FontOf(string folder)
    {
        if (folder.Length == 0)
            return null;

        string directory = Path.Combine("Assets", "fonts", folder);
        if (!Directory.Exists(directory))
            return null;

        string? file = Directory.GetFiles(directory, "*.ttf").Select(Path.GetFileName).Order().FirstOrDefault();
        return file is null ? null : UIFont.Shared(directory + "/", file, 96, terminal: true);
    }

    private void PanelChanged()
    {
        string title = panel.Current?.Title ?? "Dusk";
        top.Title = panel.Tabs.Count > 0 ? $"{title}  -  Dusk {VERSION}" : $"Dusk {VERSION}";
        Engine.WindowManager.Title = panel.Tabs.Count > 0 ? $"{title} - Dusk" : "Dusk";

        // The last tab closed closes the window, as a terminal does
        if (panel.Tabs.Count == 0 && !options.SelfTest)
            Quit(0);
    }

    private void ShowTabMenu(TerminalTab tab, Vector2 point)
    {
        var shown = new Menu("tab");
        shown.Add("Close", () => panel.CloseTab(tab), KeyOf("close_tab"), icon: "close");
        shown.Add("Close the others", () =>
        {
            foreach (TerminalTab other in panel.Tabs.ToArray())
                if (other != tab) panel.CloseTab(other);
        }, icon: "close-all");
        shown.Add("Rename", () => host.AskRename(tab), icon: "edit");
        shown.AddSeparator();
        shown.Add("Move left", () => panel.MoveTab(tab, panel.IndexOf(tab) - 1), icon: "arrow-left");
        shown.Add("Move right", () => panel.MoveTab(tab, panel.IndexOf(tab) + 1), icon: "arrow-right");
        ContextMenu.Show(chrome, point, shown);
    }

    /* The menus, every item a command run by its name, so the keys and the palette say the same thing */

    private void BuildMenus()
    {
        Menu terminal = menu.AddMenu("Terminal");
        Item(terminal, "New tab", "new_tab", "add");
        Item(terminal, "New tab with...", "new_tab_with", "list-selection");
        terminal.AddSeparator();
        Item(terminal, "Split right", "split_right", "split-horizontal");
        Item(terminal, "Split down", "split_down", "split-vertical");
        Item(terminal, "Close pane", "close_pane", "close");
        terminal.AddSeparator();
        Item(terminal, "Close", "close_tab", "close");
        Item(terminal, "Close the whole tab", "close_whole_tab", "close-all");
        Item(terminal, "Rename tab", "rename_tab", "edit");
        terminal.AddSeparator();
        Item(terminal, "Settings file", "open_settings", "settings-gear");
        Item(terminal, "Keys file", "open_keys", "keyboard");
        terminal.AddSeparator();
        Item(terminal, "Quit", "quit", "sign-out");

        Menu view = menu.AddMenu("View");
        Item(view, "Command palette", "palette", "search");
        view.AddSeparator();
        Menu themes = view.AddMenu("Theme", "color-mode");
        foreach (var (name, title) in Themes())
        {
            string picked = name;
            MenuItem item = themes.Add(title, () => UseTheme(picked));
            item.IsChecked = () => settings.Theme == picked;
        }

        Item(view, "Next theme", "theme_next", "color-mode");
        MenuItem animations = Item(view, "Animations", "animations", "play");
        animations.IsChecked = () => settings.Animations;
        view.AddSeparator();
        Item(view, "Bigger text", "zoom_in", "zoom-in");
        Item(view, "Smaller text", "zoom_out", "zoom-out");
        Item(view, "Text as it comes", "zoom_reset", "screen-normal");
        view.AddSeparator();
        Item(view, "Focus left", "focus_left", "arrow-left");
        Item(view, "Focus right", "focus_right", "arrow-right");
        Item(view, "Focus up", "focus_up", "arrow-up");
        Item(view, "Focus down", "focus_down", "arrow-down");

        Menu help = menu.AddMenu("Help");
        Item(help, "Keyboard shortcuts", "keys", "keyboard");
        Item(help, "Plugins", "plugins", "extensions");
        help.AddSeparator();
        help.Add("About Dusk", () => Notify($"Dusk {VERSION}, a terminal made with Horizon. The emulator and the panel are the engine's, this window and its plugins are Dusk."), icon: "info");

        RefreshMenuKeys();
    }

    /// <summary>A menu item that runs a command by name, greyed while no plugin has registered one by that name.</summary>
    private MenuItem Item(Menu menu, string label, string command, string icon)
    {
        MenuItem item = menu.Add(label, () => Run(command), icon: icon);
        item.IsEnabled = () => host.Commands.Contains(command);
        wiredItems.Add((item, command));
        return item;
    }

    private readonly List<(MenuItem Item, string Command)> wiredItems = [];

    /// <summary>The keys written on the menu items read off the commands again, after keys.hor or a plugin changed one.</summary>
    private void RefreshMenuKeys()
    {
        foreach (var (item, command) in wiredItems)
            item.Shortcut = KeyOf(command);
    }

    /* The status bar */

    private void BuildStatusBar()
    {
        messageItem = statusBar.Add(string.Empty, tooltip: "What Dusk last said");
        themeItem = statusBar.Add(string.Empty, right: true, pressed: () => Run("theme_next"), tooltip: "The theme, click for the next");
        sizeItem = statusBar.Add(string.Empty, right: true, tooltip: "How many columns and rows the focused terminal has");
        profileItem = statusBar.Add(string.Empty, right: true, pressed: () => Run("new_tab_with"), tooltip: "The shell of the focused terminal, click for another in a new tab");
    }

    private (int Columns, int Rows) sizeShown;
    private TerminalProfile? profileShown;
    private string themeShown = string.Empty;

    private void UpdateStatusBar()
    {
        if (messageItem.Text.Length > 0 && System.Environment.TickCount64 - saidAt > SAID_FOR)
        {
            messageItem.Text = string.Empty;
            messageItem.Color = null;
        }

        TerminalView? view = panel.FocusedView;
        var size = view is null ? (0, 0) : (view.Columns, view.Rows);
        if (size != sizeShown)
        {
            sizeShown = size;
            sizeItem.Text = view is null ? string.Empty : $"{size.Item1} x {size.Item2}";
        }

        TerminalProfile? profile = view?.Profile;
        if (profile != profileShown)
        {
            profileShown = profile;
            profileItem.Text = profile is null ? string.Empty : $"$({profile.Icon}) {profile.Name}";
        }

        if (themeShown != settings.Theme)
        {
            themeShown = settings.Theme;
            themeItem.Text = $"$(color-mode) {TitleOfTheme(settings.Theme)}";
        }
    }
}
