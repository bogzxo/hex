using System.Text.Json.Nodes;

using Horizon.Core.Threading;

namespace Hex.Dawn;

// What the Language Servers window shows of a language and what its buttons do. The steps themselves are worked out by
// HexServerSetup from what's found here: the tools of language_servers.hor looked for the way the servers are (off the
// editor's thread, a version each), the servers as HexLanguages.Updates finds them, and the folder on screen looked at
// for what its project needs. A step's command runs one at a time behind any other, its output kept as the step's log,
// and once it's done everything it could have changed is looked for again, so the next step unblocks by itself.
internal sealed partial class HexLanguages
{
    private const string TOOL_WORK = "servers:tools", SETUP_WORK = "setup:";

    // The tools language_servers.hor names, read with it
    private Dictionary<string, HexTool> setupTools = new(StringComparer.OrdinalIgnoreCase);

    // What was found of each tool, and the ones being looked for
    private readonly Dictionary<string, SetupFound> toolsFound = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> toolsProbing = new(StringComparer.OrdinalIgnoreCase);

    // The steps whose commands are going, waiting their turn, or ended badly, by key, and what each said
    private readonly Dictionary<string, SetupState> setupRunning = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> setupLogs = new(StringComparer.Ordinal);
    private readonly List<(SetupStep Step, HexServerEntry Entry)> setupQueue = [];
    private string? setupGoing;

    // The languages Set up everything is seeing through, each step that becomes runnable queued as it does
    private readonly HashSet<string> setupAll = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Opens the Language Servers window at a language (null for its Overview), the scene's to say.</summary>
    internal Action<string?>? OpenSetup { get; set; }

    /// <summary>Goes up whenever anything a step shows changed, for the window to make its rows again.</summary>
    internal int SetupVersion { get; private set; }

    private void SetupChanged()
    {
        SetupVersion++;
        context.Wake();
    }

    /// <summary>The tools language_servers.hor names, read the first time they're wanted.</summary>
    internal IReadOnlyDictionary<string, HexTool> SetupTools
    {
        get
        {
            if (setupTools.Count == 0 && File.Exists(LANGUAGE_SERVERS))
            {
                try { setupTools = HexServerSetup.ReadTools(File.ReadAllText(LANGUAGE_SERVERS)); }
                catch (Exception e) { Console.WriteLine($"[Hex] {LANGUAGE_SERVERS}'s tools weren't read, {e.Message}"); }
            }
            return setupTools;
        }
    }

    /// <summary>
    /// Looks for every tool (or those named) and every server again, off the editor's thread, as the window opens and
    /// after a step's command ends: what was installed is found, the PATH read again from the system as it starts.
    /// </summary>
    internal void ProbeSetup(IEnumerable<string>? tools = null, IEnumerable<HexServerEntry>? servers = null)
    {
        foreach (string server in (servers ?? ServerEntries).Select(entry => entry.Name))
            serversFound.Remove(server);
        ProbeServers(servers ?? ServerEntries, SetupChanged);

        var looking = (tools ?? SetupTools.Keys).Where(name => SetupTools.ContainsKey(name) && toolsProbing.Add(name)).Select(name => SetupTools[name]).ToList();
        if (looking.Count == 0)
            return;
        foreach (HexTool tool in looking)
            toolsFound.Remove(tool.Name);
        SetupChanged();

        var found = new List<(string Name, SetupFound Found)>();
        context.Work.Start(TOOL_WORK + string.Join(',', looking.Select(tool => tool.Name)), context.Strings.Said("server_setup.looking"), work =>
        {
            Horizon.Core.Processes.ProcessEnvironment.WaitForLoginShell();
            for (int i = 0; i < looking.Count; i++)
            {
                HexTool tool = looking[i];
                work.Report((float)i / looking.Count, tool.Title);
                string? path = HexServerCatalog.Locate(tool.Command);
                string? version = path is not null && tool.Arguments.Length > 0 ? HexServerCatalog.VersionOf(path, tool.Arguments) : null;
                found.Add((tool.Name, new SetupFound(path, version)));
            }
        }, _ =>
        {
            foreach (HexTool tool in looking)
                toolsProbing.Remove(tool.Name);
            foreach (var (name, what) in found)
                toolsFound[name] = what;
            SetupChanged();
            ContinueSetupAll();
        }, seen: false, cancellable: false);
    }

    /// <summary>The folder a language's project steps are about, the workspace's first folder, null with none open.</summary>
    internal string? SetupFolder => context.Workspace.Folders.FirstOrDefault();

