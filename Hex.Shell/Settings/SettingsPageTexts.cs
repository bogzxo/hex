using Horizon.UI.Components;

namespace Hex.Shell;

/// <summary>
/// What the settings window says of its own in Hex's words, out of the settings_page section of the texts. Both modes
/// give it to their window, the engine's page only knows English.
/// </summary>
public static class SettingsPageTexts
{
    public static SettingsTexts Make() => new()
    {
        Title = Said("title"),
        SearchPlaceholder = Said("search_placeholder"),
        ResetToDefault = Said("reset_to_default"),
        ResetTo = Said("reset_to"),
        On = Said("on"),
        Off = Said("off"),
        General = Said("general"),
        ResetSectionOne = Said("reset_section_one"),
        ResetSectionMany = Said("reset_section_many"),
        ResetPartOne = Said("reset_part_one"),
        ResetPartMany = Said("reset_part_many"),
        ResetAll = Said("reset_all"),
        Filter = Said("filter"),
        FilterTooltip = Said("filter_tip"),
        FilterModified = Said("filter_modified"),
        FilterId = Said("filter_id"),
        FilterSection = Said("filter_section"),
        ClearTooltip = Said("clear_tip"),
        CloseTooltip = Said("close_tip"),
        NothingFound = Said("nothing_found"),
        ClearSearch = Said("clear_search"),
        FoundOne = Said("found_one"),
        FoundMany = Said("found_many"),
        ResetSetting = Said("reset_setting"),
        CopyId = Said("copy_id"),
        CopyLine = Said("copy_line"),
        Between = Said("between"),
        NothingToPick = Said("nothing_to_pick"),
        ResetAllTitle = Said("reset_all_title"),
        ResetAllTextOne = Said("reset_all_text_one"),
        ResetAllTextMany = Said("reset_all_text_many"),
        ResetAllYes = Said("reset_all_yes"),
        ResetAllNo = Said("reset_all_no"),
        AddItem = Window("add_item"),
        PairKey = Window("pair_key"),
        PairValue = Window("pair_value"),
        EditItem = Window("edit_item"),
        RemoveItem = Window("remove_item"),
        PairOk = Window("pair_ok"),
        PairCancel = Window("pair_cancel")
    };

    // One of the section's texts, its names in braces left for the page to fill in
    private static string Said(string key) => HexText.Get("settings_page." + key);

    // The words of a row of pairs, kept with the rest of the window's own
    private static string Window(string key) => HexText.Get("settings_window." + key);
}
