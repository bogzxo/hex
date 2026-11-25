using System.Collections.Concurrent;

using Horizon.Platform;
using Horizon.UI.Components;

namespace Hex.Dawn;

// On a Mac the menus are in the menu bar along the top of the screen, as in every Mac program, and not in the window's title
// bar. The bar is the system's (the engine's IPlatformMenuBar, which only a Mac's desktop host has) and a mirror of the
// menus the commands put together: it shows their items, their keys and their state as a menu opens, and a click with the
// mouse is queued here to be done on the editor's own thread. The keys stay Hex's own (UICommands.Keys), the menu only
// shows them and never answers one, so chords, the user's own keys and keys that mean something else while typing go on
// working. The application's menu (About, Settings, Services, Hide, Quit) and the Window menu are the Mac's own.
internal sealed partial class DawnScene
{
    // The system's bar, null on everything but a Mac and while the editor is under test
    private IPlatformMenuBar? nativeMenuBar;

    // What was picked in the system's menu bar, done on the next tick of the editor's thread
    private readonly ConcurrentQueue<Action> menuPicks = new();

    // The menus given to the bar, to see which of them are on it
    private IReadOnlyList<PlatformMenu> nativeMenus = [];

    // Which of the menus were on the bar as it was last told, a bit for each
    private ulong nativeMenusShown;

    /// <summary>Whether the menus are in the system's menu bar, and so left out of the window.</summary>
    private bool MacMenuIsNative => nativeMenuBar is not null && settings.MacNativeMenu;

    /// <summary>Puts the menus in the system's menu bar. Once the editor's menus are made.</summary>
    private void BuildMacMenu()
    {
        if (!OperatingSystem.IsMacOS() || options.SelfTest)
            return;

        IPlatformMenuBar? bar = Engine.WindowManager.Host.MenuBar;
        if (bar is null)
            return;

        nativeMenuBar = bar;
        bar.RunPicked = pick =>
        {
            menuPicks.Enqueue(pick);
            Engine.WindowManager.Wake();
        };

        ApplyMacMenu();
    }

    /// <summary>The setting that puts the menus in the system's bar was changed, they move out of the window and back.</summary>
    internal void MacMenuSettingChanged()
    {
        if (nativeMenuBar is not null)
            ApplyMacMenu();
    }

    private void ApplyMacMenu()
    {
        if (nativeMenuBar is not { } bar)
            return;

        // Off, the bar is only the application's menu and the Window menu, the window has its own menus
        bool native = settings.MacNativeMenu;
        menu.Visible = !native;

        nativeMenus = MacMenus(native);
        nativeMenusShown = ~0ul;
        bar.SetMenus(nativeMenus);
        UpdateMacMenu();
    }

    /// <summary>The menus of the system's bar: the application's, then Hex's own, with the Window menu before Help.</summary>
    private IReadOnlyList<PlatformMenu> MacMenus(bool withMenus)
    {
        var menus = new List<PlatformMenu> { MacApplicationMenu() };

        PlatformMenu window = MacWindowMenu();
        if (withMenus)
        {
            // Help is the last, the Window menu goes before it
            IReadOnlyList<PlatformMenu> own = UIMenuBridge.FromBar(menu, title => title == "Help" ? PlatformMenuRole.HelpSearch : PlatformMenuRole.None);
            int help = -1;
            for (int i = 0; i < own.Count; i++)
            {
                if (own[i].Role == PlatformMenuRole.HelpSearch)
                    help = i;
            }

            for (int i = 0; i < own.Count; i++)
            {
                if (i == help)
                    menus.Add(window);
                menus.Add(own[i]);
            }

            if (help < 0)
                menus.Add(window);
        }
        else
        {
            menus.Add(window);
        }

        return menus;
    }

    /// <summary>
    /// Whether File, Exit is in the menus. A Mac with its menus in the system's bar has Hex, Quit Hex (Cmd+Q) for that and no
    /// Alt+F4, anywhere else Exit is File's last item.
    /// </summary>
    internal static bool ExitShown(bool mac, bool nativeMenu) => !(mac && nativeMenu);

