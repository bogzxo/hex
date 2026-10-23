using System.Numerics;

using Hex.Shell;

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

    /// <summary>
    /// The font a folder of Assets/fonts holds, the first .ttf in it, null for the skin's. A folder that isn't there is Hack,
    /// the skin's font isn't a monospace and the cursor ended up yards past the prompt.
    /// </summary>
    private static UIFont? FontOf(string folder)
    {
        if (folder.Length == 0)
            return null;

        string directory = Path.Combine("Assets", "fonts", folder);
        if (!Directory.Exists(directory))
            directory = Path.Combine("Assets", "fonts", "hack");
        if (!Directory.Exists(directory))
            return null;

        string? file = Directory.GetFiles(directory, "*.ttf").Select(Path.GetFileName).Order().FirstOrDefault();
        return file is null ? null : UIFont.Shared(directory + "/", file, 96, terminal: true);
    }

    private void PanelChanged()
    {
        ShowTitle();

        // The last tab closed closes the window, as a terminal does
        if (panel.Tabs.Count == 0 && !options.SelfTest)
            Quit(0);
    }

    private string? titleShown;

    /// <summary>
    /// The title bar and the window say what the tab says. A shell moving folders doesn't tell the panel anything, so the
    /// status bar asks again every frame, or the window stayed "Windows PowerShell" while the tab said "hexbuild".
    /// </summary>
    private void ShowTitle()
    {
        titleShown = panel.Current?.Title;
        string title = titleShown ?? HexText.Get("dusk_chrome.name");
        top.Title = panel.Tabs.Count > 0
            ? HexText.Get("dusk_chrome.title_tab", ("title", title), ("version", VERSION))
            : HexText.Get("dusk_chrome.title", ("version", VERSION));
        Engine.WindowManager.Title = panel.Tabs.Count > 0
            ? HexText.Get("dusk_chrome.window_title_tab", ("title", title))
            : HexText.Get("dusk_chrome.name");
    }

    private void ShowTabMenu(TerminalTab tab, Vector2 point)
    {
        var shown = new Menu("tab");
        shown.Add(HexText.Get("tabs.close"), () => panel.CloseTab(tab), KeyOf("close_tab"), icon: "close");
        shown.Add(HexText.Get("tabs.close_others"), () =>
        {
            foreach (TerminalTab other in panel.Tabs.ToArray())
                if (other != tab) panel.CloseTab(other);
        }, icon: "close-all");
        shown.Add(HexText.Get("code_hints.rename"), () => host.AskRename(tab), icon: "edit");
        shown.AddSeparator();
        shown.Add(HexText.Get("dusk_chrome.move_left"), () => panel.MoveTab(tab, panel.IndexOf(tab) - 1), icon: "arrow-left");
        shown.Add(HexText.Get("dusk_chrome.move_right"), () => panel.MoveTab(tab, panel.IndexOf(tab) + 1), icon: "arrow-right");
        ContextMenu.Show(chrome, point, shown);
    }

    /* The menus, every item a command run by its name, so the keys and the palette say the same thing */

    private void BuildMenus()
    {
        Menu terminal = menu.AddMenu(HexText.Get("dusk_chrome.terminal"));
        Item(terminal, "New tab", "new_tab", "add");
        Item(terminal, HexText.Get("dusk_chrome.new_tab_with"), "new_tab_with", "list-selection");
        terminal.AddSeparator();
        Item(terminal, HexText.Get("dusk_chrome.split_right"), "split_right", "split-horizontal");
        Item(terminal, HexText.Get("dusk_chrome.split_down"), "split_down", "split-vertical");
        Item(terminal, HexText.Get("dusk_chrome.close_pane"), "close_pane", "close");
        terminal.AddSeparator();
        Item(terminal, HexText.Get("tabs.close"), "close_tab", "close");
        Item(terminal, HexText.Get("dusk_chrome.close_whole_tab"), "close_whole_tab", "close-all");
        Item(terminal, HexText.Get("dusk_chrome.rename_tab"), "rename_tab", "edit");
        terminal.AddSeparator();
        Item(terminal, HexText.Get("dusk_chrome.settings_file"), "open_settings", "settings-gear");
        Item(terminal, HexText.Get("dusk_chrome.keys_file"), "open_keys", "keyboard");
        terminal.AddSeparator();
        Item(terminal, HexText.Get("dusk_chrome.quit"), "quit", "sign-out");

        Menu view = menu.AddMenu(HexText.Get("runner.view"));
        Item(view, HexText.Get("dusk_chrome.command_palette"), "palette", "search");
        view.AddSeparator();
        Menu themes = view.AddMenu(HexText.Get("settings.designer.theme"), "color-mode");
        foreach (var (name, title) in Themes())
        {
            string picked = name;
            MenuItem item = themes.Add(title, () => UseTheme(picked));
            item.IsChecked = () => settings.Theme == picked;
        }

        Item(view, HexText.Get("dusk_chrome.next_theme"), "theme_next", "color-mode");
        MenuItem animations = Item(view, HexText.Get("dusk_chrome.animations"), "animations", "play");
        animations.IsChecked = () => settings.Animations;
        view.AddSeparator();
        Item(view, HexText.Get("dusk_chrome.zoom_in"), "zoom_in", "zoom-in");
        Item(view, HexText.Get("dusk_chrome.zoom_out"), "zoom_out", "zoom-out");
        Item(view, HexText.Get("dusk_chrome.zoom_reset"), "zoom_reset", "screen-normal");
        view.AddSeparator();
        Item(view, HexText.Get("dusk_chrome.focus_left"), "focus_left", "arrow-left");
        Item(view, HexText.Get("dusk_chrome.focus_right"), "focus_right", "arrow-right");
        Item(view, HexText.Get("dusk_chrome.focus_up"), "focus_up", "arrow-up");
        Item(view, HexText.Get("dusk_chrome.focus_down"), "focus_down", "arrow-down");

        Menu help = menu.AddMenu(HexText.Get("welcome.help"));
        Item(help, HexText.Get("dusk_chrome.keyboard_shortcuts"), "keys", "keyboard");
        Item(help, HexText.Get("dusk_chrome.plugins"), "plugins", "extensions");
        help.AddSeparator();
        help.Add(HexText.Get("dusk_chrome.about"), () => Notify(HexText.Get("dusk_chrome.about_text", ("version", VERSION))), icon: "info");

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
        messageItem = statusBar.Add(string.Empty, tooltip: HexText.Get("dusk_chrome.message_tip"));
        themeItem = statusBar.Add(string.Empty, right: true, pressed: () => Run("theme_next"), tooltip: HexText.Get("dusk_chrome.theme_tip"));
        sizeItem = statusBar.Add(string.Empty, right: true, tooltip: HexText.Get("dusk_chrome.size_tip"));
        profileItem = statusBar.Add(string.Empty, right: true, pressed: () => Run("new_tab_with"), tooltip: HexText.Get("dusk_chrome.profile_tip"));
    }

    private (int Columns, int Rows) sizeShown;
    private TerminalProfile? profileShown;
    private string? shellShown;
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
            sizeItem.Text = view is null ? string.Empty : HexText.Get("dusk_chrome.size", ("columns", size.Item1.ToString()), ("rows", size.Item2.ToString()));
        }

        // Named like the tab, the profile only gives it its icon
        TerminalProfile? profile = view?.Profile;
        string? title = panel.Current?.Title;
        if (title != titleShown)
            ShowTitle();

        if (profile != profileShown || title != shellShown)
        {
            profileShown = profile;
            shellShown = title;
            profileItem.Text = view is null || title is null ? string.Empty : $"$({profile?.Icon ?? "terminal"}) {title}";
        }

        if (themeShown != settings.Theme)
        {
            themeShown = settings.Theme;
            themeItem.Text = $"$(color-mode) {TitleOfTheme(settings.Theme)}";
        }
    }
}
