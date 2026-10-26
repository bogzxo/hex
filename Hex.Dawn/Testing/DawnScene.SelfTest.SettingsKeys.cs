using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test of the settings with the keyboard alone, the way VS Code's go, and how a row is laid out on a wide window
internal sealed partial class DawnScene
{
    private void TestSettingsKeys(HexPointerScript test)
    {
        SettingsPage page = null!;
        string? first = null, second = null;
        test.Run(() =>
        {
            page = SettingsPage!;
            page.Find(string.Empty);
            page.GoTo(page.Settings[0].Group);
        });
        test.Wait();

        // Down out of the search into the rows, down them a row at a time and back up into the search from the first
        test.Run(() =>
        {
            page.FocusSearch();
            page.Press(UIKeyboard.DOWN);
            first = page.FocusedKey;
        });
        test.Check("settings keys: Down in the search puts the keyboard on the first row", () =>
            first is not null && GroupOf(page, first) == page.SectionsShown.First(group => page.Settings.Any(setting => setting.Group == group)));
        test.Run(() =>
        {
            page.Press(UIKeyboard.DOWN);
            second = page.FocusedKey;
        });
        test.Check("settings keys: Down from a row goes to the next one", () => second is not null && second != first);
        test.Run(() => page.MoveRow(-1));
        test.Check("settings keys: and Up back to the one before", () => page.FocusedKey == first);
        test.Run(() => page.Press(UIKeyboard.UP));
        test.Check("settings keys: Up from the first row goes back to the search", () => page.Search.IsFocused && page.FocusedKey is null);

        // A dropdown that's shut lets Up and Down go row to row and keeps its value, a slider moves with Left and Right
        IRuntimeValue? effects = null;
        test.Run(() =>
        {
            page.Reveal("effects");
            effects = page.ValueOf!("effects");
        });
        test.Settle(0.2f);
        test.Run(() => page.Press(UIKeyboard.DOWN));
        test.Check("settings keys: Down on a shut dropdown goes to the next row and leaves its value be", () =>
            page.FocusedKey is { } key && key != "effects" && Equals(page.ValueOf!("effects"), effects));
        float blur = 0.0f;
        test.Run(() =>
        {
            page.Reveal("glass_blur");
            blur = settings.GlassBlur;
        });
        test.Settle(0.2f);
        test.Check("settings keys: a slider's row takes the keyboard", () => page.FocusedKey == "glass_blur");
        test.Run(() => page.Press(UIKeyboard.RIGHT));
        test.Check("settings keys: and Right moves it a step", () => settings.GlassBlur > blur);
        test.Run(() =>
        {
            page.Press(UIKeyboard.DOWN);
            page.Reset("glass_blur");
        });
        test.Check("settings keys: and Down still goes on to the next row", () => page.FocusedKey is { } key && key != "glass_blur");

        // A section at a time, Alt+Down and MoveSection, with and without a search
        string? from = null;
        string? After(string? group) => page.SectionsShown.ToList().IndexOf(group!) is var at and >= 0 && at + 1 < page.SectionsShown.Count ? page.SectionsShown[at + 1] : null;
        test.Run(() =>
        {
            page.GoTo(page.SectionsShown[0]);
            page.FocusFirstRow();
            from = GroupOf(page, page.FocusedKey);
            page.MoveSection(1);
        });
        test.Settle(0.2f);
        test.Check("settings keys: a section on goes to the next section and puts the keyboard on its first row", () =>
            After(from) is { } next && page.CurrentGroup == next && GroupOf(page, page.FocusedKey) == next);
        test.Run(() =>
        {
            from = page.CurrentGroup;
            page.Press(UIKeyboard.MOVE_LINES_DOWN);
        });
        test.Settle(0.2f);
        test.Check("settings keys: and Alt+Down the one after", () => After(from) is { } next && GroupOf(page, page.FocusedKey) == next);
        test.Run(() => page.Find("wrap"));
        test.Wait();
        test.Run(() =>
        {
            page.FocusFirstRow();
            from = GroupOf(page, page.FocusedKey);
            page.MoveSection(1);
        });
        test.Settle(0.2f);
        test.Check("settings keys: with a search the sections it left nothing in are passed over", () =>
            page.SectionsShown.Count >= 2 && After(from) is { } next && GroupOf(page, page.FocusedKey) == next && page.CurrentGroup == next
            && page.Settings.Where(setting => setting.Group == next).Any(setting => page.IsRowShown(setting.Key)));
        test.Run(() => page.Find(string.Empty));
        test.Wait();

        // The sections down the left, Up and Down along them and Enter into one
        string? lit = null;
        test.Run(() =>
        {
            page.GoTo(page.SectionsShown[0]);
            page.FocusFirstRow();

            // A box being typed in keeps Ctrl+Left for itself, so it starts from the first row that's a toggle or a choice
            for (int i = 0; i < 8 && page.Settings.FirstOrDefault(setting => setting.Key == page.FocusedKey) is { Kind: not (UIFormKind.Flag or UIFormKind.Choice) }; i++)
                page.MoveRow(1);
        });
        test.Wait();
        test.Run(() => page.Press(UIKeyboard.WORD_LEFT));
        test.Check("settings keys: Ctrl+Left from a row puts the keyboard on the sections", () => (lit = page.FocusedSection) is not null);
        test.Run(() => page.Press(UIKeyboard.DOWN));
        test.Wait();
        test.Run(() => Shot("settings-nav-focus"));
        test.Check("settings keys: Down on a section goes to the next one", () =>
            page.FocusedSection is { } next && next != lit && page.SectionsShown.ToList().IndexOf(next) == page.SectionsShown.ToList().IndexOf(lit!) + 1);
        test.Run(() => page.Press(UIKeyboard.UP));
        test.Check("settings keys: and Up back", () => page.FocusedSection == lit);
        string? entered = null;
        test.Run(() =>
        {
            page.Press(UIKeyboard.DOWN);
            entered = page.FocusedSection;
            page.Press(UIKeyboard.ENTER);
        });
        test.Settle(0.2f);
        test.Run(() => Shot("settings-row-focus"));
        test.Check("settings keys: Enter on a section scrolls there and puts the keyboard on its first row", () =>
            entered is not null && page.CurrentGroup == entered && GroupOf(page, page.FocusedKey) == entered);
        test.Run(() => page.FocusNav());
        test.Check("settings keys: and FocusNav comes back to the one lit", () => page.FocusedSection == entered);
        test.Run(() => compositor.Focus = null);
    }

