using Hex.Dawn;
using Horizon.UI.Drawing;

namespace Hex.Tests;

/// <summary>Regression tests for Pass 8 macOS New File flow and code-font fallback handling.</summary>
public sealed class Pass8NewFileAndFontTests
{
    [Fact]
    public void Menlo_alias_on_macOS_resolves_to_system_ttc()
    {
        string? resolved = HexCodeEditing.TryResolveFontPath(
            "Menlo",
            programFolder: "/Applications/Hex.app/Contents/MacOS",
            isMac: true,
            fileExists: p => p == "/System/Library/Fonts/Menlo.ttc");

        Assert.Equal("/System/Library/Fonts/Menlo.ttc", resolved);
    }

    [Fact]
    public void Broken_code_font_falls_back_cleanly_without_exposing_zero_glyph_font()
    {
        string? warned = null;
        var unloaded = new UIFont(Path.GetTempPath(), "broken-font-pass8.ttc", 96);
        Assert.False(unloaded.IsLoaded);

        UIFont? result = HexCodeEditing.ResolveConfiguredCodeFont(
            "broken-font",
            resolvePath: name => name == "broken-font" ? "/bad/broken-font.ttc" : "/fallback/hack.ttf",
            loadShared: _ => unloaded,
            onMissingOrInvalid: name => warned = name);

        Assert.Equal("broken-font", warned);
        Assert.Null(result);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void File_new_command_uses_scratchpad_when_empty_workspace_is_not_browsing(bool workspaceEmpty, bool browsing, bool expectScratchpad)
    {
        Assert.Equal(expectScratchpad, DawnScene.ShouldCreateScratchpadOnNewFile(workspaceEmpty, browsing));
    }
}
