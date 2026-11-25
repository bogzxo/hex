using Hex.Core;
using Hex.Core.Modules;
using Hex.Dawn;
using Hex.Shell;
using Horizon.HIDL.Runtime;

namespace Hex.Tests;

[CollectionDefinition("HexModules", DisableParallelization = true)]
public sealed class HexModulesCollection;

[Collection("HexModules")]
public sealed class HexModulesTests : IDisposable
{
    public HexModulesTests() => HexModules.ResetForTests();

    public void Dispose() => HexModules.ResetForTests();

    [Fact]
    public void Catalog_exposes_all_first_party_modules_and_supports_registration()
    {
        string[] expected = [HexModuleId.Link, HexModuleId.LocalAi, HexModuleId.Agentic, HexModuleId.Notebooks, HexModuleId.Designer];
        foreach (string id in expected)
        {
            HexModuleDescriptor? descriptor = HexModuleCatalog.Find(id);
            Assert.NotNull(descriptor);
            Assert.Equal($"module_{id}", descriptor.SettingKey);
            Assert.True(descriptor.DefaultEnabled);
        }

        HexModuleDescriptor notebooks = HexModuleCatalog.Find("NOTEBOOKS")!;
        Assert.Equal("Hex.Notebooks.Render", notebooks.NativeLibrary);
        Assert.Contains("Hex.Notebooks.Render", notebooks.OwnedNativeFiles);
        Assert.True(HexModuleCatalog.Find(HexModuleId.Agentic)!.RestartRequired);
        Assert.False(HexModuleCatalog.Find(HexModuleId.Link)!.RestartRequired);
    }

    [Fact]
    public void Runtime_gating_respects_cli_forced_off_overrides()
    {
        Assert.True(HexModules.IsEnabled(HexModuleId.Link));
        Assert.True(HexModules.IsEnabled(HexModuleId.Notebooks));

        HexModules.SetEnabled(HexModuleId.Designer, false);
        Assert.False(HexModules.IsEnabled(HexModuleId.Designer));
        HexModules.SetEnabled(HexModuleId.Designer, true);
        Assert.True(HexModules.IsEnabled(HexModuleId.Designer));

        HexModules.ApplyArguments(["--no-module", "link", "--no-module=notebooks,local_ai"]);
        Assert.False(HexModules.IsEnabled(HexModuleId.Link));
        Assert.False(HexModules.IsEnabled(HexModuleId.Notebooks));
        Assert.False(HexModules.IsEnabled(HexModuleId.LocalAi));
        Assert.True(HexModules.IsEnabled(HexModuleId.Agentic));

        // CLI overrides always beat settings toggles
        HexModules.SetEnabled(HexModuleId.Link, true);
        Assert.False(HexModules.IsEnabled(HexModuleId.Link));
    }

