using Hex.Core;
using Hex.Dawn;
using Hex.Shell;
using Horizon.HIDL.Runtime;

namespace Hex.Tests;

public class NativeMarkdownSettingsTests
{
    [Theory]
    [InlineData("README.md", true)]
    [InlineData("notes.MARKDOWN", true)]
    [InlineData("data.json", false)]
    [InlineData(null, false)]
    public void Native_preview_matches_markdown_files(string? path, bool expected) => Assert.Equal(expected, DawnScene.IsMarkdownFile(path));

    [Fact]
    public void Legacy_markdown_extension_is_not_listed_in_catalogs()
    {
        string root = Path.Combine(Path.GetTempPath(), "hex-native-markdown-" + Guid.NewGuid().ToString("N"));
        foreach (string name in new[] { "markdown_preview", "another_extension" })
        {
            Directory.CreateDirectory(Path.Combine(root, name));
            File.WriteAllText(Path.Combine(root, name, HexExtensionCatalog.FILE), "let extension = {};\n");
        }
        Assert.Equal("another_extension", Path.GetFileName(Assert.Single(HexExtensionCatalog.Extensions(root))));
    }

    [Fact]
    public void Shared_scroll_speed_has_safe_bounds_persists_and_resets_to_one()
    {
        string root = Path.Combine(Path.GetTempPath(), "hex-scroll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "settings.hor");
        var settings = new HexSettings(path);
        Assert.Equal(1, settings.ScrollSpeed);
        settings.ScrollSpeed = 2.5f;
        Assert.Null(settings.Save());
        Assert.Equal(2.5f, new HexSettings(path).ScrollSpeed);
        Assert.Equal(1, Assert.IsType<NumberValue>(EditorSettingsCatalog.Usual["scroll_speed"]).Value);
        Assert.Equal(0.25f, HexSettings.ScrollSpeedOf(-20));
        Assert.Equal(5, HexSettings.ScrollSpeedOf(100));
        Assert.Equal(1, HexSettings.ScrollSpeedOf(float.NaN));
        Assert.Equal(1, HexSettings.ScrollSpeedOf(float.PositiveInfinity));
    }
}
