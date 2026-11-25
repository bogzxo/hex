using Hex.Core;
using Hex.Shell;

namespace Hex.Tests;

/// <summary>Which theme the editor is drawn in while it follows the system's appearance, and that the setting for it is kept.</summary>
public class HexAppearanceTests
{
    [Theory]
    // not following: the pick, whatever the system is
    [InlineData(false, true, "flat", "solstice_day", "solstice_day")]
    [InlineData(false, false, "flat", "solstice", "solstice")]
    // the system's mode is the pick's already
    [InlineData(true, true, "flat", "solstice", "solstice")]
    [InlineData(true, true, "flat", "midnight", "midnight")]
    [InlineData(true, false, "flat", "solstice_day", "solstice_day")]
    [InlineData(true, false, "flat", "light", "light")]
    // light system, dark pick
    [InlineData(true, false, "flat", "solstice", "solstice_day")]
    [InlineData(true, false, "flat", "graphite", "solstice_day")]
    [InlineData(true, false, "flat", "horizon", "light")]
    [InlineData(true, false, "flat", "visual_studio", "light")]
    // dark system, light pick
    [InlineData(true, true, "flat", "solstice_day", "solstice")]
    [InlineData(true, true, "flat", "light", "dark")]
    // a skin without the pairs, and no theme at all
    [InlineData(true, false, "kenney", "blue", "blue")]
    [InlineData(true, true, "flat", "", "")]
    public void The_theme_shown_is_decided_by_the_setting_the_system_and_the_pick(bool follow, bool dark, string skin, string pick, string shown) =>
        Assert.Equal(shown, HexAppearance.ThemeFor(follow, dark, skin, pick));

    [Fact]
    public void Following_the_system_is_off_for_settings_that_are_never_kept_and_is_kept_when_set()
    {
        Assert.Equal(OperatingSystem.IsMacOS(), new HexSettings(null).ThemeFollowSystem);
        Assert.Equal(OperatingSystem.IsMacOS(), EditorSettingsCatalog.Usual.Single(usual => usual.Key == "theme_follow_system").Value is Horizon.HIDL.Runtime.BooleanValue { Value: true });

        string folder = Path.Combine(Path.GetTempPath(), "hex-appearance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "settings.hor");
            var written = new HexSettings(file) { ThemeFollowSystem = true, EditorTheme = "graphite" };
            Assert.Null(written.Save());
            Assert.Equal(!OperatingSystem.IsMacOS(), File.ReadAllText(file).Contains("theme_follow_system"));

            var read = new HexSettings(file);
            Assert.True(read.ThemeFollowSystem);
            Assert.Equal("graphite", read.EditorTheme);

            // turned off it reads as off from a file that exists, and is written only where off is not the default (a Mac)
            read.ThemeFollowSystem = false;
            Assert.Null(read.Save());
            Assert.Equal(OperatingSystem.IsMacOS(), File.ReadAllText(file).Contains("theme_follow_system"));
            Assert.False(new HexSettings(file).ThemeFollowSystem);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void The_default_follows_the_system_on_a_mac_only(bool mac, bool follows) =>
        Assert.Equal(follows, HexSettings.ThemeFollowSystemDefault(mac));

    [Theory]
    // only a Mac, with a file that is there, that was not decided before
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, false, false)]
    public void An_existing_file_is_pinned_off_once_on_a_mac_only(bool mac, bool exists, bool decided, bool pinned) =>
        Assert.Equal(pinned, HexSettings.ThemeFollowNeedsPin(mac, exists, decided));

    [Fact]
    public void A_file_that_was_there_is_written_off_once_on_a_mac_and_a_new_one_is_left_to_follow()
    {
        string folder = Path.Combine(Path.GetTempPath(), "hex-appearance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string old = Path.Combine(folder, "old.hor");
            var section = new Horizon.HIDL.HIDLObject();
            section.Add("tab_size", 3.0f);
            File.WriteAllText(old, Horizon.UI.Components.SettingsSections.Write(old, (Horizon.UI.Components.SettingsSections.EDITOR, section)));
            HexSettings.PinFollowSystem(old);
            Assert.False(new HexSettings(old).ThemeFollowSystem);
            Assert.Equal(OperatingSystem.IsMacOS(), File.ReadAllText(old).Contains("theme_follow_system"));

            // decided once: following turned on later is kept and not pinned again
            var on = new HexSettings(old) { ThemeFollowSystem = true };
            Assert.Null(on.Save());
            HexSettings.PinFollowSystem(old);
            Assert.True(new HexSettings(old).ThemeFollowSystem);

            // no file: a new install, decided with the file still not there, and following on a Mac
            string fresh = Path.Combine(folder, "fresh.hor");
            HexSettings.PinFollowSystem(fresh);
            Assert.False(File.Exists(fresh));
            Assert.Equal(OperatingSystem.IsMacOS(), new HexSettings(fresh).ThemeFollowSystem);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

}
