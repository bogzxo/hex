using Hex.Core;
using Hex.Shell;
using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Tests;

/// <summary>
/// A setting is written down in several places (its field, the catalogue's row, the catalogue's usual value, the clamp
/// table the settings window goes by) and nothing but these keeps them saying the same thing. They are the guard for
/// moving all of it into one table.
/// </summary>
public class HexSettingsTableTests
{
    private static IEnumerable<UISetting> Rows => EditorSettingsCatalog.Settings.Concat(EditorSettingsCatalog.Text);

    [Fact]
    public void The_catalogue_usual_values_are_the_settings_own_defaults()
    {
        Dictionary<string, IRuntimeValue> defaults = new HexSettings(null).EditorValues();
        string[] differing = [.. EditorSettingsCatalog.Usual
            .Where(usual => defaults.TryGetValue(usual.Key, out IRuntimeValue? own) && !SettingValues.Same(own, usual.Value))
            .Select(usual => usual.Key).Order(StringComparer.Ordinal)];
        Assert.Empty(differing);
    }

    [Fact]
    public void Every_value_a_row_of_the_settings_can_be_dragged_to_is_one_the_clamp_table_takes()
    {
        // The two ranges aren't the same on purpose: a slider offers what is sensible and the file takes more, for
        // whoever writes it by hand. What must never be is a slider that goes somewhere the file is then cut back from
        string[] outside = [.. Rows
            .Where(row => row.Most > row.Least && Hex.Dawn.DawnScene.ClampEnds.TryGetValue(row.Key, out var ends)
                && (row.Least < ends.Least || row.Most > ends.Most))
            .Select(row => $"{row.Key}: row {row.Least}..{row.Most}, taken {Hex.Dawn.DawnScene.ClampEnds[row.Key].Least}..{Hex.Dawn.DawnScene.ClampEnds[row.Key].Most}")
            .Order(StringComparer.Ordinal)];
        Assert.Empty(outside);
    }

    [Fact]
    public void Changed_settings_survive_a_save_and_a_read_and_nothing_else_is_written()
    {
        string folder = Path.Combine(Path.GetTempPath(), "hex-settings-table-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "settings.hor");
            var written = new HexSettings(file) { TabSize = 3, EditorTheme = "graphite", MiddleScrollSpeed = 2.0f };
            Assert.Null(written.Save());

            var read = new HexSettings(file);
            Assert.Equal(3, read.TabSize);
            Assert.Equal("graphite", read.EditorTheme);
            Assert.Equal(2.0f, read.MiddleScrollSpeed);

            // Only what isn't what it comes as is written, and the save leaves nothing beside the file
            string text = File.ReadAllText(file);
            Assert.Contains("tab_size", text);
            Assert.DoesNotContain("code_zoom", text);
            Assert.Equal([file], Directory.GetFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_key_this_Hex_does_not_know_is_written_back_as_it_was()
    {
        string folder = Path.Combine(Path.GetTempPath(), "hex-settings-unknown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "settings.hor");
            var had = new Horizon.HIDL.HIDLObject().Add("tab_size", 3.0f).Add("setting_of_a_newer_hex", "kept").Add("another_one", 7.0f);
            File.WriteAllText(file, SettingsSections.Write(file, (SettingsSections.EDITOR, had)));

            // Twice over, the second save is of a file the first one wrote
            for (int save = 0; save < 2; save++)
            {
                var settings = new HexSettings(file) { CodeZoom = 1.5f + save * 0.1f };
                Assert.Null(settings.Save());
                Dictionary<string, IRuntimeValue> kept = SettingsSections.Section(SettingsSections.Read(file)!, SettingsSections.EDITOR);
                Assert.True(kept.GetValueOrDefault("setting_of_a_newer_hex") is StringValue { Value: "kept" });
                Assert.True(kept.GetValueOrDefault("another_one") is NumberValue { Value: 7.0f });
                Assert.True(kept.GetValueOrDefault("tab_size") is NumberValue { Value: 3.0f });
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Every_setting_has_one_row_and_the_ends_are_the_rows_own()
    {
        Assert.Equal(HexSettings.Rows.Length, HexSettings.Rows.Select(row => row.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(new HexSettings(null).EditorValues().Keys, HexSettings.Rows.Select(row => row.Key));
        Assert.Same(HexSettings.Ranges, Hex.Dawn.DawnScene.ClampEnds);
        Assert.Equal((1.0f, 16.0f), HexSettings.Ranges["tab_size"]);
        Assert.False(HexSettings.Ranges.ContainsKey("left_width"));
        Assert.True(HexSettings.IsKnown("extension_settings") && !HexSettings.IsKnown("setting_of_a_newer_hex"));
    }

    [Fact]
    public void A_setting_the_file_has_at_what_it_comes_as_reads_as_untouched()
    {
        var settings = new HexSettings(null);
        foreach (var (key, value) in settings.EditorValues())
            Assert.True(SettingValues.Same(HexSettings.Usual.GetValueOrDefault(key), value), key);
    }
}
