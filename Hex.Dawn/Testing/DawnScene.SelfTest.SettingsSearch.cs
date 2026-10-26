using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test of the settings search by the words developers type, the rows in two columns, the reset saying what it
// puts back and the rows that hang off a switch
internal sealed partial class DawnScene
{
    private void TestSettingsSearch(HexPointerScript test)
    {
        SettingsPage page = null!;
        test.Run(() =>
        {
            page = SettingsPage!;
            page.Find(string.Empty);
        });

        // What people type rather than what the rows are called, each leaving the row it means
        foreach (var (typed, key) in new[] { ("word wrap", "wrap_code"), ("wordwrap", "wrap_code"), ("font size", "code_zoom"), ("autosave", "auto_save"), ("tabsize", "tab_size"), ("tab width", "tab_size") })
        {
            test.Run(() => page.Find(typed));
            test.Check($"settings search: keywords, {typed} finds {key}", () =>
                page.IsRowShown(key) && page.FoundText.Length > 0 && !page.FoundText.StartsWith("0 ", StringComparison.Ordinal));
        }

        test.Run(() => page.Find("tab width"));
        test.Wait();
        test.Run(() => Shot("settings-search-keywords"));

        // A word in one sentence of a panel doesn't bring the whole panel along, a heading still does
        test.Run(() => page.Find("wrap"));
        test.Wait();
        test.Check("settings search: keywords, wrap leaves Extensions · Available out though a description of it says wrap", () =>
            page.IsRowShown("wrap_code") && !page.SectionsShown.Contains(HexExtensions.AVAILABLE));
        test.Run(() => page.Find("add a language"));
        test.Wait();
        test.Check("settings search: keywords, add a language still finds the language servers' panel by its heading", () =>
            page.SectionsShown.Contains(HexLanguages.SERVER_SECTION));
        test.Run(() => page.Find(string.Empty));
        test.Wait();

        // A toggle, a choice and a number in the column down the right, past the end of their sentences
        test.Run(() => page.Reveal("git_subject_length"));
        test.Settle(0.3f);
        test.Run(() => Shot("settings-rows-columns"));
        bool Right(string key) => page.RowParts(key) is { } parts && !parts.Control.IsEmpty && !parts.Help.IsEmpty && parts.Control.Min.X > parts.Help.Max.X
            && parts.Control.Max.Y >= parts.Title.Min.Y && parts.Control.Max.Y <= parts.Row.Max.Y;
        test.Check("settings rows: control on the right of the sentence for a toggle, a choice and a whole number", () =>
            Right("wrap_code") && Right("git_pull_mode") && Right("git_subject_length"));
        test.Check("settings rows: control on the right, lined up down the page whatever the row", () =>
            page.RowParts("wrap_code") is { } toggle && page.RowParts("git_subject_length") is { } number && Math.Abs(toggle.Control.Max.X - number.Control.Max.X) < 1.5f);
        test.Check("settings rows: control on the right, a row wider than 860 when the window has the room", () =>
            page.RowParts("wrap_code") is { } row && (SettingsWindow!.Bounds.Width < 1240 || row.Row.Width > 862) && row.Row.Width <= 1600.0f * page.ZoomNow + 2.0f);
        test.Check("settings rows: control on the right, the key small after the name", () =>
            page.RowParts("git_subject_length") is { } parts && !parts.Key.IsEmpty && parts.Key.Min.X >= parts.Title.Max.X);

        // Changed, the row keeps its place and has its line down the edge with nothing focused, and its reset says what it
        // puts back once the editor's text settings say what they come as
        float before = 0.0f;
        test.Run(() =>
        {
            page.Reveal("tab_size");
            compositor.Focus = null;
        });
        test.Settle(0.3f);
        test.Run(() =>
        {
            before = page.RowParts("tab_size")?.Row.Min.X ?? float.NaN;
            page.Enter("tab_size", new NumberValue(8.0f));
            compositor.Focus = null;
        });
        test.Wait();
        test.Run(() => Shot("settings-rows-modified"));
        test.Check("settings rows: control on the right, a row marked changed doesn't move", () =>
            page.RowParts("tab_size") is { } parts && Near(parts.Row.Min.X, before, 0.5f) && page.RowMarked("tab_size") == true && compositor.Focus is null);
        test.Check("settings rows: control on the right, the reset says what it puts back", () =>
            page.ResetTooltipOf("tab_size") is { } said && (said == "Reset to 4" || page.DefaultOf?.Invoke("tab_size") is null && said == Horizon.UI.Components.SettingsPage.RESET_TO_DEFAULT));
        test.Run(() => page.Reset("tab_size"));

        // Saving by itself switched off, how long it waits is in a little and faint, and full strength once it's on
        bool autoSave = false;
        test.Run(() =>
        {
            autoSave = page.ValueOf!("auto_save") is BooleanValue { Value: true };
            page.Enter("auto_save", Values.Bool(false));
            page.Reveal("auto_save_delay");
        });
        test.Settle(0.3f);
        test.Run(() => Shot("settings-rows-dependent"));
        test.Check("settings rows: dependent row dimmed and in a little while auto save is off", () =>
            page.DependentShown("auto_save_delay") is (true, true) && page.RowParts("auto_save_delay") is { } delay && page.RowParts("auto_save") is { } parent
            && delay.Title.Min.X > parent.Title.Min.X + 10.0f);
        test.Run(() => page.Enter("auto_save", Values.Bool(true)));
        test.Wait();
        test.Check("settings rows: dependent row dimmed no more once auto save is on, still in a little", () =>
            page.DependentShown("auto_save_delay") is (true, false));
        test.Run(() =>
        {
            if (autoSave)
                page.Enter("auto_save", Values.Bool(true));
            else
                page.Reset("auto_save");
        });
        test.Check("settings rows: dependent row dimmed again with auto save back as it was", () =>
            page.DependentShown("auto_save_delay") is (true, var faint) && faint == !autoSave);

        TestSettingsPage(test);
    }

