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
    /// {file} the file from its folder, {exe} the program it makes and {here} the folder it's in as the shell says it.
    /// </summary>
    internal sealed record RunPreset(string Key, string Build, string Run);

    internal static readonly RunPreset[] Presets =
    [
        new("d_dub", "dub build", "dub run"),
        new("d_dmd", "dmd \"{file}\" -of={exe}", "{here}{exe}"),
        new("d_ldc", "ldc2 \"{file}\" -of={exe}", "{here}{exe}"),
        new("d_rdmd", "", "rdmd \"{file}\""),
        new("csharp", "dotnet build", "dotnet run --no-build"),
        new("rust", "cargo build", "cargo run"),
        new("go", "go build -o {exe} .", "{here}{exe}"),
        new("c", "gcc \"{file}\" -o {exe}", "{here}{exe}"),
        new("cpp", "g++ \"{file}\" -o {exe}", "{here}{exe}"),
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

        setupFile = forFile ?? context.Documents.Current.Path;
        setupRoot = RootFor(setupFile);
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

        setupBuild = new TextBox { Size = new Vector2(SETUP_WIDTH - SETUP_CAPTION - 10.0f, SETUP_FIELD), Placeholder = Said("setup_build_hint"), MaxLength = 1000 };
        body.Add(SetupField(Said("setup_build"), setupBuild));

        setupRun = new TextBox { Size = new Vector2(SETUP_WIDTH - SETUP_CAPTION - 10.0f, SETUP_FIELD), Placeholder = Said("setup_run_hint"), MaxLength = 1000 };
        body.Add(SetupField(Said("setup_run"), setupRun));

        setupFolder = new TextBox { Size = new Vector2(300.0f, SETUP_FIELD), Text = ".", MaxLength = 400 };
        body.Add(SetupField(Said("setup_folder"), setupFolder));

        setupOutput = new Dropdown(Said("setup_in_panel"), Said("setup_in_dusk")) { Size = new Vector2(300.0f, SETUP_FIELD) };
        body.Add(SetupField(Said("setup_output"), setupOutput));

        var foot = body.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 10.0f, Anchor = Origin.Left, Pivot = Origin.Left, Padding = new UIEdges(0.0f, 6.0f, 0.0f, 0.0f) });
        foot.Add(new Button { Label = Said("setup_save_run"), Primary = true, Size = new Vector2(150.0f, SETUP_FIELD), OnPressed = () => SaveSetup(run: true) });
        foot.Add(new Button { Label = Said("setup_save"), Size = new Vector2(100.0f, SETUP_FIELD), OnPressed = () => SaveSetup(run: false) });
        foot.Add(new Button { Label = Said("setup_cancel"), Size = new Vector2(100.0f, SETUP_FIELD), OnPressed = window.Close });
        setupSaid = foot.Add(new Label { Size = new Vector2(SETUP_WIDTH - 380.0f, SETUP_FIELD), Align = Origin.Left, Wrap = true });

        // What the folder's tasks say already, or the guess at what it is
        setupKind.Index = GuessPreset(setupRoot, setupFile);
        KindPicked(setupKind.Value);
        if (TasksFileIn(setupRoot) is { } tasksFile && TasksOf(tasksFile, setupRoot) is var known && known.Find(task => task.Name == "run") is { } run)
        {
            setupRun.Text = run.Command;
            setupBuild.Text = known.Find(task => task.Name == "build")?.Command ?? string.Empty;
            setupFolder.Text = run.Folder;
            setupOutput.Index = run.Terminal.Equals(HexTask.DUSK, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        context.Window.Dress(window);
        chrome.Root.Add(window);
        setupBuild.Focus();
    }

    // A line of the window, what it is on the left and its control on the right
    private static StackPanel SetupField(string caption, UIComponent control)
    {
        var line = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 10.0f, Anchor = Origin.Left, Pivot = Origin.Left };
        line.Add(new Label { Text = caption, Size = new Vector2(SETUP_CAPTION, SETUP_FIELD), Align = Origin.Left });
        line.Add(control);
        return line;
    }

    /// <summary>
    /// The run task F5 runs for a file, the one of the tasks.hor of the folder it's in (the workspace's folder it's under,
    /// or its own outside them), null for none. Any folder's run task was run, a D file got another folder's dotnet run.
    /// </summary>
    internal HexTask? RunTaskFor(string file)
    {
        string root = RootFor(file);
        if (TasksFileIn(root) is not { } tasksFile)
            return null;

        List<HexTask> tasks = TasksOf(tasksFile, root);
        return tasks.FirstOrDefault(task => task.Default && task.Group == "run") ?? tasks.FirstOrDefault(task => task.Group == "run");
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
        string extension = Path.GetExtension(file ?? string.Empty).ToLowerInvariant();
        bool Beside(params string[] patterns)
        {
            try
            {
                return patterns.Any(pattern => Directory.EnumerateFiles(root, pattern).Any());
            }
            catch (Exception)
            {
                return false;
            }
        }

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

    /// <summary>A kind's lines with the file's name in them, for a shell of a profile (pwsh, cmd, git_bash).</summary>
    internal static (string Build, string Run) Fill(RunPreset preset, string root, string? file, string shell)
    {
        string relative = file is null ? string.Empty : Path.GetRelativePath(root, file);
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
