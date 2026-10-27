using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The menus kept short enough to read and inside the window. No menu or menu inside one lists more than twenty rows or
// six rules, no two rules in a row, every list a menu opens (and the ones open off it) inside the window, and a list too
// tall for the window scrolls, with the wheel and with its last item still reachable. Run it in a small window to mean
// something, --size 1280x720, and with HEX_SELFTEST_SHOTS set for a picture of every menu open
internal sealed partial class DawnScene
{
    private const int MOST_ROWS = 20, MOST_RULES = 6;

    private void TestMenus(HexPointerScript test)
    {
        // What every menu lists, in a layout's designer (Layout is there) and in code (Selection is)
        OnLayoutForSettings(test);
        CheckMenuSizes(test, "designer");
        OpenEveryMenu(test, "designer");
        OnCode(test, dataFile);
        CheckMenuSizes(test, "code");
        OpenEveryMenu(test, "code");

        TestMenuThroughPointer(test);
        TestTallMenu(test);
        TestMenuKeys(test);
    }

    // Every menu of the bar there is right now, and every menu inside one, held to the rows and rules a menu may have
    private void CheckMenuSizes(HexPointerScript test, string where)
    {
        test.Check($"menus ({where}): no menu lists more than {MOST_ROWS} rows or {MOST_RULES} rules, or two rules in a row", () =>
        {
            var wrong = new List<string>();
            foreach (Menu bar in menu.Menus.Where(bar => bar.IsVisible?.Invoke() ?? true))
                MenuSizeFaults(bar, bar.Title, wrong);

            foreach (string fault in wrong)
                Console.WriteLine($"[Hex self-test]   {fault}");

            return wrong.Count == 0;
        });
    }

    private static void MenuSizeFaults(Menu checking, string path, List<string> wrong)
    {
        int rows = 0, rules = 0;
        bool lastRule = true;
        for (int i = 0; i < checking.Items.Count; i++)
        {
            if (!checking.IsShown(i))
                continue;

            MenuItem item = checking.Items[i];
            if (item.IsSeparator)
            {
                if (lastRule)
                    wrong.Add($"{path} has a rule after a rule or at its top");
                rules++;
            }
            else
            {
                rows++;
                if (item.Submenu is { } inside)
                    MenuSizeFaults(inside, $"{path} > {item.Label}", wrong);
            }

            lastRule = item.IsSeparator;
        }

        if (rows > MOST_ROWS)
            wrong.Add($"{path} lists {rows} rows");
        if (rules > MOST_RULES)
            wrong.Add($"{path} has {rules} rules");
        if (rows > 0 && lastRule)
            wrong.Add($"{path} ends on a rule");
    }

    // Every list of the bar's menus inside the window, and the lists of every menu inside each of them
    private bool BarListsInside() => menu.Module is { } module && menu.OpenListBounds.All(list => Inside(list, module.Frame));

    private static bool Inside(UIRect list, UIRect frame) =>
        !list.IsEmpty && list.Min.X >= frame.Min.X - 0.5f && list.Min.Y >= frame.Min.Y - 0.5f && list.Max.X <= frame.Max.X + 0.5f && list.Max.Y <= frame.Max.Y + 0.5f;

    // Every menu of the bar opened in turn, and every menu inside it, each list checked inside the window and a picture
    // taken of it when pictures are asked for
    private void OpenEveryMenu(HexPointerScript test, string where)
    {
        for (int index = 0; index < menu.Menus.Count; index++)
        {
            Menu bar = menu.Menus[index];
            int at = index;
            test.Run(() => { if (bar.IsVisible?.Invoke() ?? true) menu.Open(at); });
            test.Wait();
            test.Check($"menus ({where}): {bar.Title} opens inside the window", () => !(bar.IsVisible?.Invoke() ?? true) || BarListsInside());
            MenuShot(test, $"{where}_{bar.Title}", () => bar.IsVisible?.Invoke() ?? true);

            foreach (MenuItem row in bar.Items.Where(item => item.Submenu is not null))
            {
                string label = row.Label;
                test.Run(() => { if (menu.OpenMenu == bar && row.Listed) menu.OpenSubmenuOf(label); });
                test.Wait();
                test.Check($"menus ({where}): {bar.Title} > {label} opens inside the window, beside its item", () =>
                    menu.OpenMenu != bar || !row.Listed || (BarListsInside() && menu.OpenListBounds.Count() == 2));
                MenuShot(test, $"{where}_{bar.Title}_{label}", () => menu.OpenMenu == bar && row.Listed);
            }

            test.Run(menu.Close);
            test.Wait();
        }
    }

