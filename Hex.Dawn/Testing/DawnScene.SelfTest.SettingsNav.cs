using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Input;
using Horizon.UI;
using Horizon.UI.Components;

using Hex.Shell;

using Key = Silk.NET.Input.Key;

namespace Hex.Dawn;

// The self test of the sections down the left of the settings as a tree. The parts bold with a rule over them, folded
// and opened by a click and by Left and Right, a search opening them again, the count of changes beside one and its
// reset. Then the keys that get about, Ctrl+F and / to the search from a row, Ctrl+PageDown a section on and Shift+Tab
// from the first row back to the sections, and where the terminal's sections and the Windows rows are
internal sealed partial class DawnScene
{
    private void TestSettingsNav(HexPointerScript test)
    {
        SettingsPage page = null!;

        // A part's heading, the headings showing go in the order of the parts showing
        UIComponent? Heading(string part) => Descendants(page).Where(component => component.GetType().Name == "PartHeading" && component.Visible).ElementAtOrDefault(page.PartsShown.ToList().IndexOf(part));
        bool TerminalLinks() => page.SectionsShown.Any(group => group.StartsWith("Terminal · ", StringComparison.Ordinal));
        string? Group(string key) => page.Settings.FirstOrDefault(setting => setting.Key == key)?.Group;

        test.Run(() =>
        {
            OpenSettings();
            page = SettingsPage!;
            page.Find(string.Empty);
            foreach (string part in EditorSettingsCatalog.Parts)
                page.SetPartCollapsed(part, false);
        });

        // The page is built on its first update, its sections down the left after that
        test.WaitFor(() => page.SectionsShown.Count > 0, 2.0f);
        test.Run(() => page.GoTo(page.SectionsShown[0]));
        test.Settle(0.3f);

        /* The tree */

        test.Run(() =>
        {
            page.Enter("wrap_code", Values.Bool(true));
            page.Enter("tab_size", new NumberValue(2.0f));
        });
        test.Wait();
        test.Run(() => Shot("settings-nav-tree"));
        test.Check("settings nav: the parts are headings, bold, bigger than the links under them", () =>
            Heading("Editor") is not null && Heading("Terminal") is not null && page.NavScales is { Part: > 0.0f } scales && scales.Part > scales.Section);
        test.Check("settings nav: a part with changes shows how many beside it", () =>
            page.PartCount("Editor") == page.Settings.Count(setting => SettingsPage.PartOf(setting.Group) == "Editor" && page.IsModified!(setting.Key)) && page.PartCount("Editor") >= 2);

        // A right click on a part offers to reset every change in it
        test.RightClick(At(() => Heading("Editor"), 0.5f, 0.3f));
        test.Wait();
        test.Check("settings nav: a right click on a part offers to reset its changes", () =>
            chrome.Root.Children.OfType<ContextMenu>().FirstOrDefault()?.Showing is { } menu
            && menu.Items.Any(item => item.Label == $"Reset {page.Settings.Count(setting => SettingsPage.PartOf(setting.Group) == "Editor" && page.IsModified!(setting.Key))} changes in Editor"));
        test.Run(() =>
        {
            chrome.Root.Children.OfType<ContextMenu>().FirstOrDefault()?.Close();
            page.Reset("wrap_code");
            page.Reset("tab_size");
        });
        test.Wait();

        // A click on Terminal folds its links away, the rows stay
        test.Click(At(() => Heading("Terminal"), 0.5f, 0.3f));
        test.Wait();
        test.Run(() => Shot("settings-nav-folded"));
        test.Check("settings nav: a click on a part's heading folds its links away and leaves its rows", () =>
            SettingsPage.IsPartCollapsed("Terminal") && !TerminalLinks() && page.IsRowShown("scrollback")
            && page.SectionsShown.Contains(EditorSettingsCatalog.TEXT));
        test.Click(At(() => Heading("Terminal"), 0.5f, 0.3f));
        test.Wait();
        test.Check("settings nav: and a click again brings them back", () => !SettingsPage.IsPartCollapsed("Terminal") && TerminalLinks());

        // A search opens a folded part it found something in
        test.Run(() =>
        {
            page.SetPartCollapsed("Terminal", true);
            page.Find("scrollback");
        });
        test.Wait();
        test.Check("settings nav: a search that finds something in a folded part opens it", () =>
            page.SectionsShown.Contains("Terminal · Scrollback") && !SettingsPage.IsPartCollapsed("Terminal"));
        test.Run(() => page.Find(string.Empty));
        test.Wait();

        // Left and Right on a heading and on a link of its part
        test.Run(() =>
        {
            page.FocusPart("Terminal");
            page.Press(UIKeyboard.LEFT);
        });
        test.Check("settings nav: Left on a heading folds its part", () => page.FocusedPart == "Terminal" && SettingsPage.IsPartCollapsed("Terminal") && !TerminalLinks());
        test.Run(() => page.Press(UIKeyboard.RIGHT));
        test.Check("settings nav: and Right opens it", () => !SettingsPage.IsPartCollapsed("Terminal") && TerminalLinks());
        test.Run(() => page.Press(UIKeyboard.ENTER));
        test.Check("settings nav: Enter folds it too", () => SettingsPage.IsPartCollapsed("Terminal"));
        test.Run(() => page.Press(' '));
        test.Check("settings nav: and Space opens it", () => !SettingsPage.IsPartCollapsed("Terminal"));
        test.Run(() => page.Press(UIKeyboard.UP));
        test.Check("settings nav: Up from a heading goes to the last link of the part over it", () =>
            page.FocusedSection is { } section && section.StartsWith("Editor · ", StringComparison.Ordinal)
            && section == page.SectionsShown.Last(group => group.StartsWith("Editor · ", StringComparison.Ordinal)));
        test.Run(() => page.Press(UIKeyboard.DOWN));
        test.Check("settings nav: and Down back onto the heading", () => page.FocusedPart == "Terminal");
        test.Run(() => page.Press(UIKeyboard.DOWN));
        test.Check("settings nav: and on down to its first link", () => page.FocusedSection is { } section && section.StartsWith("Terminal · ", StringComparison.Ordinal));
        test.Run(() => page.Press(UIKeyboard.LEFT));
        test.Check("settings nav: Left on a link folds its part and goes up to the heading", () =>
            SettingsPage.IsPartCollapsed("Terminal") && page.FocusedPart == "Terminal");
        test.Run(() => page.SetPartCollapsed("Terminal", false));

        /* The keys */

        // A row with a toggle or a choice, a box being typed in keeps / for itself
        void OnRowNotTyped()
        {
            page.GoTo(page.SectionsShown[0]);
            page.FocusFirstRow();
            for (int i = 0; i < 8 && page.Settings.FirstOrDefault(setting => setting.Key == page.FocusedKey) is { Kind: not (UIFormKind.Flag or UIFormKind.Choice) }; i++)
                page.MoveRow(1);
        }

        test.Run(OnRowNotTyped);
        test.Wait();
        test.Run(() => page.Press('/'));
        test.Check("settings nav: / from a row puts the keyboard in the search", () => page.Search.IsFocused && page.FocusedKey is null);

        // Ctrl+F pressed for real on the keyboard, through the input the way a person's keys go
        test.Run(OnRowNotTyped);
        test.Wait();
        test.Check("settings nav: (a row has the keyboard before Ctrl+F)", () => page.FocusedKey is not null);
        test.Run(() => GameEngine.Instance.Input.Play(new InputScript().Chord(0.05f, Key.ControlLeft, Key.F)));
        test.Settle(0.5f);
        test.Check("settings nav: Ctrl+F from a row puts the keyboard in the search", () => page.Search.IsFocused && page.FocusedKey is null);

        // Ctrl held for real and Page Down given to the row, a section on
        string? from = null;
        string? After(string? group) => page.SectionsShown.ToList().IndexOf(group!) is var at and >= 0 && at + 1 < page.SectionsShown.Count ? page.SectionsShown[at + 1] : null;
        test.Run(() =>
        {
            page.GoTo(page.SectionsShown[0]);
            page.FocusFirstRow();
            from = Group(page.FocusedKey!);
            GameEngine.Instance.Input.Play(new InputScript().HoldKey(0.0f, Key.ControlLeft, 0.6f));
        });
        test.Settle(0.15f);
        test.Run(() => page.Press(UIKeyboard.PAGE_DOWN));
        test.Settle(0.2f);
        test.Check("settings nav: Ctrl+PageDown from a row goes to the next section's first row", () =>
            After(from) is { } next && page.CurrentGroup == next && Group(page.FocusedKey ?? string.Empty) == next);
        test.Settle(0.5f);

        // Shift+Tab from the first row back to the link of the section it's in
        test.Run(() => page.GoTo(page.SectionsShown[0]));
        test.Settle(0.2f);
        test.Run(() =>
        {
            page.FocusFirstRow();
            compositor.FocusNext(-1);
        });
        test.Check("settings nav: Shift+Tab from the first row lands on the section's link down the left", () =>
            page.FocusedSection is { } section && section == page.SectionsShown[0] && section == page.CurrentGroup);

        /* Where things are */

        test.Check("settings nav: the terminal's look is Terminal · Display and its colours Terminal · Highlighting", () =>
            Group("text_scale") == "Terminal · Display" && Group("highlight_output") == "Terminal · Highlighting"
            && !page.Settings.Any(setting => setting.Group is "Terminal · Appearance" or "Terminal · Colours"));
        test.Check("settings nav: the terminal's font and shell are dropdowns of the fonts and the profiles there are", () =>
            page.Settings.FirstOrDefault(setting => setting.Key == "font") is { Kind: UIFormKind.Choice, Choices: { } fonts } && fonts.Contains("hack")
            && page.Settings.FirstOrDefault(setting => setting.Key == "profile") is { Kind: UIFormKind.Choice, Choices: { Length: >= 2 } });
        test.Check("settings nav: the Windows rows are Editor · Windows, not Editor · Files", () =>
            new WindowsSource().Settings.Count == 0
            || Group("explorer_menu") == WindowsSource.GROUP && Group("user_path") == WindowsSource.GROUP
            && !page.Settings.Any(setting => setting.Group == EditorSettingsCatalog.FILES && setting.Key is "explorer_menu" or "user_path"));

        TestSettingsCommon(test);
        TestSettingsRemembered(test);
    }

