using Horizon.Core.Threading;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    // Opt-in, real npm but no network or changes to the user's global packages.
    private void TestNpmInstall(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "npm install & proof");
        string package = Path.Combine(root, "fixture package"), prefix = Path.Combine(root, "local prefix"), cache = Path.Combine(root, "cache");
        string installed = OperatingSystem.IsWindows() ? Path.Combine(prefix, "hex-npm-fixture.cmd") : Path.Combine(prefix, "bin", "hex-npm-fixture");
        string line = $"npm install --global --prefix \"{prefix}\" --cache \"{cache}\" --offline --ignore-scripts --no-audit --no-fund \"{package}\"";
        var entry = new HexServerEntry("npm-install-selftest", installed, [], "npm-selftest", [".npmselftestnever"], "", line, line, "", null);
        WorkItem? install = null;
        test.Run(() =>
        {
            Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, "package.json"), """{"name":"hex-npm-fixture","version":"1.0.0","bin":{"hex-npm-fixture":"server.js"}}""");
            File.WriteAllText(Path.Combine(package, "server.js"), "#!/usr/bin/env node\nconsole.log('hex-npm-fixture 1.0.0');\n");
            languages.InstallServer(entry);
            install = languages.Installing(entry);
        });
        test.WaitFor(() => install is not null && !install.IsActive, 60);
        test.Check("npm install: real offline install succeeds through Hex's Install action", () => install?.State == WorkState.Succeeded);
        test.Check("npm install: a global-style bin shim is created in the isolated prefix", () => File.Exists(installed));
        test.Check("npm install: installer output is retained", () => install is { Output.Count: > 0 });
        test.Check("npm install: the installed server is discoverable", () => HexServerCatalog.Locate(installed) is not null);
        Task<string?>? version = null;
        test.Run(() => version = Task.Run(() => HexServerCatalog.VersionOf(installed, ["--version"])));
        test.WaitFor(() => version?.IsCompleted == true, 10);
        test.Check("npm install: the installed npm shim runs through the server launcher", () => version is { IsCompletedSuccessfully: true, Result: "1.0.0" });

        string terminalPrefix = Path.Combine(root, "terminal prefix");
        string terminalBin = OperatingSystem.IsWindows() ? Path.Combine(terminalPrefix, "hex-npm-fixture.cmd") : Path.Combine(terminalPrefix, "bin", "hex-npm-fixture");
        Horizon.UI.Components.TerminalTab? tab = null;
        test.Run(() => { ShowPanel(CONSOLE_PAGE); tab = terminalPanel.NewTab(terminals.UsualProfile, testFolder); });
        test.WaitFor(() => terminalPanel.FocusedView?.Screen.AtPrompt == true, 15);
        test.Run(() => terminalPanel.FocusedView?.Send(line.Replace(prefix, terminalPrefix, StringComparison.Ordinal) + "\r"));
        test.WaitFor(() => File.Exists(terminalBin) && terminalPanel.FocusedView?.Screen.AtPrompt == true, 60);
        test.Check("npm install: typing the same install in Hex's terminal succeeds", () => File.Exists(terminalBin));
        test.Run(() => { if (tab is not null) terminalPanel.CloseTab(tab); });
        test.Wait();
    }
}
