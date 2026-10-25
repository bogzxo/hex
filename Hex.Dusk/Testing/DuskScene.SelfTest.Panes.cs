using System.Numerics;

using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dusk;

// The self test of the rest of the plan's step five. Tabs put in another order, panes swapped by their badges and by
// their keys and renamed (and the name kept in the session), the settings as a window of rows, what the mouse and the
// bell do, and the drop down from the top of the screen. Nothing touches the clipboard, it is the user's.
internal sealed partial class DuskScene
{
    private void TestPanes(DuskScript test)
    {
        TabStrip Strip() => Descendants(panel).OfType<TabStrip>().First();

        // Tabs in another order, by the strip as a drag does it and by the keys' commands
        test.Run(() => Run("new_tab"));
        test.Settle(settle);
        TerminalTab? first = null, second = null;
        test.Run(() =>
        {
            first = panel.Tabs[0];
            second = panel.Tabs[1];
            Strip().Move(1, 0);
        });
        test.Check("a tab dragged along the strip moves", () => panel.Tabs[0] == second && panel.Tabs[1] == first && panel.Current == second);
        test.Run(() => Run("move_tab_right"));
        test.Check("move_tab_right takes the open one a step to the right", () => panel.Tabs[1] == second && panel.CurrentIndex == 1);
        test.Run(() => Run("move_tab_left"));
        test.Check("and move_tab_left back", () => panel.Tabs[0] == second);

        // Panes swapped
        test.Run(() => Run("split_right"));
        test.Settle(settle);
        TerminalView? left = null, right = null;
        test.Run(() =>
        {
            left = panel.Current!.Tree.Leaves.First().Content;
            right = panel.Current.Tree.Leaves.Last().Content;
        });
        test.Settle(0.1f);
        test.Run(() => Shot("panes-badges"));
        test.Check("with two panes each has its title in a badge", () => left!.ShowPaneTitle && right!.ShowPaneTitle && left.PaneBadge.Width > 0.0f);
        test.Run(() => left!.DropPane(right!.Bounds.Center));
        test.Check("a badge let go of over the other pane swaps the two", () => panel.Current!.Tree.Leaves.First().Content == right && panel.Current.Tree.Leaves.Last().Content == left);
        test.Check("and the keyboard stays with the one it was with", () => panel.FocusedView == right);
        test.Run(() => Run("swap_pane_right"));
        test.Check("swap_pane_right swaps the focused pane with the one on its right", () => panel.Current!.Tree.Leaves.Last().Content == right && panel.FocusedView == right);

        // Renamed, and kept
        test.Run(() => host.Run("rename_pane"));
        test.Check("rename_pane asks for the name", () => chrome.Dialog is not null);
        test.Run(() => chrome.Dialog?.Cancel());
        test.Run(() => right!.PaneName = "builds");
        test.Check("a pane's name is its title", () => right!.PaneTitle == "builds");
        test.Check("and the session keeps it", () => TerminalSessions.Write(panel).Contains("name: \"builds\""));
        test.Run(() => Run("close_tab"));
        test.Settle(settle);
        test.Check("one pane, no badge", () => panel.Current!.Tree.Count == 1 && !panel.FocusedView!.ShowPaneTitle);

        // The mouse and the bell as the settings say
        test.Run(() =>
        {
            host.Settings.CopyOnSelect = true;
            host.Settings.RightClickPaste = true;
            host.Settings.Bell = "none";
            host.ApplySettings();
        });
        test.Check("copy_on_select, right_click_paste and bell reach every terminal", () =>
            panel.FocusedView is { CopyOnSelect: true, RightClickPaste: true, Bell: TerminalBell.None });
        test.Run(() =>
        {
            host.Settings.CopyOnSelect = false;
            host.Settings.RightClickPaste = false;
            host.Settings.Bell = "flash";
            host.ApplySettings();
        });

        // The settings, Hex's one page of them over the window
        test.Run(() => Run("settings"));
        test.Settle(settle);
        test.Settle(0.2f);
        test.Run(() => Shot("settings"));
        test.Check("the settings come up as a page, a row a setting, the terminal's and the editor's", () =>
            SettingsPage is { } page && page.Settings.Count == TerminalSettings.Described.Length + Hex.Shell.EditorSettingsCatalog.Settings.Length
                + new Hex.Shell.WindowsSource().Settings.Count + new Hex.Shell.DefaultTerminalSource().Settings.Count
            && page.ValueOf!("theme") is StringValue && page.RowsShown == page.Settings.Count);
        test.Run(() => SettingsPage!.Find("scrollback"));
        test.Settle(0.1f);
        test.Check("the search leaves the rows that have what was typed", () => SettingsPage!.RowsShown is > 0 and < 4);
        test.Run(() => SettingsPage!.Find(string.Empty));
        test.Run(() => SettingsPage!.Enter("text_scale", new NumberValue(0.2f)));
        test.Check("a row changed is taken in and written", () =>
            Math.Abs(host.Settings.TextScale - 0.2f) < 0.001f && Math.Abs(panel.FocusedView!.TextScale - 0.2f) < 0.001f
            && SettingsPage!.IsModified!("text_scale")
            && (host.Settings.Path is null || File.ReadAllText(host.Settings.Path).Contains("text_scale: 0.2")));
        test.Run(() => SettingsPage!.Enter("plain_profiles", new ListValue([new StringValue("cmd"), new StringValue("pwsh")])));
        test.Check("a list is ticked as buttons and kept as a list", () => host.Settings.PlainProfiles.SetEquals(["cmd", "pwsh"]));
        test.Run(() => SettingsPage!.Enter("wrap_code", Values.Bool(true)));
        test.Check("the editor's section is written for Dawn to take in", () =>
            host.Settings.Path is null || SettingsSections.Section(SettingsSections.Read(host.Settings.Path)!, SettingsSections.EDITOR)["wrap_code"] is BooleanValue { Value: true });
        test.Run(() =>
        {
            SettingsPage!.Reset("text_scale");
            SettingsPage!.Reset("plain_profiles");
            SettingsPage!.Reset("wrap_code");
        });
        test.Check("and a reset puts it back to what it comes as", () =>
            Math.Abs(host.Settings.TextScale - 0.16f) < 0.001f && host.Settings.PlainProfiles.Count == 0 && !SettingsPage!.IsModified!("wrap_code"));
        test.Run(() => SettingsPage!.OnClose!());
        test.Settle(settle);
        test.Check("and its x puts it away", () => SettingsPage is null);

        // The drop down, and a key for everywhere: Windows' own, the desktop's elsewhere, bound to hex --dusk --toggle
        if (OperatingSystem.IsWindows())
        {
            test.Check("a key for everywhere can be had", () =>
            {
                int id = Engine.WindowManager.RegisterHotKey("Ctrl+Alt+Shift+F11");
                bool had = id > 0 && !Engine.WindowManager.TakeHotKey(id);
                Engine.WindowManager.UnregisterHotKey(id);
                return had;
            });
        }
        else
        {
            test.Check("there's no key for everywhere of Hex's own here, the desktop's is", () => Engine.WindowManager.RegisterHotKey("Ctrl+Alt+Shift+F11") < 0);
            test.Run(() => TakeInstanceLine("toggle"));
            test.WaitFor(() => Engine.WindowManager.IsDropped, 2.0f);
            test.Check("hex --dusk --toggle drops it down", () => Engine.WindowManager.IsDropped);
            test.Run(() => TakeInstanceLine("toggle"));
            test.WaitFor(() => !Engine.WindowManager.IsDropped, 2.0f);
            test.Check("and takes it back up", () => !Engine.WindowManager.IsDropped);
        }
        test.Run(() => Run("quake"));
        test.WaitFor(() => Engine.WindowManager.IsDropped, 2.0f);
        test.Settle(0.4f);
        test.Run(() => Shot("quake"));
        test.Check("the quake command drops the window down from the top", () => Engine.WindowManager.IsDropped);
        test.Run(() => Run("quake"));
        test.WaitFor(() => !Engine.WindowManager.IsDropped, 2.0f);
        test.Check("and takes it back up", () => !Engine.WindowManager.IsDropped);
        test.Settle(0.4f);
        test.Run(() => Engine.WindowManager.Drop(true, 0.6f, 0.0f));
        test.Settle(0.3f);
    }
}