    [Fact]
    public void Settings_round_trip_module_flags_as_user_only_and_sync_to_runtime()
    {
        string[] keys = ["module_link", "module_local_ai", "module_agentic", "module_notebooks", "module_designer"];
        foreach (string key in keys)
        {
            Assert.False(HexSettings.IsWorkspaceKey(key));
            Assert.Contains(EditorSettingsCatalog.Settings, row => row.Key == key && row.Group == EditorSettingsCatalog.MODULES);
            Assert.Equal(Values.Bool(true), EditorSettingsCatalog.Usual[key]);
        }

        string folder = Path.Combine(Path.GetTempPath(), "hex-modules-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "settings.hor");
            var written = new HexSettings(file) { ModuleNotebooks = false, ModuleDesigner = false };
            Assert.Null(written.Save());

            var read = new HexSettings(file);
            Assert.True(read.ModuleLink);
            Assert.True(read.ModuleLocalAi);
            Assert.True(read.ModuleAgentic);
            Assert.False(read.ModuleNotebooks);
            Assert.False(read.ModuleDesigner);

            read.ApplyModules();
            Assert.True(HexModules.IsEnabled(HexModuleId.Link));
            Assert.False(HexModules.IsEnabled(HexModuleId.Notebooks));
            Assert.False(HexModules.IsEnabled(HexModuleId.Designer));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Missing_native_module_degrades_gracefully_without_throwing()
    {
        string emptyDir = Path.Combine(Path.GetTempPath(), "hex-missing-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDir);
        try
        {
            const string missingName = "Hex.Missing.TestModule.DoesNotExist";
            Assert.False(HexNativeModule.ExistsOnDisk(missingName, emptyDir));
            HexNativeModule loaded = HexNativeModule.TryLoad(missingName, emptyDir);
            Assert.False(loaded.IsLoaded);
            Assert.Null(loaded.ResolvedPath);
            Assert.False(string.IsNullOrWhiteSpace(loaded.LoadError));
            Assert.False(loaded.TryGetExport("hex_nb_abi_version", out nint addr));
            Assert.Equal(0, addr);
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }

    [Fact]
    public void Host_registry_deactivates_disabled_hosts_and_ticks_only_active_ones()
    {
        var activeHost = new FakeModuleHost(HexModuleId.Link, active: true);
        var inactiveHost = new FakeModuleHost(HexModuleId.Designer, active: false);
        HexModules.RegisterHost(activeHost);
        HexModules.RegisterHost(inactiveHost);

        HexModules.TickActiveHosts(0.016f);
        Assert.Equal(1, activeHost.Ticks);
        Assert.Equal(0, inactiveHost.Ticks);

        HexModules.SetEnabled(HexModuleId.Link, false);
        HexModules.ReconcileHosts();
        Assert.False(activeHost.IsActive);
        Assert.Equal(1, activeHost.Deactivations);

        HexModules.TickActiveHosts(0.016f);
        Assert.Equal(1, activeHost.Ticks);
    }

    [Fact]
    public void Disabled_notebooks_module_blocks_rendering_and_returns_null_for_export_math()
    {
        HexModules.SetEnabled(HexModuleId.Notebooks, false);
        Assert.Throws<InvalidOperationException>(() => NotebookRender.Svg("<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'/>", 1.0f));
        Assert.Throws<InvalidOperationException>(() => NotebookRender.Math("E=mc^2", 1.0f));
        Assert.Throws<InvalidOperationException>(() => NotebookRender.MarkdownMath("$E=mc^2$", 1.0f));
        Assert.Null(NotebookRender.MathSvg("E=mc^2", display: false));
    }

    [Fact]
    public void Pending_restart_helpers_detect_saved_changes_and_ignore_forced_off_modules()
    {
        // Active session defaults all 5 modules to enabled
        var settings = new HexSettings(Path.Combine(Path.GetTempPath(), "hex-pending-restart-" + Guid.NewGuid().ToString("N") + ".hor"));
        Assert.False(HexModules.HasPendingModuleRestart(settings));
        Assert.Equal(0, HexModules.PendingModuleRestartCount(settings));
        Assert.False(HexModules.ModuleKeyNeedsRestart("module_agentic", true));
        Assert.False(HexModules.ModuleKeyNeedsRestart("not_a_module_key", false));

        // Flipping saved settings without mutating live HexModules reports pending restart
        settings.ModuleAgentic = false;
        settings.ModuleDesigner = false;
        Assert.True(HexModules.HasPendingModuleRestart(settings));
        Assert.Equal(2, HexModules.PendingModuleRestartCount(settings));
        Assert.True(HexModules.IsConfiguredDifferent(HexModuleId.Agentic, false));
        Assert.True(HexModules.ModuleKeyNeedsRestart("module_agentic", false));
        Assert.True(HexModules.ModuleKeyNeedsRestart("module_designer", false));
        Assert.False(HexModules.ModuleKeyNeedsRestart("module_link", true));

        // Callback overload matches HexSettings count
        int callbackCount = HexModules.PendingRestartCount(key => key switch
        {
            "module_agentic" => settings.ModuleAgentic,
            "module_designer" => settings.ModuleDesigner,
            _ => true
        });
        Assert.Equal(2, callbackCount);

        // CLI-forced-off modules are ignored by pending restart checks
        HexModules.ForceOff(HexModuleId.Link);
        Assert.True(HexModules.IsForcedOff(HexModuleId.Link));
        Assert.False(HexModules.IsConfiguredDifferent(HexModuleId.Link, true));
        Assert.False(HexModules.IsConfiguredDifferent(HexModuleId.Link, false));

        // Restoring saved values back to active session state clears pending restart
        settings.ModuleAgentic = true;
        settings.ModuleDesigner = true;
        Assert.False(HexModules.HasPendingModuleRestart(settings));
        Assert.Equal(0, HexModules.PendingModuleRestartCount(settings));
    }

    [Fact]
    public void Restart_uses_injectable_launcher_and_closes_only_when_launch_succeeds()
    {
        (HexMode Mode, string? Folder)? launched = null;
        int closeCalls = 0;
        try
        {
            HexModes.RestartLauncher = (mode, folder) =>
            {
                launched = (mode, folder);
                return true;
            };

            Assert.True(HexModes.Restart(HexMode.Dawn, @"C:\workspace\demo", () => closeCalls++));
            Assert.Equal((HexMode.Dawn, @"C:\workspace\demo"), launched);
            Assert.Equal(1, closeCalls);

            // When launch fails, window close must not be invoked
            HexModes.RestartLauncher = (mode, folder) =>
            {
                launched = (mode, folder);
                return false;
            };

            Assert.False(HexModes.Restart(HexMode.Dusk, null, () => closeCalls++));
            Assert.Equal((HexMode.Dusk, (string?)null), launched);
            Assert.Equal(1, closeCalls);
        }
        finally
        {
            HexModes.RestartLauncher = null;
        }
    }

    private sealed class FakeModuleHost(string id, bool active) : IHexModuleHost
    {
        public string Id => id;
        public bool IsActive { get; private set; } = active;
        public int Ticks { get; private set; }
        public int Deactivations { get; private set; }

        public void Activate() => IsActive = true;
        public void Deactivate() { IsActive = false; Deactivations++; }
        public void Tick(float dt) => Ticks++;
    }
}
