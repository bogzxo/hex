using System.IO.Compression;
using System.Text.Json.Nodes;
using Hex.Installation;

namespace Hex.Installer.Tests;

public sealed class InstallChoicesTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "hex-choices-" + Guid.NewGuid().ToString("N"));
    public InstallChoicesTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void DefaultsMatchLegacyIntegrationAndRoundTripAllDisabled()
    {
        Assert.Equal(new InstallChoices(), InstallChoices.FromJson(null));
        Assert.Equal(new InstallChoices(), InstallChoices.FromJson(new JsonObject()));
        var none = new InstallChoices(false, false, false, false, false);
        Assert.Equal(none, InstallChoices.FromJson(JsonNode.Parse(none.ToJson().ToJsonString())));
        Assert.Throws<InvalidOperationException>(() => InstallChoices.FromJson(JsonNode.Parse("{\"desktop_hex\":\"wrong\"}")));
    }

    [Fact]
    public void UpdatesPreserveChoicesAndRollbackRestoresPreviousChoices()
    {
        string root = Path.Combine(folder, "hex"), stage = root + ".stage", zip = Path.Combine(folder, "release.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (string file in new[] { InstallPaths.Executable, "installer/" + InstallPaths.InstallerExecutable })
            {
                using var writer = new StreamWriter(archive.CreateEntry("Hex/" + file).Open());
                writer.Write("payload");
            }
        var choices = new InstallChoices(false, true, false, true, false);
        InstallTransaction.Prepare(zip, stage, root, "2.4.0", choices: choices);
        InstallTransaction.Commit(stage, root, () => { });
        Assert.Equal(choices, InstallTransaction.ReadChoices(root));
        InstallTransaction.Prepare(zip, stage, root, "2.5.0");
        Assert.Equal(choices, InstallTransaction.ReadChoices(stage));
        InstallTransaction.Commit(stage, root, () => { });
        var replacement = new InstallChoices();
        InstallTransaction.Prepare(zip, stage, root, "2.6.0", choices: replacement);
        Assert.Throws<IOException>(() => InstallTransaction.Commit(stage, root, () => throw new IOException("failed")));
        Assert.Equal(choices, InstallTransaction.ReadChoices(root));
        InstallTransaction.Commit(stage, root, () => { });
        Assert.Equal(replacement, InstallTransaction.ReadChoices(root));
        var legacy = JsonNode.Parse(File.ReadAllText(Path.Combine(root, InstallPaths.Manifest)))!.AsObject();
        legacy.Remove("integration");
        File.WriteAllText(Path.Combine(root, InstallPaths.Manifest), legacy.ToJsonString());
        Assert.Equal(new InstallChoices(), InstallTransaction.ReadChoices(root));
    }

    [Fact]
    public void CreatesOnlySelectedLinuxShortcutsAndRemovesDeselectedOwnedEntries()
    {
        string root = Path.Combine(folder, "hex"), apps = Path.Combine(folder, "apps"), desktop = Path.Combine(folder, "desktop");
        PlatformRegistration.RegisterLinux(root, new(DesktopDusk: true), apps, desktop);
        Assert.True(File.Exists(Path.Combine(desktop, "hex-dawn.desktop")));
        Assert.True(File.Exists(Path.Combine(desktop, "hex-dusk.desktop")));
        Assert.Contains("--dusk", File.ReadAllText(Path.Combine(desktop, "hex-dusk.desktop")));
        PlatformRegistration.RegisterLinux(root, new(false, false, false, true, false), apps, desktop);
        Assert.False(File.Exists(Path.Combine(desktop, "hex-dawn.desktop")));
        Assert.False(File.Exists(Path.Combine(desktop, "hex-dusk.desktop")));
        Assert.False(File.Exists(Path.Combine(apps, "hex-dawn.desktop")));
        Assert.False(File.Exists(Path.Combine(apps, "hex-dusk.desktop")));
        Assert.True(File.Exists(Path.Combine(apps, "hex-uninstall.desktop")));
        File.WriteAllText(Path.Combine(desktop, "hex-dawn.desktop"), "another installation");
        PlatformRegistration.RegisterLinux(root, new(false, false, true, false, false), apps, desktop);
        Assert.Equal("another installation", File.ReadAllText(Path.Combine(desktop, "hex-dawn.desktop")));
        Assert.True(File.Exists(Path.Combine(apps, "hex-dawn.desktop")));
        Assert.False(File.Exists(Path.Combine(apps, "hex-dusk.desktop")));
    }

    [Fact]
    public void ModuleChoicesRoundTripAndDefaultMissingModulesToEnabled()
    {
        var defaults = new InstallChoices();
        foreach (string id in InstallChoices.KnownModules)
            Assert.True(defaults.IsModuleEnabled(id));

        var custom = new InstallChoices(Modules: new Dictionary<string, bool>
        {
            ["notebooks"] = false,
            ["designer"] = false
        });
        InstallChoices parsed = InstallChoices.FromJson(JsonNode.Parse(custom.ToJson().ToJsonString()));
        Assert.Equal(custom, parsed);
        Assert.False(parsed.IsModuleEnabled("notebooks"));
        Assert.False(parsed.IsModuleEnabled("designer"));
        Assert.True(parsed.IsModuleEnabled("link"));
        Assert.True(parsed.IsModuleEnabled("local_ai"));
        Assert.True(parsed.IsModuleEnabled("agentic"));

        // Legacy manifest without "modules" defaults every module to enabled
        InstallChoices legacy = InstallChoices.FromJson(JsonNode.Parse("{\"desktop_hex\":true}"));
        Assert.Equal(new InstallChoices(), legacy);
        Assert.Throws<InvalidOperationException>(() => InstallChoices.FromJson(JsonNode.Parse("{\"modules\":{\"notebooks\":\"nope\"}}")));
    }

    [Fact]
    public void PrepareSkipsDeselectedModuleNativeFilesAndWritesFreshSettingsWithoutClobberingExisting()
    {
        string root = Path.Combine(folder, "hex-mod"), stage = root + ".stage", zip = Path.Combine(folder, "mod-release.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (string file in new[]
            {
                InstallPaths.Executable,
                "installer/" + InstallPaths.InstallerExecutable,
                "modules/Hex.Notebooks.Render.dll",
                "modules/libSkiaSharp.dll",
                "Assets/core.dat"
            })
            {
                using var writer = new StreamWriter(archive.CreateEntry("Hex/" + file).Open());
                writer.Write("payload");
            }
        }

        var withoutNotebooks = new InstallChoices(Modules: new Dictionary<string, bool>
        {
            ["notebooks"] = false,
            ["link"] = false
        });
        InstallTransaction.Prepare(zip, stage, root, "2.9.0", choices: withoutNotebooks);
        InstallTransaction.Commit(stage, root, () => { });

        Assert.True(File.Exists(Path.Combine(root, "Assets", "core.dat")));
        Assert.False(File.Exists(Path.Combine(root, "modules", "Hex.Notebooks.Render.dll")));
        Assert.False(File.Exists(Path.Combine(root, "modules", "libSkiaSharp.dll")));

        string settingsFile = Path.Combine(root, "settings", "settings.hor");
        Assert.True(File.Exists(settingsFile));
        string settingsText = File.ReadAllText(settingsFile);
        Assert.Contains("module_notebooks: false", settingsText);
        Assert.Contains("module_link: false", settingsText);
        Assert.DoesNotContain("module_designer", settingsText);

        // Existing user settings.hor must never be overwritten on update
        File.WriteAllText(settingsFile, "// user custom settings");
        InstallTransaction.Prepare(zip, stage, root, "2.9.1", choices: withoutNotebooks);
        InstallTransaction.Commit(stage, root, () => { });
        Assert.Equal("// user custom settings", File.ReadAllText(settingsFile));
    }
}
