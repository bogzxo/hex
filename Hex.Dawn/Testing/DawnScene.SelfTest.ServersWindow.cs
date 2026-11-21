using System.Text.Json.Nodes;

using Hex.Core;

using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test of the Language Servers window, with the commands it shows really run. A folder with Python, TypeScript
// and a lone C# file: the Overview finds the three and leads with what's left, Python's page says it has no environment
// with the command that makes one, Run makes it (and tsconfig.json waits its turn behind it, then is made too), a
// command that fails says so with its log and Try Again, a server's own setting is changed and reset, and the status bar
// says Set up for a language that has something left. Pictures of every page when HEX_SELFTEST_SHOTS is set.
internal sealed partial class DawnScene
{
    private void TestServersWindow(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "servers_ws");
        string python = Path.Combine(folder, "main.py");
        HexServerEntry py = null!, ts = null!, cs = null!, html = null!;

        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(python, "import os\nprint(os.getcwd())\n");
            File.WriteAllText(Path.Combine(folder, "app.ts"), "export const answer: number = 42;\n");
            File.WriteAllText(Path.Combine(folder, "Program.cs"), "System.Console.WriteLine(\"hi\");\n");
            explorer.OpenFolder(folder);
            Open(python);
            py = languages.ServerEntry("python")!;
            ts = languages.ServerEntry("typescript")!;
            cs = languages.ServerEntry("csharp")!;
            html = languages.ServerEntry("html")!;
            OpenLanguageServers();
        });

        // Everything looked for, the tools and the servers, before anything's said of them
        bool Settled(HexServerEntry entry) => !languages.SetupStepsFor(entry).Any(step => step.State == SetupState.Checking);
        test.WaitFor(() => !languages.SetupBusy && Settled(py) && Settled(ts) && Settled(cs), 60.0f);

        // What's on the page, every label's words
        string[] Words() => serversPage is null ? [] : [.. Descendants(serversPage).OfType<Label>().Select(label => label.Text)];
        Button? ButtonOn(string label) => serversPage is null ? null : Descendants(serversPage).OfType<Button>().FirstOrDefault(button => button.Label == label);

        test.Check("servers window: it opens at Overview, and the workspace's languages are Python, TypeScript and C#", () =>
            serversWindow is not null && serversShown == ServersPage.Overview
            && languages.WorkspaceEntries().Select(entry => entry.Name).ToHashSet() is var here && here.Contains("python") && here.Contains("typescript") && here.Contains("csharp"));
        test.Check("servers window: Overview leads with what's left, at display size, and has a row for each language", () =>
        {
            var heading = Descendants(serversPage!).OfType<Label>().FirstOrDefault(label => label.TextScale == SERVERS_DISPLAY);
            // Python has no environment yet, so what's left names it, and the heading isn't the one said while looking
            return heading is not null && heading.Text != Said("server_window.checking") && heading.Text != Said("server_window.ready")
                && heading.Text.Contains(languages.LanguageNameOf(py))
                && Words().Any(text => text.Contains(languages.LanguageNameOf(py))) && Words().Any(text => text.Contains(languages.LanguageNameOf(cs)));
        });
        test.Check("servers window: the status bar says Set up for Python, which has no environment yet", () => setupItemFor == "python");
        test.Run(() => Shot("servers-overview"));

        // Python: its environment a step of the project, the command shown that makes one, in the workspace's folder
        test.Run(() => ShowServersPage(ServersPage.Language, "python"));
        SetupStep Step(HexServerEntry entry, string key) => languages.SetupStepsFor(entry).Single(step => step.Key == key);
        string venv = $"{(OperatingSystem.IsWindows() ? "python" : "python3")} -m venv .venv";
        test.Check("servers window: Python's page has its environment as a step to do, with the command and where it runs", () =>
            Step(py, "project:python:python-environment") is { State: SetupState.Needed or SetupState.Blocked, Command: var command, Folder: var at }
            && command == venv && at == folder && Words().Any(text => text.Contains(venv)));
        test.Check("servers window: every step shows its state as an icon and a word, and a needed one has Copy beside its command", () =>
            Words().Any(text => text.Contains(Said("server_setup.state_to_do")) || text.Contains(Said("server_setup.state_blocked")))
            && ButtonOn(Said("server_setup.copy")) is not null);
        test.Run(() => Shot("servers-python"));

        // Run it, with tsconfig.json queued behind; each made, one after the other
        bool pythonThere = HexServerCatalog.Locate(OperatingSystem.IsWindows() ? "python" : "python3") is not null;
        bool tscThere = HexServerCatalog.Locate("tsc") is not null;
        test.Run(() =>
        {
            if (pythonThere)
            {
                // Run whether or not node (pyright's) is there, the project's own step is python's
                languages.RunSetupStep(py, Step(py, "project:python:python-environment") with { State = SetupState.Needed });
                if (tscThere)
                    languages.RunSetupStep(ts, Step(ts, "project:typescript:tsconfig") with { State = SetupState.Optional });
            }
        });
        if (pythonThere)
        {
            test.Check("servers window: Run starts it, and a second step waits its turn behind it", () =>
                Step(py, "project:python:python-environment").State == SetupState.Running
                && (!tscThere || Step(ts, "project:typescript:tsconfig").State == SetupState.Queued));
            test.WaitFor(() => Step(py, "project:python:python-environment").State == SetupState.Ready, 120.0f);
            test.Check("servers window: the environment's made, the step's done, and its log says what ran", () =>
                Directory.Exists(Path.Combine(folder, ".venv")) && Step(py, "project:python:python-environment").State == SetupState.Ready
                && languages.SetupLog("project:python:python-environment").FirstOrDefault() == "> " + venv);
            if (tscThere)
            {
                test.WaitFor(() => Step(ts, "project:typescript:tsconfig").State == SetupState.Ready, 60.0f);
                test.Check("servers window: then the one waiting ran, tsconfig.json made", () => File.Exists(Path.Combine(folder, "tsconfig.json")));
            }
            test.Check("servers window: with nothing left of Python's project, the status bar has no Set up for it once its server's there", () =>
                HexServerSetup.LeftOf(languages.SetupStepsFor(py)) > 0 || setupItemFor is null);
        }

        // A command that fails: Try Again, and the log says why
        test.Run(() =>
        {
            ShowServersPage(ServersPage.Language, "csharp");
            var failing = new SetupStep("project:csharp:selftest-fail", SetupStepKind.Project, "selftest-fail", "Fails on purpose", string.Empty, SetupState.Needed,
                string.Empty, OperatingSystem.IsWindows() ? "echo going wrong && exit 3" : "echo going wrong; exit 3", null, false, null, folder);
            languages.RunSetupStep(cs, failing);
        });
        test.WaitFor(() => languages.SetupStateOf("project:csharp:selftest-fail") == SetupState.Failed, 30.0f);
        test.Check("servers window: a command that fails is Failed, with what it said and why in its log", () =>
            languages.SetupStateOf("project:csharp:selftest-fail") == SetupState.Failed
            && languages.SetupLog("project:csharp:selftest-fail") is var log && log.Any(line => line.Contains("going wrong")) && log.Any(line => line.Contains("3")));
        test.Run(() => Shot("servers-csharp"));

        // A server's own settings, changed and reset, and told to it
        test.Run(() =>
        {
            serversSettingsTab = true;
            ShowServersPage(ServersPage.Language, "html");
            serversSettingsTab = true;
            FillLanguageServers();
        });
        test.Check("servers window: HTML's Settings has its server's own settings, by their keys", () =>
            Words().Any(text => text.Contains("html.format.wrapLineLength")) && Words().Any(text => text.Contains(Said("server_settings.where"))));
        test.Run(() => Shot("servers-html-settings"));
        test.Check("servers window: a setting changed is kept, laid over the catalog's, and what the server's told", () =>
        {
            languages.SetServerSetting(html, "html.format.wrapLineLength", JsonValue.Create(100));
            JsonObject? told = languages.SettingsOf(html);
            return languages.ServerSettingChanged(html, "html.format.wrapLineLength") && told?["html"]?["format"]?["wrapLineLength"]?.ToJsonString() == "100"
                && told["html"]?["validate"]?["scripts"] is not null
                && LanguageServers.Registered.First(entry => entry.Name == "html").Settings?["html"]?["format"]?["wrapLineLength"]?.ToJsonString() == "100";
        });
        test.Check("servers window: and Reset puts the catalog's back, nothing of it kept", () =>
        {
            languages.SetServerSetting(html, "html.format.wrapLineLength", null);
            return !languages.ServerSettingChanged(html, "html.format.wrapLineLength") && !settings.ServerSettings.ContainsKey("html")
                && languages.SettingsOf(html)?["html"]?["format"]?["wrapLineLength"]?.ToJsonString() == "120";
        });

        // The other pages
        test.Run(() => ShowServersPage(ServersPage.All));
        test.Check("servers window: All languages has every server, the workspace's first", () =>
            Words().Any(text => text.Contains(Said("server_window.this_workspace"))) && languages.ServerEntries.All(entry => Words().Any(text => text.Contains(entry.Command))));
        test.Run(() => Shot("servers-all"));
        test.Run(() => ShowServersPage(ServersPage.Advanced));
        test.Check("servers window: Advanced has the update checks and the form that adds a server", () =>
            Words().Any(text => text.Contains(Said("language_settings.updates"))) && Words().Any(text => text.Contains(Said("language_settings.add_title"))));
        test.Run(() => Shot("servers-advanced"));

        test.Run(() =>
        {
            serversWindow?.Close();
            CloseNow();
            LanguageServers.StopAll();
        });
    }
}
