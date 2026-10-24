using Horizon.Core.Threading;

namespace Hex.Dawn;

// The self test of the catalog of extensions, on a catalog of its own (a folder laid out like hex-extensions) so it says
// the same with GitHub down or the real catalog changed. A sync lists what's in it, Install puts one in the extensions
// folder (the test's own) and starts it, a newer version in the catalog is offered as Update and the update reads it
// again, and a catalog that can't be synced says so and keeps what it had.
internal sealed partial class DawnScene
{
    private void TestCatalog(HexPointerScript test)
    {
        const string ID = "self_test_echo";
        string folder = Path.Combine(testFolder, "catalog");
        string source = Path.Combine(folder, "source"), file = Path.Combine(source, ID, "extension.hor");
        string oldSource = string.Empty, oldCatalog = string.Empty, oldOwn = string.Empty;
        WorkItem? going = null;

        static string Echo(string version) =>
            $"let extension = {{ name: \"Echo\", description: \"Says echo.\", version: \"{version}\", author: \"bogz\" }};\n" +
            "func on_start(hex) { hex.command(\"Echo\", func(hex) { hex.say(\"echo\"); }); }\n";

        HexExtensions.CatalogEntry? Entry() => extensions.Catalog.FirstOrDefault(entry => entry.Id == ID);

        test.Run(() =>
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, Echo("1.0.0"));

            oldSource = settings.ExtensionCatalog;
            oldCatalog = extensions.CatalogFolder;
            oldOwn = extensions.OwnFolder;
            settings.ExtensionCatalog = source;
            extensions.CatalogFolder = Path.Combine(folder, "synced");
            extensions.OwnFolder = Path.Combine(folder, "installed");
            going = extensions.SyncCatalog(asked: true);
        });
        test.WaitFor(() => going is { IsActive: false } && Entry() is not null, 10.0f);
        test.Check("catalog: a sync lists what's in it, not installed and offered Install", () =>
            Entry() is { Version: "1.0.0", Name: "Echo" } entry && extensions.InstalledOf(entry) is null && extensions.CatalogButton(entry) == Said("extension_catalog.install"));

        test.Run(() => OpenSettings(HexExtensions.AVAILABLE));
        test.Settle(0.3f);
        test.Check("catalog: Extensions, Available is in the settings", () => SettingsPage is not null);

        test.Run(() => going = extensions.Install(Entry()!));
        test.WaitFor(() => going is { IsActive: false } && extensions.InstalledOf(Entry()!) is not null, 10.0f);
        test.Check("catalog: Install puts it in the extensions folder and starts it", () =>
            extensions.InstalledOf(Entry()!) is { Enabled: true, Failed: null, BuiltIn: false, Version: "1.0.0" }
            && File.Exists(Path.Combine(folder, "installed", ID, "extension.hor")) && settings.CatalogExtensions.Contains(ID)
            && extensions.Commands().Any(command => command.Text.StartsWith("Echo: Echo", StringComparison.Ordinal)));
        test.Check("catalog: and then it has no button, it's up to date", () => extensions.CatalogButton(Entry()!) is null && !extensions.Behind().Any());

        test.Run(() =>
        {
            File.WriteAllText(file, Echo("1.1.0"));
            going = extensions.SyncCatalog(asked: true);
        });
        test.WaitFor(() => going is { IsActive: false } && Entry() is { Version: "1.1.0" }, 10.0f);
        test.Check("catalog: a newer version in the catalog is offered as Update", () =>
            extensions.Behind().Any(behind => behind.Entry.Id == ID) && extensions.CatalogButton(Entry()!) == Said("extension_catalog.update"));

        test.Run(() => going = extensions.Install(Entry()!));
        test.WaitFor(() => going is { IsActive: false } && extensions.InstalledOf(Entry()!) is { Version: "1.1.0" }, 10.0f);
        test.Check("catalog: and the update reads it again, the new version running", () =>
            extensions.InstalledOf(Entry()!) is { Enabled: true, Failed: null, Version: "1.1.0" } && !extensions.Behind().Any());

        test.Run(() =>
        {
            settings.ExtensionCatalog = Path.Combine(folder, "not there", "at all");
            going = extensions.SyncCatalog(asked: false);
        });
        test.WaitFor(() => going is { IsActive: false }, 10.0f);
        test.Check("catalog: one that can't be synced fails and keeps what it had", () => going is { State: WorkState.Failed } && Entry() is { Version: "1.1.0" });

        test.Run(() =>
        {
            CloseSettings();
            if (extensions.All.FirstOrDefault(extension => extension.Id == ID) is { } echo)
            {
                extensions.Switch(echo, false);
                extensions.Remove(echo);
            }

            settings.CatalogExtensions.Remove(ID);
            settings.DisabledExtensions.Remove(ID);
            settings.ExtensionCatalog = oldSource;
            extensions.CatalogFolder = oldCatalog;
            extensions.OwnFolder = oldOwn;
            SaveSettings();
        });
        test.Wait();
    }
}
