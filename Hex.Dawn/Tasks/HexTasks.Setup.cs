using System.Diagnostics.CodeAnalysis;
using System.Numerics;

using Horizon.HIDL;
using Horizon.Rendering;

namespace Hex.Dawn;

// Set Up F5, a little window that writes the build and run tasks of a folder's tasks.hor for whatever's open, so F5
// builds it and runs it. The kind of program is guessed from the file and its folder (a dub project, a .d file, a
// .csproj), each kind a build line and a run line with the file's name in them, both there to change. Where it runs is
// picked, the terminal panel under the editor or a Dusk window of its own. F5 on a file with no run task opens it.
internal sealed partial class HexTasks
{
    /// <summary>
    /// A kind of program, what it's called (a key of tasks.presets in the strings), what builds it and what runs it,
    /// {file} the file from its folder, {exe} the program it makes and {here} the folder it's in as the shell says it. A
    /// project's kind says the files its project has (dub.json, *.csproj), it runs in the nearest folder over the file
    /// with one of them, a project down in the workspace built where it is and not from the workspace's folder.
    /// </summary>
    internal sealed record RunPreset(string Key, string Build, string Run, string[]? Projects = null)
    {
        /// <summary>
        /// Whether it's a file's program rather than a project's, run in the file's own folder so what it makes is next
        /// to it. It was made in the workspace's folder, a program of a folder down in it nowhere near its code.
        /// </summary>
        public bool InFilesFolder => Build.Contains("{file}", StringComparison.Ordinal) || Run.Contains("{file}", StringComparison.Ordinal);
    }

    // dmd and ldc2 with -i, the modules a file imports from its folder built with it (a util.d beside it didn't link), and
    // -of= quoted whole, Windows PowerShell cut -of=main.exe in two at the dot and dmd made a .obj of nothing
    internal static readonly RunPreset[] Presets =
    [
        new("d_dub", "dub build", "dub run", ["dub.json", "dub.sdl"]),
        new("d_dmd", "dmd -i -g \"{file}\" \"-of={exe}\"", "{here}{exe}"),
        new("d_ldc", "ldc2 -i -g \"{file}\" \"-of={exe}\"", "{here}{exe}"),
        new("d_rdmd", "", "rdmd \"{file}\""),
        new("csharp", "dotnet build", "dotnet run --no-build", ["*.csproj", "*.sln", "*.slnx"]),
        new("rust", "cargo build", "cargo run", ["Cargo.toml"]),
        new("go", "go build -o \"{exe}\" .", "{here}{exe}", ["go.mod"]),
        new("c", "gcc \"{file}\" -o \"{exe}\"", "{here}{exe}"),
        new("cpp", "g++ \"{file}\" -o \"{exe}\"", "{here}{exe}"),
        new("python", "", "python \"{file}\""),
        new("node", "", "node \"{file}\""),
        new("other", "", "")
    ];

    private const float SETUP_WIDTH = 640.0f, SETUP_CAPTION = 96.0f, SETUP_FIELD = 28.0f;

    // The window while it's up, and what's in it
    private WindowPanel? setupWindow;
    private Dropdown? setupKind, setupOutput;
    private TextBox? setupBuild, setupRun, setupFolder;
    private Label? setupSaid;
    private string setupRoot = string.Empty;
    private string? setupFile;

    /// <summary>The window, null while it's down, for a test.</summary>
    internal WindowPanel? SetupWindow => setupWindow is { Parent: not null } ? setupWindow : null;

    /* What a test reads off the window and fills it with, the way the UI does */

    internal string SetupKind => setupKind?.Value ?? string.Empty;
    internal string SetupBuild => setupBuild?.Text ?? string.Empty;
    internal string SetupRunLine => setupRun?.Text ?? string.Empty;

    internal void FillSetup(string build, string run, bool inDusk)
    {
        setupBuild!.Text = build;
        setupRun!.Text = run;
        setupOutput!.Index = inDusk ? 1 : 0;
    }

    internal void PressSetup(bool run) => SaveSetup(run);