    /// <summary>A language's steps as they stand now.</summary>
    internal List<SetupStep> SetupStepsFor(HexServerEntry entry)
    {
        SetupFound? server = serversProbing.Contains(entry.Name) || !serversFound.TryGetValue(entry.Name, out ServerFound? found)
            ? null : new SetupFound(found.Path, found.Version);
        return HexServerSetup.StepsFor(entry, SetupTools, name => toolsFound.GetValueOrDefault(name), server, NewerServerVersion(entry),
            key => setupRunning.TryGetValue(key, out SetupState state) ? state : null, ProjectFacts(entry), entry.ReleasesPage);
    }

    /// <summary>What a step's command said the last time it ran, nothing before it has.</summary>
    internal IReadOnlyList<string> SetupLog(string key) =>
        // While it goes, what it's said so far
        context.Work.Find(SETUP_WORK + key) is { } going ? [.. setupLogs.GetValueOrDefault(key) ?? [], .. going.Output]
            : setupLogs.GetValueOrDefault(key) ?? [];

    /// <summary>What's going on with a step's command, Running, Queued or Failed, null for nothing.</summary>
    internal SetupState? SetupStateOf(string key) => setupRunning.TryGetValue(key, out SetupState state) ? state : null;

    /// <summary>How far a step's command has got, null when it isn't going or doesn't say.</summary>
    internal float? SetupProgress(string key) => context.Work.Find(SETUP_WORK + key)?.Progress;

    // How long what a folder has is kept before it's looked at again, the window asks for it every time it's drawn
    private const long FOLDER_LOOK_MS = 2000;
    private readonly Dictionary<string, (long At, HashSet<string> Extensions, HashSet<string> Names)> folderLooks = new(PlatformPaths.Comparer);

    // The extensions and the file names a folder has, a few folders down, the build's and the hidden ones left out
    private (HashSet<string> Extensions, HashSet<string> Names) FolderHas(string folder)
    {
        long now = Environment.TickCount64;
        if (folderLooks.TryGetValue(folder, out var known) && now - known.At < FOLDER_LOOK_MS)
            return (known.Extensions, known.Names);

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Look(string at, int depth)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(at))
                {
                    if (depth == 0)
                        names.Add(Path.GetFileName(file));
                    extensions.Add(Path.GetExtension(file));
                }
                if (depth >= 3)
                    return;
                foreach (string inside in Directory.EnumerateDirectories(at))
                {
                    string name = Path.GetFileName(inside);
                    if (name.StartsWith('.') || name is "bin" or "obj" or "node_modules" or "target" or "build" or "out" or "dist" or "__pycache__")
                    {
                        if (depth == 0)
                            names.Add(name + "/");
                        continue;
                    }
                    if (depth == 0)
                        names.Add(name + "/");
                    Look(inside, depth + 1);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        Look(folder, 0);
        folderLooks[folder] = (now, extensions, names);
        return (extensions, names);
    }

    /// <summary>
    /// The languages the workspace is written in, by the files it has and the files open, the ones with most files first.
    /// Nothing open, none.
    /// </summary>
    internal List<HexServerEntry> WorkspaceEntries()
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (SetupFolder is { } folder)
            extensions.UnionWith(FolderHas(folder).Extensions);
        foreach (HexDocument open in context.Documents.All)
            if (open.Path is { } path)
                extensions.Add(Path.GetExtension(path));