    /// <summary>The key the menu says Redo has: Shift+Cmd+Z on a Mac, where Cmd+Y goes on working without being said, Ctrl+Y elsewhere.</summary>
    internal static string RedoShortcut(bool mac) => mac ? "Ctrl+Shift+Z" : "Ctrl+Y";

    // About, Settings, Check for Updates, Services, Hide and Quit, in the order every Mac program has them. The ones with a role
    // are the system's own with its own keys, the others do what Hex does and their keys are answered by Hex's own
    private PlatformMenu MacApplicationMenu()
    {
        List<PlatformMenuItem> items =
        [
            new() { Label = Said("mac_menu.about"), Role = PlatformMenuRole.About, Picked = ShowAbout },
            PlatformMenuItem.Separator(),
            new() { Label = Said("mac_menu.settings"), Role = PlatformMenuRole.Settings, Shortcut = "Ctrl+,", Picked = OpenSettings },
            new() { Label = Hex.Installation.InstallText.Get("updates.command"), Picked = CheckForHexUpdates },
            new() { Label = Said("mac_menu.install_cli"), Picked = InstallHexCommand },
            PlatformMenuItem.Separator(),
            new() { Label = Said("mac_menu.services"), Role = PlatformMenuRole.Services },
            PlatformMenuItem.Separator(),
            new() { Label = Said("mac_menu.hide"), Role = PlatformMenuRole.Hide },
            new() { Label = Said("mac_menu.hide_others"), Role = PlatformMenuRole.HideOthers },
            new() { Label = Said("mac_menu.show_all"), Role = PlatformMenuRole.ShowAll },
            PlatformMenuItem.Separator(),
            new() { Label = Said("mac_menu.quit"), Role = PlatformMenuRole.Quit },
        ];

        return new PlatformMenu("Hex", items);
    }

    // The system lists the windows in it
    private PlatformMenu MacWindowMenu() => new(Said("mac_menu.window"),
    [
        new() { Label = Said("mac_menu.minimize"), Role = PlatformMenuRole.Minimize },
        new() { Label = Said("mac_menu.zoom"), Role = PlatformMenuRole.Zoom },
        PlatformMenuItem.Separator(),
        new() { Label = Said("mac_menu.full_screen"), Role = PlatformMenuRole.FullScreen },
        PlatformMenuItem.Separator(),
        new() { Label = Said("mac_menu.bring_all"), Role = PlatformMenuRole.BringAllToFront },
    ], PlatformMenuRole.WindowList);

    /// <summary>Every tick: what was picked in the system's menu bar is done, and a menu that came or went (the Layout menu) is told to the bar.</summary>
    private void UpdateMacMenu()
    {
        UpdateMacDock();

        // The scene is made before the window's host starts, which is what makes the system's bar, so the bar is taken on the
        // first tick that has it (a property read a tick until then, nothing after)
        if (nativeMenuBar is null && OperatingSystem.IsMacOS() && !options.SelfTest && Engine.WindowManager.Host.MenuBar is not null)
            BuildMacMenu();

        if (nativeMenuBar is not { } bar)
            return;

        while (menuPicks.TryDequeue(out Action? pick))
        {
            try
            {
                pick();
            }
            catch (Exception e)
            {
                Say(e.Message, error: true);
            }
        }

        // Which menus are on the bar can change without one opening (the Layout menu is the designer's), so it is looked at
        // as it is every tick, which is a few comparisons, and the system told when it changed
        ulong shown = 0;
        for (int i = 0; i < nativeMenus.Count && i < 64; i++)
        {
            if (nativeMenus[i].IsVisible())
                shown |= 1ul << i;
        }

        if (shown != nativeMenusShown)
        {
            nativeMenusShown = shown;
            bar.Invalidate();
        }
    }
}
