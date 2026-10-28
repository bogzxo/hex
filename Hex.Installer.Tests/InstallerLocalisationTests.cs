using System.Text.RegularExpressions;
using Hex.Installation;
using Horizon.Content;

namespace Hex.Installer.Tests;

[CollectionDefinition("Installer texts", DisableParallelization = true)]
public sealed class InstallerTextCollection;

[Collection("Installer texts")]
public sealed class InstallerLocalisationTests
{
    private static IReadOnlyDictionary<string, string> Read() => Localisation.ReadFile(
        Path.Combine(AppContext.BaseDirectory, "Assets", "installer", "localisation", "en-gb.hor"));

    [Fact]
    public void LayoutAndRuntimeKeysExistInTheActualLanguageFile()
    {
        var strings = Read();
        foreach (string layout in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Assets", "installer"), "*.hor"))
            foreach (Match match in Regex.Matches(File.ReadAllText(layout), "@installer\\.[a-z_.]+"))
                Assert.True(strings.ContainsKey(match.Value[1..]), match.Value);
        foreach (string path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "TestSources"), "*.cs"))
            foreach (Match match in Regex.Matches(File.ReadAllText(path), "InstallText\\.Get\\(\"([^\"]+)\""))
                Assert.True(strings.ContainsKey("installer." + match.Groups[1].Value), match.Groups[1].Value);
    }

    [Fact]
    public void FormatsEditableMessagesAndIdentifiesMissingKeys()
    {
        var strings = Read();
        InstallText.Use(strings);
        Assert.Equal(strings["installer.version.platform"].Replace("{platform}", "win-x64"), InstallText.Get("version.platform", ("platform", "win-x64")));
        Assert.Equal(strings["installer.status.downloading"].Replace("{version}", "v3.0.0").Replace("{downloaded}", "12.5").Replace("{total}", "40.0"), InstallText.Get("status.downloading",
            ("version", "v3.0.0"), ("downloaded", "12.5"), ("total", "40.0")));
        Assert.Equal(strings["installer.brand.tagline"], InstallText.Get("brand.tagline"));
        Assert.Equal("@installer.missing", InstallText.Get("missing"));
    }
}