        return [.. ServerEntries.Where(entry => entry.Extensions.Any(extensions.Contains))];
    }

    // What the project of the workspace needs for a server, by the checks its entry names
    private List<ProjectFact> ProjectFacts(HexServerEntry entry)
    {
        var facts = new List<ProjectFact>();
        if (entry.Project is not { Length: > 0 } checks || SetupFolder is not { } folder)
            return facts;

        var (extensions, names) = FolderHas(folder);
        bool Has(string extension) => extensions.Contains(extension);
        string python = OperatingSystem.IsWindows() ? "python" : "python3";
        string S(string key, params (string, string)[] args) => context.Strings.Said("server_setup." + key, args);

        foreach (string check in checks)
        {
            switch (check)
            {
                case "dotnet-project":
                {
                    bool applies = Has(".cs") || Has(".fs");
                    bool done = HasWhatItNeeds(entry, folder);
                    facts.Add(new(check, S("dotnet_project"), S("dotnet_project_about"), applies, done,
                        done ? S("dotnet_project_done") : S("dotnet_project_missing"), "dotnet new console", folder));
                    break;
                }
                case "dotnet-restore":
                {
                    bool applies = HasWhatItNeeds(entry, folder);
                    string? target = applies ? Unrestored(folder) : null;
                    facts.Add(new(check, S("dotnet_restore"), S("dotnet_restore_about"), applies, target is null,
                        target is null ? S("dotnet_restore_done") : S("dotnet_restore_missing", ("name", Path.GetFileName(target))),
                        target is null ? $"dotnet restore" : $"dotnet restore \"{Path.GetFileName(target)}\"", folder));
                    break;
                }
                case "python-environment":
                {
                    string? local = PythonEnvironment.FindLocal(folder, folder);
                    string? picked = PythonEnvironment.Selected(folder);
                    facts.Add(new(check, S("python_environment"), S("python_environment_about"), Has(".py"), local is not null || picked is not null,
                        picked ?? local ?? S("python_environment_missing"), $"{python} -m venv .venv", folder));
                    break;
                }
                case "compile-commands":
                {
                    bool cmake = names.Contains("CMakeLists.txt");
                    bool done = names.Contains("compile_commands.json") || File.Exists(Path.Combine(folder, "build", "compile_commands.json"));
                    facts.Add(new(check, S("compile_commands"), S("compile_commands_about"), cmake, done,
                        done ? S("compile_commands_done") : S("compile_commands_missing"), "cmake -S . -B build -DCMAKE_EXPORT_COMPILE_COMMANDS=ON", folder));
                    break;
                }
                case "tsconfig":
                {
                    bool done = names.Contains("tsconfig.json") || names.Contains("jsconfig.json");
                    facts.Add(new(check, S("tsconfig"), S("tsconfig_about"), Has(".ts") || Has(".tsx"), done,
                        done ? S("tsconfig_done") : S("tsconfig_missing"), "tsc --init", folder, Optional: true));
                    break;
                }
                case "go-module":
                {
                    string module = new([.. Path.GetFileName(folder.TrimEnd('/', '\\')).ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.')]);
                    bool done = names.Contains("go.mod") || names.Contains("go.work");
                    facts.Add(new(check, S("go_module"), S("go_module_about"), Has(".go"), done,
                        done ? S("go_module_done") : S("go_module_missing"), $"go mod init {(module.Length > 0 ? module : "example")}", folder));
                    break;
                }
            }
        }

        return facts;
    }

    /// <summary>
    /// Runs a step's command, now, or after the one that's going: its output kept as its log, and once it's ended the
    /// tools and the servers looked for again. A server's step stops the server first (a program that's running can't be
    /// written over) and starts it again after.
    /// </summary>
    internal void RunSetupStep(HexServerEntry entry, SetupStep step)
    {
        if (step.Command is not { Length: > 0 } || setupRunning.GetValueOrDefault(step.Key) is SetupState.Running or SetupState.Queued)
            return;
        if (step.Kind == SetupStepKind.Server && HexHost.NoDownloads(entry.Name) is { } why)
        {
            context.Notices.Say(why, error: true);
            return;
        }

        setupLogs.Remove(step.Key);
        if (setupGoing is not null)
        {
            setupRunning[step.Key] = SetupState.Queued;
            setupQueue.Add((step, entry));
            SetupChanged();
            return;
        }

        StartSetupStep(entry, step);
    }

    private void StartSetupStep(HexServerEntry entry, SetupStep step)
    {
        string line = step.Command!;
        setupGoing = step.Key;
        setupRunning[step.Key] = SetupState.Running;
        setupLogs[step.Key] = [$"> {line}"];
        SetupChanged();

        Task stopped = step.Kind == SetupStepKind.Server ? StopServer(entry) : Task.CompletedTask;
        context.Work.Start(SETUP_WORK + step.Key, context.Strings.Said("server_setup.running", ("title", step.Title)), async work =>
        {
            await stopped.WaitAsync(work.Token).ConfigureAwait(false);
            work.Say(line);
            int code = await RunCommandLine(line, work, step.Folder).ConfigureAwait(false);
            if (code != 0)
                throw new InvalidOperationException(context.Strings.Said("server_setup.ended_with", ("code", code.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }, item =>
        {
            List<string> log = setupLogs.GetValueOrDefault(step.Key) ?? [];
            log.AddRange(item.Output);
            if (item.State == WorkState.Failed && item.Error is { } error)
                log.Add(error);
            setupLogs[step.Key] = log;

            setupGoing = null;
            if (item.State == WorkState.Succeeded)
                setupRunning.Remove(step.Key);
            else
            {
                setupRunning[step.Key] = SetupState.Failed;
                setupAll.Remove(entry.Name);
            }

            // What's there now, every tool and the servers that could have changed, and the project looked at again
            folderLooks.Clear();
            if (step.Kind == SetupStepKind.Server)
                ProbeServers([entry], () => { RestartServer(entry); SetupChanged(); });
            ProbeSetup(step.Kind == SetupStepKind.Environment ? [step.Name] : [], step.Kind == SetupStepKind.Environment
                ? ServerEntries.Where(other => (other.Tools ?? []).Contains(step.Name, StringComparer.OrdinalIgnoreCase)) : []);
            if (step.Kind == SetupStepKind.Project)
                RestartServer(entry);

            if (setupQueue.Count > 0)
            {
                var (nextStep, nextEntry) = setupQueue[0];
                setupQueue.RemoveAt(0);
                StartSetupStep(nextEntry, nextStep);
            }
            else
                ContinueSetupAll();
            SetupChanged();
        }, seen: false, cancellable: true);
    }

    /// <summary>The step whose command is going stopped, the steps waiting behind it let go of.</summary>
    internal void CancelSetup(string key)
    {
        if (setupRunning.GetValueOrDefault(key) == SetupState.Queued)
        {
            setupQueue.RemoveAll(queued => queued.Step.Key == key);
            setupRunning.Remove(key);
            SetupChanged();
            return;
        }
        context.Work.Find(SETUP_WORK + key)?.Cancel();
        setupAll.Clear();
    }

    /// <summary>
    /// Set up everything: every step of these languages that can run, run in order, and the ones that couldn't yet run
    /// as soon as the steps they wait for are done. It stops at a step that needs somebody (a download).
    /// </summary>
    internal void SetupEverything(IEnumerable<HexServerEntry> entries)
    {
        foreach (HexServerEntry entry in entries)
            setupAll.Add(entry.Name);
        ContinueSetupAll();
    }

    /// <summary>Whether a step's command is going or anything's being looked for, for the window to follow it more often.</summary>
    internal bool SetupBusy => setupGoing is not null || toolsProbing.Count > 0 || serversProbing.Count > 0;

    /// <summary>Whether a step's command is going, its log and its bar moving.</summary>
    internal bool SetupRunning => setupGoing is not null;

    /// <summary>Whether Set up everything is going.</summary>
    internal bool SettingUpAll => setupAll.Count > 0 && (setupGoing is not null || setupQueue.Count > 0);

    private void ContinueSetupAll()
    {
        if (setupAll.Count == 0 || setupGoing is not null)
            return;

        bool queued = false;
        foreach (string name in setupAll.ToList())
        {
            if (ServerEntry(name) is not { } entry)
                continue;
            var steps = SetupStepsFor(entry);
            if (steps.Any(step => step.State == SetupState.Checking))
            {
                queued = true;
                continue;
            }
            List<SetupStep> runnable = HexServerSetup.RunnableInOrder(steps);
            if (runnable.Count == 0)
            {
                setupAll.Remove(name);
                continue;
            }
            foreach (SetupStep step in runnable.Where(step => step.State != SetupState.Failed))
            {
                RunSetupStep(entry, step);
                queued = true;
            }
        }

        if (!queued)
            setupAll.Clear();
    }

    /* A server's own settings, laid over its catalog's */

    /// <summary>What a server is told: its catalog's settings with the user's changes laid over them, null for nothing.</summary>
    internal JsonObject? SettingsOf(HexServerEntry entry)
    {
        JsonObject? told = settings.ServerSettings.GetValueOrDefault(entry.Name);
        if (told is null || told.Count == 0)
            return entry.Settings;
        var merged = entry.Settings?.DeepClone().AsObject() ?? new JsonObject();
        Lay(merged, told);
        return merged;

        static void Lay(JsonObject into, JsonObject over)
        {
            foreach (var (key, value) in over)
            {
                if (value is JsonObject inside && into[key] is JsonObject already)
                    Lay(already, inside);
                else
                    into[key] = value?.DeepClone();
            }
        }
    }

    /// <summary>One of a server's settings changed by its dotted path ("html.format.wrapLineLength"), null putting the catalog's back.</summary>
    internal void SetServerSetting(HexServerEntry entry, string path, JsonNode? value)
    {
        string[] parts = path.Split('.');
        if (!settings.ServerSettings.TryGetValue(entry.Name, out JsonObject? told))
            settings.ServerSettings[entry.Name] = told = new JsonObject();

        JsonObject at = told;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (at[parts[i]] is not JsonObject inside)
            {
                if (value is null)
                    return;
                at[parts[i]] = inside = new JsonObject();
            }
            at = inside;
        }

        if (value is null)
            at.Remove(parts[^1]);
        else
            at[parts[^1]] = value;
        Prune(told);
        if (told.Count == 0)
            settings.ServerSettings.Remove(entry.Name);
        context.Settings.Save();

        // Said to every server of it that's running, the way it's told as it starts, and registered so the next one starts with it
        LanguageServers.UpdateSettings(entry.Name, SettingsOf(entry));
        foreach (LanguageServer server in LanguageServers.Running.Where(server => server.Name == entry.Name && server.Failure is null))
            server.ChangeSettings(SettingsOf(entry));
        SetupChanged();

        static void Prune(JsonObject json)
        {
            foreach (var (key, value) in json.ToList())
            {
                if (value is JsonObject inside)
                {
                    Prune(inside);
                    if (inside.Count == 0)
                        json.Remove(key);
                }
            }
        }
    }

    /// <summary>Whether one of a server's settings was changed from its catalog's.</summary>
    internal bool ServerSettingChanged(HexServerEntry entry, string path)
    {
        JsonNode? at = settings.ServerSettings.GetValueOrDefault(entry.Name);
        foreach (string part in path.Split('.'))
            at = at is JsonObject inside ? inside[part] : null;
        return at is not null;
    }

    /// <summary>Starts a language's server again, every open file of its in the new one.</summary>
    internal void RestartLanguage(HexServerEntry entry) => RestartServer(entry);

    /* What the window's pages change, the way the settings did */

    /// <summary>Where a server is set to be, empty for its command on the PATH.</summary>
    internal string ServerPathOf(HexServerEntry entry) => settings.ServerPaths.GetValueOrDefault(entry.Name, string.Empty);

    /// <summary>Where a server is, set, empty for its command on the PATH; the one it was stopped and this one looked for.</summary>
    internal void SetServerPath(HexServerEntry entry, string path)
    {
        new ServerSource(this).Set(SERVER_KEY + entry.Name, new Horizon.HIDL.Runtime.StringValue(path));
        SetupChanged();
    }

    /// <summary>Where a server is, picked with Hex's own dialog.</summary>
    internal void ChooseServerFile(HexServerEntry entry) => ChooseServer(entry);

    /// <summary>The form that adds a server for a language Hex colours, the one the settings had.</summary>
    internal UIComponent AddServerForm() => MakeServerEditor(rows: false);

    /// <summary>Whether Hex looks for newer servers as it starts, and Check now.</summary>
    internal bool ServerUpdates
    {
        get => settings.ServerUpdates;
        set
        {
            settings.ServerUpdates = value;
            context.Settings.Save();
        }
    }

    internal void CheckForServerUpdates() => CheckServerUpdates(asked: true);

    /// <summary>The colours C# and TypeScript are drawn in, resharper, visual_studio or skin.</summary>
    internal string CodeColors
    {
        get => settings.CSharpColors;
        set
        {
            if (value is not ("resharper" or "visual_studio" or "skin") || value == settings.CSharpColors)
                return;
            settings.CSharpColors = value;
            context.Settings.Save();
            ApplyCSharpColors();
            SetupChanged();
        }
    }

    /// <summary>The Python a folder's files are checked with, picked from the ones found, its own environment first.</summary>
    internal void PickPythonInterpreter(string root)
    {
        var choices = new List<(string, Action)> { (context.Strings.Said("languages.python_auto"), () => { UsePythonInterpreter(root, null); SetupChanged(); }) };
        var found = Hex.Core.Notebooks.KernelSpecs.Pythons().ToList();
        if (PythonEnvironment.FindLocal(root, root) is { } local) found.Insert(0, local);
        foreach (string python in found.Distinct(PlatformPaths.Comparer))
            choices.Add((python, () => { UsePythonInterpreter(root, python); SetupChanged(); }));
        context.Windows.Choose(context.Strings.Said("languages.python_interpreter"), root, choices);
    }

    /// <summary>The Python picked for a folder, null for the one found by itself.</summary>
    internal static string? PythonPickedFor(string root) => PythonEnvironment.Selected(root);
}