    // A picture of the window once what just opened has finished coming in, for a menu that is there at all
    private void MenuShot(HexPointerScript test, string name, Func<bool>? there = null)
    {
        if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is not { Length: > 0 } shots)
            return;

        string file = Path.Combine(shots, $"menu_{string.Concat(name.Select(each => char.IsLetterOrDigit(each) ? each : '_'))}.png");
        test.Settle(0.3f);
        // A frame asked for, Hex only draws on a change and the picture would be of the next one
        test.Run(() =>
        {
            if (!(there?.Invoke() ?? true))
                return;

            Engine.CaptureScreenshot(file);
            Engine.WindowManager.RequestFrames(0.2);
            Engine.WindowManager.Wake();
        });
        test.Settle(0.2f);
    }

    // The Git menu by the pointer, its word clicked and Branches rested on, and an item of Branches clicked
    private void TestMenuThroughPointer(HexPointerScript test)
    {
        test.Click(() => chrome.ToWorld(menu.TitleBounds("Git").Center));
        test.Check("menus: Git is short, its rarer commands in menus of their own", () =>
            menu.OpenMenu is { Title: "Git" } git && git.Items.Count(item => item.Submenu is not null) >= 6 && !git.Items.Any(item => item.Label == Said("git.fetch")));
        test.Hover(() => chrome.ToWorld(menu.ItemBounds(Said("menus.git_remote")).Center));
        test.Settle(0.4f);
        test.Check("menus: resting on Remote opens it beside the Git menu, inside the window", () =>
            menu.OpenSubmenu?.Title == Said("menus.git_remote") && BarListsInside() && menu.OpenListBounds.Count() == 2
            && menu.OpenListBounds.Last() is var side && (side.Min.X >= menu.ListBounds.Max.X - 12.0f || side.Max.X <= menu.ListBounds.Min.X + 12.0f));
        MenuShot(test, "pointer_git_remote");
        test.Run(menu.Close);
        test.Wait();
    }

    // A menu much taller than the window, the way the skins were, scrolls rather than running off the bottom
    private void TestTallMenu(HexPointerScript test)
    {
        const int ROWS = 60;
        int picked = -1;
        var tall = new Menu("selftest tall");
        for (int i = 0; i < ROWS; i++)
        {
            int row = i;
            tall.Add($"Row {row + 1}", () => picked = row);
            if (i % 10 == 9 && i < ROWS - 1)
                tall.AddSeparator();
        }

        // Low down at the right, where it can't go down or right and has to go up and scroll
        test.Run(() => contextMenu = ContextMenu.ShowAt(chrome, chrome.ToWorld(new Vector2(chrome.Frame.Max.X - 20.0f, chrome.Frame.Min.Y + 40.0f)), tall));
        test.Wait();
        test.Check("menus: a menu taller than the window scrolls and stays inside it", () =>
            contextMenu is { ListScrolls: true } shown && shown.OpenListBounds.All(list => Inside(list, chrome.Frame)));
        MenuShot(test, "tall_top");

        float firstAt = 0.0f;
        test.Run(() => firstAt = contextMenu!.ItemBounds(0).Max.Y);
        test.Hover(() => chrome.ToWorld(contextMenu!.OpenListBounds.First().Center));
        test.Run(() => compositor.Scroll(-3));
        test.Wait();
        test.Check("menus: the wheel over it scrolls it", () => contextMenu!.ItemBounds(0).Max.Y > firstAt + 1.0f);

        // The last row found by what it says is scrolled into view and can be clicked
        test.Click(() => chrome.ToWorld(contextMenu!.ItemBounds($"Row {ROWS}").Center));
        test.Wait();
        test.Check("menus: its last row is reachable, scrolled to and picked", () => picked == ROWS - 1 && contextMenu?.Showing is null);
        MenuShot(test, "tall_after");
    }
}