    /// <summary>
    /// Set Up F5, the window over the editor for the file that's open (or one named), filled in with what the folder's
    /// tasks.hor says already or a guess at it.
    /// </summary>
    internal void SetUpRun(string? forFile = null)
    {
        setupWindow?.Parent?.Remove(setupWindow);

        // A D module of a program is set up as the program, the file with main, a lone module of its own doesn't link
        setupFile = forFile ?? context.Documents.Current.Path;
        if (HexDebugging.IsD(setupFile) && HexLanguages.DubProjectOf(Path.GetFullPath(setupFile!)) is null)
            setupFile = HexDebugging.MainFileOf(setupFile!, context.Workspace.Roots) ?? setupFile;
        setupRoot = SetupFolderFor(setupFile);
        UIModule chrome = context.Window.Chrome;
        string Said(string key) => context.Strings.Said("tasks." + key);

        var window = setupWindow = new WindowPanel(Said("setup_title"))
        {
            Icon = "debug-alt",
            Size = new Vector2(SETUP_WIDTH + 40.0f, 0.0f),
            ResizeGrip = false,
            Padding = new UIEdges(20.0f, 14.0f, 20.0f, 18.0f),
            Intro = UIIntro.Pop,
            IntroTime = 0.18f
        };
        window.OnClosed = () => window.Parent?.Remove(window);

        var body = window.Add(new StackPanel { Spacing = 10.0f, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
        string what = setupFile is { } file ? Path.GetFileName(file) : Path.GetFileName(setupRoot.TrimEnd('/', '\\'));
        body.Add(new Label
        {
            Text = context.Strings.Said("tasks.setup_intro", ("name", what), ("file", TASKS_FILE)),
            Wrap = true,
            Size = new Vector2(SETUP_WIDTH, 0.0f),
            Align = Origin.TopLeft,
            Color = context.Window.Compositor.Skin?.DimTextColor
        });

        string[] kinds = [.. Presets.Select(preset => Said("presets." + preset.Key))];
        setupKind = new Dropdown(kinds) { Size = new Vector2(300.0f, SETUP_FIELD), MaxRows = 12, OnChanged = KindPicked };
        body.Add(SetupField(Said("setup_kind"), setupKind));

        // Enter in any of its boxes saves and runs, the way the primary button does
        setupBuild = new TextBox { Size = new Vector2(SETUP_WIDTH - SETUP_CAPTION - 10.0f, SETUP_FIELD), Placeholder = Said("setup_build_hint"), MaxLength = 1000, OnSubmitted = _ => SaveSetup(run: true) };
        body.Add(SetupField(Said("setup_build"), setupBuild));

        setupRun = new TextBox { Size = new Vector2(SETUP_WIDTH - SETUP_CAPTION - 10.0f, SETUP_FIELD), Placeholder = Said("setup_run_hint"), MaxLength = 1000, OnSubmitted = _ => SaveSetup(run: true) };
        body.Add(SetupField(Said("setup_run"), setupRun));

        // Where it runs, the folder of the tasks unless another is typed or picked, as far down from it as it is. Empty
        // says which folder that is by name, a lone "." told nobody anything
        string here = Path.GetFileName(setupRoot.TrimEnd('/', '\\')) is { Length: > 0 } rootName ? rootName : setupRoot;
        setupFolder = new TextBox
        {
            Size = new Vector2(SETUP_WIDTH - SETUP_CAPTION - 10.0f - 110.0f, SETUP_FIELD),
            Placeholder = context.Strings.Said("tasks.setup_folder_here", ("folder", here)),
            MaxLength = 400,
            OnSubmitted = _ => SaveSetup(run: true)
        };
        var folderLine = SetupField(Said("setup_folder"), setupFolder);
        folderLine.Add(new Button { Label = Said("setup_browse"), Size = new Vector2(100.0f, SETUP_FIELD), OnPressed = BrowseSetupFolder });
        body.Add(folderLine);

        setupOutput = new Dropdown(Said("setup_in_panel"), Said("setup_in_dusk")) { Size = new Vector2(300.0f, SETUP_FIELD) };
        body.Add(SetupField(Said("setup_output"), setupOutput));

        // What it says along the bottom on the left, the buttons on the right with the one Enter presses last, as every
        // dialog of the engine's has them
        var foot = body.Add(new Panel { Size = new Vector2(SETUP_WIDTH, SETUP_FIELD + 6.0f), Anchor = Origin.Left, Pivot = Origin.Left, Padding = new UIEdges(0.0f, 6.0f, 0.0f, 0.0f) });
        setupSaid = foot.Add(new Label { Size = new Vector2(SETUP_WIDTH - 390.0f, SETUP_FIELD), Anchor = Origin.Left, Pivot = Origin.Left, Align = Origin.Left, Wrap = true });
        var buttons = foot.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 10.0f, Anchor = Origin.Right, Pivot = Origin.Right });
        buttons.Add(new Button { Label = Said("setup_cancel"), Size = new Vector2(100.0f, SETUP_FIELD), OnPressed = window.Close });
        buttons.Add(new Button { Label = Said("setup_save"), Size = new Vector2(100.0f, SETUP_FIELD), OnPressed = () => SaveSetup(run: false) });
        buttons.Add(new Button { Label = Said("setup_save_run"), Primary = true, Size = new Vector2(150.0f, SETUP_FIELD), OnPressed = () => SaveSetup(run: true) });

        // What the folder's tasks say already, or the guess at what it is
        setupKind.Index = GuessPreset(setupRoot, setupFile);
        KindPicked(setupKind.Value);
        if (TasksFileIn(setupRoot) is { } tasksFile && TasksOf(tasksFile, setupRoot) is var known && known.Find(task => task.Name == "run") is { } run)
        {
            setupRun.Text = run.Command;
            setupBuild.Text = known.Find(task => task.Name == "build")?.Command ?? string.Empty;
            setupFolder.Text = run.Folder == "." ? string.Empty : run.Folder;
            setupOutput.Index = run.Terminal.Equals(HexTask.DUSK, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        context.Window.Dress(window);
        chrome.Root.Add(window);
        setupBuild.Focus();
    }

    // Browse beside Runs in, a folder picked and written as far down from the tasks' folder as it is, or whole outside it
    private void BrowseSetupFolder() => context.Windows.ChooseFolder(picked =>
    {
        if (setupFolder is null)
            return;

        string inside = Path.GetRelativePath(setupRoot, picked);
        setupFolder.Text = inside.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(inside) ? picked : inside;
    });

    // A line of the window, what it is on the left and its control on the right
    private static StackPanel SetupField(string caption, UIComponent control)
    {
        var line = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 10.0f, Anchor = Origin.Left, Pivot = Origin.Left };
        line.Add(new Label { Text = caption, Size = new Vector2(SETUP_CAPTION, SETUP_FIELD), Align = Origin.Left });
        line.Add(control);
        return line;
    }

    /// <summary>
    /// The run task F5 runs for a file, the one of the nearest tasks.hor over it (in its project's folder, its own, or the
    /// workspace's folder it's under), null for none. Any folder's run task was run, a D file got another folder's dotnet run.
    /// </summary>
    internal HexTask? RunTaskFor(string file)
    {
        if (TasksFolderFor(file) is not { } root || TasksFileIn(root) is not { } tasksFile)
            return null;

        List<HexTask> tasks = TasksOf(tasksFile, root);
        return tasks.FirstOrDefault(task => task.Default && task.Group == "run") ?? tasks.FirstOrDefault(task => task.Group == "run");
    }

    /// <summary>
    /// The nearest folder over a file with a tasks.hor in it, up to the workspace's folder it's in, null for none. Two
    /// projects in one workspace each have their own, a run of one was the run of both.
    /// </summary>
    internal string? TasksFolderFor(string? file)
    {
        string root = RootFor(file);
        if (file is null || Path.GetDirectoryName(Path.GetFullPath(file)) is not { } own || !PlatformPaths.IsUnder(own, root))
            return TasksFileIn(root) is not null ? root : null;

        for (string? folder = own; folder is not null; folder = Path.GetDirectoryName(folder))
        {
            if (TasksFileIn(folder) is not null)
                return folder;
            if (PlatformPaths.Same(folder, root))
                break;
        }

        return null;
    }

    /// <summary>
    /// Where Set Up F5 writes a file's tasks, the folder of the tasks.hor it has already, or else where the program it
    /// looks like runs (its project's folder, its own folder for a lone file), or else the workspace's folder.
    /// </summary>
    private string SetupFolderFor(string? file)
    {
        if (TasksFolderFor(file) is { } known)
            return known;

        string root = RootFor(file);
        int guessed = GuessPreset(root, file);
        return guessed >= 0 && FolderFor(Presets[guessed], root, file) is { Length: > 0 } folder ? Path.GetFullPath(Path.Combine(root, folder)) : root;
    }

    /// <summary>The workspace's folder a file is in, the first one for none, the file's own folder outside them.</summary>
    internal string RootFor(string? file)
    {
        if (file is not null && context.Workspace.Roots.Where(root => PlatformPaths.IsUnder(Path.GetFullPath(file), root)).OrderByDescending(root => root.Length).FirstOrDefault() is { } inside)
            return inside;
        if (file is not null && Path.GetDirectoryName(Path.GetFullPath(file)) is { } own)
            return own;
        return context.Workspace.Roots.FirstOrDefault() ?? System.Environment.CurrentDirectory;
    }

    /// <summary>Which of the kinds a file in a folder most likely is, by its extension and the projects beside it.</summary>
    internal static int GuessPreset(string root, string? file)
    {
        // A project's files in the file's folder or any over it up to the tasks' folder, a dub project down in the
        // workspace was taken for a lone file
        string extension = Path.GetExtension(file ?? string.Empty).ToLowerInvariant();
        string from = file is not null && Path.GetDirectoryName(Path.GetFullPath(file)) is { } own ? own : root;
        bool Beside(params string[] patterns) => ProjectFolder(from, root, patterns) is not null;

        string key = extension switch
        {
            ".d" or ".di" => Beside("dub.json", "dub.sdl") ? "d_dub"
                : LanguageServers.IsAvailable("dmd") ? "d_dmd" : LanguageServers.IsAvailable("ldc2") ? "d_ldc" : "d_dmd",
            ".cs" => "csharp",
            ".rs" => "rust",
            ".go" => "go",
            ".c" => "c",
            ".cpp" or ".cc" or ".cxx" => "cpp",
            ".py" => "python",
            ".js" or ".mjs" => "node",
            _ => Beside("dub.json", "dub.sdl") ? "d_dub" : Beside("*.csproj", "*.sln") ? "csharp" : Beside("Cargo.toml") ? "rust" : Beside("go.mod") ? "go" : "other"
        };

        return Array.FindIndex(Presets, preset => preset.Key == key);
    }

    /// <summary>
    /// Where a kind runs from the tasks' folder, the file's own folder for a file's program, its project's for a project's,
    /// empty for the tasks' folder itself.
    /// </summary>
    internal static string FolderFor(RunPreset preset, string root, string? file)
    {
        if (file is null || Path.GetDirectoryName(Path.GetFullPath(file)) is not { } own)
            return string.Empty;

        string? at = preset.InFilesFolder ? own : preset.Projects is { } projects ? ProjectFolder(own, root, projects) : null;
        if (at is null || PlatformPaths.Same(at, root))
            return string.Empty;

        string inside = Path.GetRelativePath(root, at);
        return inside.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(inside) ? at : inside;
    }

    /// <summary>The nearest folder from one up to the tasks' folder with a file of a project's in it, null for none.</summary>
    internal static string? ProjectFolder(string from, string root, string[] projects)
    {
        for (string? folder = from; folder is not null; folder = Path.GetDirectoryName(folder))
        {
            try
            {
                if (projects.Any(pattern => Directory.EnumerateFiles(folder, pattern).Any()))
                    return folder;
            }
            catch (Exception)
            {
                // A folder that can't be read has no project as far as this goes
            }

            if (PlatformPaths.Same(folder, root) || !PlatformPaths.IsUnder(folder, root))
                break;
        }

        return null;
    }

    /// <summary>A kind's lines with the file's name in them, for a shell of a profile (pwsh, cmd, git_bash).</summary>
    internal static (string Build, string Run) Fill(RunPreset preset, string root, string? file, string shell)
    {
        // From the folder it runs in, the file's own for a file's program
        string from = FolderFor(preset, root, file) is { Length: > 0 } folder ? Path.GetFullPath(Path.Combine(root, folder)) : root;
        string relative = file is null ? string.Empty : Path.GetRelativePath(from, file);
        string name = file is null ? Path.GetFileName(root.TrimEnd('/', '\\')) : Path.GetFileNameWithoutExtension(file);
        bool unixShell = !OperatingSystem.IsWindows() || shell.StartsWith("wsl", StringComparison.OrdinalIgnoreCase) || shell.Contains("bash", StringComparison.OrdinalIgnoreCase);
        string exe = OperatingSystem.IsWindows() && !shell.StartsWith("wsl", StringComparison.OrdinalIgnoreCase) ? name + ".exe" : name;
        string here = unixShell ? "./" : ".\\";
        if (unixShell)
            relative = relative.Replace('\\', '/');

        string Filled(string line) => line.Replace("{file}", relative).Replace("{exe}", exe).Replace("{here}", here);
        return (Filled(preset.Build), Filled(preset.Run));
    }

    // A kind picked, its lines filled in for the file
    private void KindPicked(string label)
    {
        if (setupBuild is null || setupRun is null || setupKind is null || setupKind.Index < 0 || setupKind.Index >= Presets.Length)
            return;

        var (build, run) = Fill(Presets[setupKind.Index], setupRoot, setupFile, context.Terminals.UsualProfileId);
        setupBuild.Text = build;
        setupRun.Text = run;
        if (setupFolder is not null)
            setupFolder.Text = FolderFor(Presets[setupKind.Index], setupRoot, setupFile);
    }

    // Save, or Save and run, the folder's build and run tasks written, then F5's run if it was asked for
    private void SaveSetup(bool run)
    {
        if (setupRun is null || setupBuild is null || setupFolder is null || setupOutput is null || setupSaid is null)
            return;

        string runLine = setupRun.Text.Trim(), build = setupBuild.Text.Trim();
        if (runLine.Length == 0)
        {
            setupSaid.Text = context.Strings.Said("tasks.setup_needs_run");
            return;
        }

        string folder = setupFolder.Text.Trim() is { Length: > 0 } typed ? typed : ".";
        string terminal = setupOutput.Index == 1 ? HexTask.DUSK : HexTask.PANEL;
        if (WriteRunTasks(setupRoot, build, runLine, folder, terminal) is not { } written)
        {
            setupSaid.Text = context.Strings.Said("tasks.file_not_written", ("file", TASKS_FILE), ("reason", "?"));
            return;
        }

        setupWindow?.Close();
        context.Notices.Say(context.Strings.Said("tasks.setup_written", ("file", written)));
        if (run && TasksOf(written, setupRoot).Find(task => task.Name == "run") is { } task)
            Run(task);
    }

    /// <summary>
    /// Writes a folder's build and run tasks into its tasks.hor (made if there isn't one), the run one depending on the
    /// build one when there is a build, both the default of their group, running where it says. The file's other tasks
    /// are kept. The file written, null when it couldn't be.
    /// </summary>
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors, typeof(HexTask))]
    internal string? WriteRunTasks(string root, string build, string run, string folder, string terminal)
    {
        string file = TasksFileIn(root) ?? Path.Combine(root, TASKS_FILE);
        List<HexTask> kept = File.Exists(file) ? [.. TasksOf(file, root).Where(task => task.Name is not ("build" or "run"))] : [];

        var tasks = new HIDLObject();
        if (build.Length > 0)
            tasks.Add("build", HIDLSerializer.ToObject(new HexTask { Command = build, Folder = folder, Group = "build", Default = true, Terminal = terminal }));
        tasks.Add("run", HIDLSerializer.ToObject(new HexTask
        {
            Command = run,
            Folder = folder,
            Group = "run",
            Default = true,
            DependsOn = build.Length > 0 ? "build" : string.Empty,
            Terminal = terminal
        }));

        foreach (HexTask other in kept)
        {
            if (other.Group is "build" or "run")
                other.Default = false;
            tasks.Add(other.Name, HIDLSerializer.ToObject(other));
        }

        try
        {
            // A file there already has only its tasks written again, what else it has (its automations, the functions
            // they call, its comments) is left as it was
            if (File.Exists(file))
            {
                File.WriteAllText(file, TasksFileText.Replace(File.ReadAllText(file), "tasks", new HIDLDocument().Let("tasks", tasks).Write()));
                return file;
            }

            File.WriteAllText(file, new HIDLDocument()
                .Comment("The tasks of this folder, each a command line Dawn runs in a terminal of its own. Run Task in the Run menu")
                .Comment("picks one, Ctrl+Shift+B runs the build task and F5 on a file that isn't HIDL the run task, after the one it")
                .Comment("depends on. Set Up F5 in the Run menu writes the build and run ones again. What a compiler says in one goes in")
                .Comment("the Problems panel.")
                .Let("tasks", tasks)
                .Write());
            return file;
        }
        catch (Exception e)
        {
            context.Notices.Say(context.Strings.Said("tasks.file_not_written", ("file", TASKS_FILE), ("reason", e.Message)), error: true);
            return null;
        }
    }
}