    // Commonly used at the top of the sections down the left, a click or Enter on it leaving only those rows in their
    // own order through @common in the search, a chip there that one Backspace takes out
    private void TestSettingsCommon(HexPointerScript test)
    {
        SettingsPage page = null!;
        string[] common = [];
        test.Run(() =>
        {
            page = SettingsPage!;
            compositor.Focus = null;
            page.Find(string.Empty);
            common =[.. page.Common.Where(key => page.Settings.Any(setting => setting.Key == key))];
        });
        test.Wait();
        test.Run(() => Shot("settings-common-nav"));
        test.Check("settings common: Commonly used is the first entry down the left", () =>
            page.CommonFirst && page.CommonText == Said("settings_page.commonly_used") && common.Length >= 10 && common.Contains("font") && common.Contains("tab_size"));

        test.Click(In(() => page.CommonBounds, 0.4f, 0.5f));
        test.Wait();
        test.Run(() => Shot("settings-common-rows"));
        test.Check("settings common: a click leaves only the commonly used rows, in their own order", () =>
            page.ShowingCommon && page.KeysShown.SequenceEqual(common) && page.SectionsShown.Count > 0);
        test.Check("settings common: the search has @common in it as a chip", () =>
            page.Search.Text.Trim() == SettingsPage.COMMON && page.SearchChips.Any(chip => chip.Word == SettingsPage.COMMON && !chip.Bounds.IsEmpty));

        // One Backspace after it takes the whole chip out, the rows come back
        test.Run(() =>
        {
            page.Search.Focus();
            page.Press(UIKeyboard.BACKSPACE);
        });
        test.Wait();
        test.Check("settings common: Backspace after the chip takes all of it out", () =>
            page.Search.Text.Length == 0 && !page.ShowingCommon && page.KeysShown.Count > common.Length && page.SearchChips.Count == 0);

        // The keyboard gets to it like any section, Home from a link, and Enter on it goes into the first of its rows
        test.Run(() =>
        {
            page.FocusNav();
            page.Press(UIKeyboard.HOME);
        });
        test.Check("settings common: Home down the left goes up to Commonly used", () => page.CommonFocused);
        test.Run(() => page.Press(UIKeyboard.DOWN));
        test.Check("settings common: and Down goes on to the first part's heading", () => page.FocusedPart == page.PartsShown.FirstOrDefault());
        test.Run(() =>
        {
            page.Press(UIKeyboard.UP);
            page.Press(UIKeyboard.ENTER);
        });
        test.Wait();
        test.Check("settings common: Enter on it shows the commonly used and goes into the first of them", () =>
            page.ShowingCommon && page.KeysShown.SequenceEqual(common) && page.FocusedKey == common[0]);
        test.Run(() =>
        {
            compositor.Focus = null;
            page.Find(string.Empty);
        });
        test.Wait();
    }