    // The group of a setting by its key, null for none
    private static string? GroupOf(SettingsPage page, string? key) => page.Settings.FirstOrDefault(setting => setting.Key == key)?.Group;

    private void TestSettingsRows(HexPointerScript test)
    {
        SettingsPage page = null!;
        test.Run(() =>
        {
            page = SettingsPage!;
            page.Find(string.Empty);
            page.Enter("wrap_code", Values.Bool(true));
            page.Reveal("wrap_code");
        });
        test.Settle(0.3f);
        test.Run(() => Shot("settings-row-reset"));
        // Just after the name and the key small after it, not off at the far end of the row
        test.Check("settings rows: the reset sits just after the name, not off at the far end of the row", () =>
            page.RowBounds("wrap_code") is { } row && page.RowParts("wrap_code") is { } parts && !row.Reset.IsEmpty
            && (parts.Key.IsEmpty ? row.Title.Max.X : MathF.Max(row.Title.Max.X, parts.Key.Max.X)) is var named && row.Reset.Min.X >= named - 1 && row.Reset.Min.X - named <= 40);
        test.Check("settings rows: and no row is wider than about 1600 at the page's zoom", () =>
            page.Settings.Select(setting => page.RowBounds(setting.Key)).All(bounds => bounds is not { } shown || shown.Row.Width <= 1600.0f * page.ZoomNow + 2.0f));
        test.Check("settings rows: on a window wide enough that they'd have been wider", () => SettingsWindow!.Bounds.Width > 1100);
        test.Run(() => page.Reset("wrap_code"));

        // @section:, only the sections with the word in their name, the panels of others hidden
        test.Run(() => page.Find("@section:git"));
        test.Wait();
        test.Run(() => Shot("settings-section-filter"));
        test.Check("settings rows: @section:git leaves only the rows of Editor · Git, and none of the panels", () =>
            page.RowsShown > 0
            && page.Settings.Where(setting => page.IsRowShown(setting.Key)).All(setting => setting.Group == Hex.Shell.EditorSettingsCatalog.GIT)
            && page.Settings.Where(setting => setting.Group == Hex.Shell.EditorSettingsCatalog.GIT).All(setting => page.IsRowShown(setting.Key))
            && page.SectionsShown.SequenceEqual([Hex.Shell.EditorSettingsCatalog.GIT]));
        test.Check("settings rows: and the funnel offers it", () => page.FilterMenu().Items.Any(item => item.Shortcut == Horizon.UI.Components.SettingsPage.SECTION));
        test.Run(() => page.Find(string.Empty));
        test.Wait();

        // A right click on a section down the left resets every change in it
        test.Run(() =>
        {
            page.Enter("git_pull_mode", new StringValue("rebase"));
            page.Enter("git_smart_commit", new StringValue("never"));
            page.GoTo(Hex.Shell.EditorSettingsCatalog.GIT);
        });
        test.Wait();
        Label? link = null;
        test.Run(() => link = Descendants(page).OfType<Label>().FirstOrDefault(label => label.GetType().Name == "SectionLink" && label.Visible && label.Text == "Git"));
        test.RightClick(At(() => link, 0.3f, 0.5f));
        test.Wait();
        int changed = 0;
        test.Run(() => changed = page.Settings.Count(setting => setting.Group == Hex.Shell.EditorSettingsCatalog.GIT && page.IsModified!(setting.Key)));
        test.Run(() => Shot("settings-section-reset"));
        test.Check("settings rows: a right click on a section with changes offers to reset them all", () =>
            changed >= 2 && chrome.Root.Children.OfType<ContextMenu>().FirstOrDefault()?.Showing is { } menu
            && menu.Items.Any(item => item.Label == $"Reset {changed} settings in Git"));
        test.Run(() =>
        {
            ContextMenu? shown = chrome.Root.Children.OfType<ContextMenu>().FirstOrDefault();
            shown?.Showing?.Items.First(item => item.Label.StartsWith("Reset", StringComparison.Ordinal)).OnPicked!();
            shown?.Close();
        });
        test.Wait();
        test.Check("settings rows: and picking it leaves nothing in the section changed", () =>
            !page.Settings.Any(setting => setting.Group == Hex.Shell.EditorSettingsCatalog.GIT && page.IsModified!(setting.Key))
            && page.SectionMenu(Hex.Shell.EditorSettingsCatalog.GIT).Items.Count == 0);
    }
}