    // The page's own part of a round of the settings. The controls down one right edge with the search's, the key on
    // every row, the best hit of a search first with the keyword it was found by, the page's words out of en-gb, Reset
    // all settings asking first, and Shift+Up stepping a number
    private void TestSettingsPage(HexPointerScript test)
    {
        SettingsPage page = null!;
        System.Numerics.Vector2 size = default;
        test.Run(() =>
        {
            page = SettingsPage!;
            page.Find(string.Empty);
            size = SettingsWindow!.Size;
            SettingsWindow.Size = size with { X = 1600.0f };
        });
        test.Settle(0.3f);
        test.Run(() => page.Reveal("code_zoom"));
        test.Settle(0.3f);
        test.Run(() => Shot("settings-rows-right-edge"));
        string[] lined = ["tab_size", "code_font", "code_zoom", "minimap"];
        test.Check("rows: controls share one right edge", () =>
            lined.Select(key => page.RowParts(key)?.Control ?? default).ToList() is var controls && controls.All(control => !control.IsEmpty)
            && controls.Max(control => control.Max.X) - controls.Min(control => control.Max.X) <= 2.0f
            && MathF.Abs(controls[0].Max.X - page.Search.Bounds.Max.X) <= 24.0f && SettingsWindow!.Bounds.Width >= 1599.0f);
        test.Check("rows: every row shows its key", () =>
            new[] { "tab_size", "insert_spaces", "line_numbers" }.All(key => page.RowParts(key) is { } parts && !parts.Key.IsEmpty && parts.Key.Width > 4.0f));
        test.Run(() => SettingsWindow!.Size = size);
        test.Settle(0.2f);

        // Font, Code font first in Dawn though the terminal's Font is all of what was typed, and wordwrap the keyword
        test.Run(() => page.Find("font"));
        test.Wait();
        test.Run(() => Shot("settings-search-ranked"));
        test.Check("search: best hit first", () => page.KeysShown is [ "code_font", .. ] && page.IsRowInView("code_font"));

        // The key and the pill after it apart. Code zoom is found by its keyword font, and its key was measured short and
        // painted whole, so it read code_zoofont with the pill over its end
        test.Run(() => page.Find("font"));
        test.Settle(0.2f);
        test.Run(() => Shot("settings-search-pill"));
        test.Check("search: pill after the key, code_zoom's font pill starts past the end of its key", () =>
            page.KeyParts("code_zoom") is { } parts && !parts.FoundBy.IsEmpty && !parts.Key.IsEmpty && parts.FoundBy.Min.X >= parts.Key.Max.X && !parts.KeyCut
            && page.FoundByShown("code_zoom") is { } by && by.Contains("font", StringComparison.OrdinalIgnoreCase));
        test.Check("search: pill after the key, no key's words are painted past its own edge", () =>
            page.KeysShown.Count > 0 && page.KeysShown.All(key => page.KeyParts(key) is { } parts && parts.KeyPainted.Max.X <= parts.Key.Max.X + 0.5f));

        // Forgiving about how it's typed, spaces, underscores and VS Code's dotted ids all find the row they mean first
        foreach (var (typed, key) in new[] { ("tabsize", "tab_size"), ("tab size", "tab_size"), ("editor.tabsize", "tab_size"), ("editor.tabSize", "tab_size"), ("word wrap", "wrap_code"), ("wordwrap", "wrap_code"), ("editor.wordWrap", "wrap_code") })
        {
            test.Run(() => page.Find(typed));
            test.Check($"search: forgiving, {typed} puts {key} first", () => page.KeysShown.FirstOrDefault() == key);
        }

        var tabSize = new UISetting("tab_size", "Tab size", UIFormKind.Whole, string.Empty);
        test.Check("search: forgiving, a word as it's written still ranks over the same found run together", () =>
            Horizon.UI.Components.SettingsPage.Score(tabSize, "size") > Horizon.UI.Components.SettingsPage.Score(tabSize, "bsize")
            && Horizon.UI.Components.SettingsPage.Score(tabSize, "tab") > Horizon.UI.Components.SettingsPage.Score(tabSize, "tabsi")
            && Horizon.UI.Components.SettingsPage.Score(tabSize, "bsize") > 0 && Horizon.UI.Components.SettingsPage.Undotted("*.orig") == "*.orig");
        test.Run(() => page.Find("wordwrap"));
        test.Wait();
        test.Run(() => Shot("settings-search-found-by"));
        test.Check("search: best hit first, and the keyword wordwrap shown on Wrap code", () =>
            page.FoundByShown("wrap_code") is { } by && by.Contains("wordwrap", StringComparison.OrdinalIgnoreCase) && page.FoundByShown("tab_size") is null);

        // The page's own words are en-gb's
        test.Run(() => page.Find("@id:git_blame"));
        test.Check("strings: from en-gb, how many a search found", () =>
            page.FoundText == Said("settings_page.found_one").Replace("{count}", "1") && page.Search.Placeholder == Said("settings_page.search_placeholder_common"));
        test.Run(() => page.Find("zzqqxx"));
        test.Check("strings: from en-gb, a search that found nothing", () =>
            page.SaysNothingFound && page.NothingText == Said("settings_page.nothing_found").Replace("{query}", "zzqqxx"));
        test.Run(() =>
        {
            page.Find(string.Empty);
            page.Enter("tab_size", new NumberValue(8.0f));
        });
        test.Wait();
        test.Check("strings: from en-gb, the reset's tooltip", () =>
            page.ResetTooltipOf("tab_size") == Said("settings_page.reset_to").Replace("{value}", "4"));

        // Shift+Up on the tab size's box, a step up and the keyboard staying on the row
        test.Run(() => page.Reveal("tab_size"));
        test.Settle(0.2f);
        test.Run(() => page.Press(UIKeyboard.SELECT_UP));
        test.Check("rows: Shift+Up steps a number box a step and stays on its row", () =>
            page.ValueOf!("tab_size") is NumberValue { Value: 9.0f } && page.FocusedKey == "tab_size");
        test.Run(() => page.Enter("tab_size", new NumberValue(16.0f)));
        test.Run(() => page.Press(UIKeyboard.SELECT_UP));
        test.Check("rows: and no further than its most", () => page.ValueOf!("tab_size") is NumberValue { Value: 16.0f });

        // Reset all settings, off the funnel, asks first and Yes leaves nothing changed
        test.Run(() =>
        {
            page.Enter("tab_size", new NumberValue(8.0f));
            page.Enter("minimap", Values.Bool(!settings.Minimap));
            compositor.Focus = null;
        });
        test.Wait();
        test.Run(() => page.FilterMenu().Items.First(item => item.Label == Said("settings_page.reset_all")).OnPicked!());
        test.Wait();
        test.Run(() => Shot("settings-reset-all"));
        UIDialog? asked = null;
        test.Check("reset all: confirm then resets, it asks first", () =>
            (asked = chrome.Root.Children.OfType<UIDialog>().FirstOrDefault()) is { } dialog
            && dialog.Buttons.Any(button => button.Label == Said("settings_page.reset_all_yes")) && page.IsModified!("tab_size"));
        test.Run(() => asked?.Buttons.First(button => button.Label == Said("settings_page.reset_all_yes")).OnPressed?.Invoke());

        // The dialog fades out before it's gone
        test.Settle(0.4f);
        test.Run(() => page.Find(Horizon.UI.Components.SettingsPage.MODIFIED));
        test.Check("reset all: confirm then resets, Yes leaves @modified empty", () =>
            page.RowsShown == 0 && !page.IsModified!("tab_size") && !page.IsModified!("minimap") && !chrome.Root.Children.OfType<UIDialog>().Any());

        // Every part open again, what the tests after this go by
        test.Run(() =>
        {
            page.Find(string.Empty);
            foreach (string part in Hex.Shell.EditorSettingsCatalog.Parts)
                page.SetPartCollapsed(part, false);
        });
        test.Wait();
    }
}
