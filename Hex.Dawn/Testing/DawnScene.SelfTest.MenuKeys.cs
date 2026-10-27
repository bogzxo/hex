using Horizon.UI.Components;

using Key = Silk.NET.Input.Key;

namespace Hex.Dawn;

// The self test of the menu bar with the keys, every key pressed for real. Alt and a letter opens its menu, Left and
// Right go along the bar, Down along a menu, Right into a menu off an item and Left out of it, Enter picks, Escape shuts,
// Alt alone lights the bar, and Alt with a letter a command has, or with an arrow, leaves the bar alone. The code keeps
// the keyboard all the while.
internal sealed partial class DawnScene
{
    private void TestMenuKeys(HexPointerScript test)
    {
        OnCode(test, dataFile);
        test.Run(() => code.Focus());

        // The first menu and the last one on the bar, for going round it
        int Last() => Enumerable.Range(0, menu.Menus.Count).Last(index => menu.Menus[index].IsVisible?.Invoke() ?? true);

        PressKeys(test, "Alt+F");
        test.Check("menu keys: Alt+F opens File with its first item lit, the code keeping the keyboard", () =>
            menu.OpenMenu?.Title == "File" && menu.KeyedItem is { } item && item == menu.OpenMenu.Items.First(each => each.Listed && !each.IsSeparator) && compositor.Focus == code);
        MenuItem? lit = null;
        test.Run(() => lit = menu.KeyedItem);
        PressKeys(test, "Down");
        test.Check("menu keys: Down lights the next item", () => menu.KeyedItem is { } now && now != lit && menu.OpenMenu?.Title == "File");
        PressKeys(test, "Left");
        test.Check("menu keys: Left from File goes round to the last menu of the bar", () => menu.OpenMenu == menu.Menus[Last()] && menu.KeyedItem is not null);
        PressKeys(test, "Right");
        test.Check("menu keys: and Right comes back to File", () => menu.OpenMenu?.Title == "File");
        PressKeys(test, "Right");
        test.Check("menu keys: Right again is Edit", () => menu.OpenMenu?.Title == "Edit");
        PressKeys(test, "Escape");
        test.Check("menu keys: Escape shuts it and the code still has the keyboard", () => menu.OpenMenu is null && compositor.Focus == code);

        // Into a menu off an item and out of it again, the first of Git's
        PressKeys(test, "Alt+G");
        test.Check("menu keys: Alt+G opens Go, the first menu with a G", () => menu.OpenMenu?.Title == "Go");
        PressKeys(test, "Alt+G");
        test.Check("menu keys: and again Git, the next one with it", () => menu.OpenMenu?.Title == "Git");
        for (int i = 0; i < 16; i++)
        {
            test.Run(() =>
            {
                if (menu.OpenMenu is not null && menu.KeyedItem is { Submenu: null })
                    Engine.Input.Keyboard.Tap([Key.Down]);
            });
            test.Wait();
        }
        test.Check("menu keys: Down gets to an item that opens a menu of its own", () => menu.KeyedItem is { Submenu: not null });
        PressKeys(test, "Right");
        test.Check("menu keys: Right opens it with its first item lit", () => menu.OpenSubmenu is { } inner && menu.KeyedItem is { } item && inner.Items.Contains(item));
        PressKeys(test, "Left");
        test.Check("menu keys: Left shuts it and Git stays open", () => menu.OpenSubmenu is null && menu.OpenMenu?.Title == "Git" && menu.KeyedItem is { Submenu: not null });
        PressKeys(test, "Escape");
        test.Check("menu keys: Escape shuts Git", () => menu.OpenMenu is null);

        // Enter picks, the palette is View's first
        PressKeys(test, "Alt+V");
        test.Check("menu keys: Alt+V opens View on the Command Palette", () => menu.OpenMenu?.Title == "View" && menu.KeyedItem?.Label == Said("menus.command_palette"));
        PressKeys(test, "Enter");
        test.Check("menu keys: Enter picks it, the menu goes and the palette comes up", () => menu.OpenMenu is null && textDialog is { IsOpen: true });
        PressKeys(test, "Escape");
        test.Check("menu keys: Escape out of the palette, the code has the keyboard", () => textDialog is not { IsOpen: true } && compositor.Focus == code);

        // Alt alone lights the bar, Right goes along it, Down opens the lit one
        PressKeys(test, "Alt");
        test.Check("menu keys: Alt alone lights the bar with no menu open", () => menu.Armed && menu.OpenMenu is null);
        PressKeys(test, "Right Down");
        test.Check("menu keys: Right goes along it and Down opens the menu lit", () => menu.OpenMenu?.Title == "Edit" && menu.KeyedItem is not null);
        PressKeys(test, "Escape");
        PressKeys(test, "Alt Alt");
        test.Check("menu keys: Alt again puts the bar out", () => !menu.Armed && menu.OpenMenu is null && compositor.Focus == code);

        // A command on Alt and a letter has it (Alt+R is the find bar's regex), an arrow with Alt goes back
        PressKeys(test, "Alt+R");
        test.Check("menu keys: Alt+R is a command's, Run doesn't open", () => menu.OpenMenu is null && !menu.Armed);
        PressKeys(test, "Alt+Left");
        test.Check("menu keys: Alt+Left lights nothing on the bar", () => menu.OpenMenu is null && !menu.Armed);

        // A right click's menu goes with the keys too
        OnCode(test, dataFile);
        test.Run(() => code.Focus());
        test.RightClick(At(() => code));
        test.Check("menu keys: a right click on the code opens its menu", () => OpenContextMenu?.Showing is not null);
        PressKeys(test, "Down");
        test.Check("menu keys: Down lights its first item", () => OpenContextMenu?.KeyedItem is not null && compositor.Focus == code);
        PressKeys(test, "Escape");
        test.Check("menu keys: and Escape shuts it with the code still having the keyboard", () => OpenContextMenu?.Showing is null && compositor.Focus == code);
    }
}