    // The window opens where it was left this run, its section, its search and its scroll, and a section or a setting
    // asked for still wins over that
    private void TestSettingsRemembered(HexPointerScript test)
    {
        SettingsPage page = null!;
        string? group = null;
        test.Run(() =>
        {
            page = SettingsPage!;
            reopenWhereLeftInTest = true;
            page.GoTo(EditorSettingsCatalog.GIT);
        });
        test.Settle(0.3f);
        test.Run(() => page.Find("blame"));
        test.Settle(0.3f);
        test.Run(() =>
        {
            group = page.CurrentGroup;
            compositor.Focus = null;
            CloseSettings();
        });
        test.Wait();

        // Ctrl+, for real on the keyboard
        test.Run(() => GameEngine.Instance.Input.Play(new InputScript().Chord(0.05f, Key.ControlLeft, Key.Comma)));
        test.WaitFor(() => SettingsPage is { Restoring: false }, 3.0f);
        test.Settle(0.3f);
        test.Run(() => Shot("settings-remembered"));
        test.Check("settings remembered: Ctrl+, opens it again on the same search and section", () =>
            SettingsPage is { } again && again != page && again.Search.Text == "blame" && group == EditorSettingsCatalog.GIT && again.CurrentGroup == group
            && again.IsRowShown("git_blame"));

        // A section asked for wins over where it was left
        test.Run(() =>
        {
            compositor.Focus = null;
            CloseSettings();
            OpenSettings(EditorSettingsCatalog.THEME);
        });
        test.Settle(0.4f);
        test.Check("settings remembered: OpenSettings of a section still lands on it, nothing typed", () =>
            SettingsPage is { } theme && theme.CurrentGroup == EditorSettingsCatalog.THEME && theme.Search.Text.Length == 0);

        // Where it's left now is the theme with nothing typed, and the tests after this start from nothing
        test.Run(() =>
        {
            compositor.Focus = null;
            CloseSettings();
            settingsLeft = null;
            reopenWhereLeftInTest = false;
        });
        test.Wait();
    }
}
