using Hex.Core;
using Hex.Shell;
using Horizon.HIDL.Runtime;

namespace Hex.Tests;

public class OnTypeFormattingSettingsTests
{
    [Fact]
    public void NativeTypingFormattingCanBeDisabledAndPersistsWithoutAffectingFormatOnSave()
    {
        string path = Path.Combine(Path.GetTempPath(), "hex-type-format-settings-" + Guid.NewGuid().ToString("N"), "settings.hor");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var settings = new HexSettings(path);
        Assert.True(settings.FormatOnType);
        Assert.False(settings.FormatOnSave);
        Assert.True(HexSettings.IsWorkspaceKey("format_on_type"));
        Assert.True(Assert.IsType<BooleanValue>(EditorSettingsCatalog.Usual["format_on_type"]).Value);
        settings.FormatOnType = false;
        Assert.Null(settings.Save());
        var reopened = new HexSettings(path);
        Assert.False(reopened.FormatOnType);
        Assert.False(reopened.FormatOnSave);
        var mapped = Assert.Single(VsCodeSettings.Plan(VsCodeSettings.Read("{\"editor.formatOnType\":false}"), []));
        Assert.Equal("format_on_type", mapped.Key);
        Assert.False(Assert.IsType<BooleanValue>(mapped.Value).Value);
        string workspace = Path.Combine(Path.GetDirectoryName(path)!, "workspace");
        Directory.CreateDirectory(workspace);
        Assert.Null(reopened.LoadWorkspace(workspace));
        Assert.Null(reopened.SetWorkspace("format_on_type", Values.Bool(true)));
        reopened.FormatOnType = true;
        Assert.Null(reopened.Save());
        Assert.False(new HexSettings(path).FormatOnType);
        var projectSettings = new HexSettings(path);
        Assert.Null(projectSettings.LoadWorkspace(workspace));
        Assert.True(projectSettings.FormatOnType);
        Assert.Null(projectSettings.LoadWorkspace(null));
        Assert.False(projectSettings.FormatOnType);
    }
}
