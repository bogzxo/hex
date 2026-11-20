using System.Globalization;
using System.Numerics;

using Horizon.Core;
using Horizon.Engine;

namespace Hex.Dawn;

public static partial class DawnProgram
{
    private const string ARGUMENT_OPEN = "--open";
    private const string ARGUMENT_WORKSPACE = "--workspace";
    private const string ARGUMENT_SIZE = "--size";
    private const string ARGUMENT_CHECK = "--check";
    private const string ARGUMENT_SELF_TEST = "--selftest";
    private const string ARGUMENT_WHEEL = "--wheel";
    private const string ARGUMENT_EXIT = "--exit";
    private const string ARGUMENT_EXERCISE = "--exercise";
    private const string ARGUMENT_SCALE = "--scale";
    private const string ARGUMENT_BROWSE = "--browse";
    private const string ARGUMENT_WATCH = "--watch";
    private const string ARGUMENT_VIEW = "--view";
    private const string ARGUMENT_CONSOLE = "--console";
    private const string ARGUMENT_LINE = "--line";
    private const string ARGUMENT_FOLDER = "--folder";
    private const string ARGUMENT_WINDOW = "--window";
    private const string ARGUMENT_FRESH = "--fresh";
    private const string ARGUMENT_MCP = "--mcp";
    private const string ARGUMENT_FACE = "--face";
    private const string ARGUMENT_NO_AI = Hex.Core.HexAi.ARGUMENT;

    // The self test's folders in the temp folder, each run's ending in its process id
    private const string SELF_TEST_FOLDER = "horizon-hex-selftest-";
    private const string SELF_TEST_HOME = "horizon-hex-selftest-dusk-";

    // Where layouts go when nobody says otherwise, next to the editor
    private const string DEFAULT_WORKSPACE = "layouts";

    /// <summary>
    /// Starts the editor. Without arguments it comes up maximised with an empty layout.
    /// <list type="bullet">
    /// <item><c>--workspace ../Game/Assets/ui</c> is the folder layouts are opened from and saved to.</item>
    /// <item><c>--open menu.hor</c> starts with a layout open.</item>
    /// <item><c>--browse</c> starts by asking which layout to open, with the dialog Windows has for it.</item>
    /// <item><c>--size 1280x720</c> starts in a window of that size rather than maximised.</item>
    /// <item><c>--check a.hor folder</c> opens every layout named (or in the folders named) and prints whether it writes back the same.</item>
    /// <item><c>--exercise</c> also edits, saves, reopens and undoes every layout that is checked. It writes over them, so point it at copies.</item>
    /// <item><c>--scale 1.25</c> draws the editor that much bigger, rather than as big as it was left the last time (the selector in its toolbar).</item>
    /// <item><c>--selftest</c> has the editor click through itself and print how that went, <c>--wheel</c> makes it wait for the real mouse wheel as well.
    /// <c>--selftest code</c> (or layout, workspace, extensions, languages, garbage, any of them) runs only those parts of it.</item>
    /// <item><c>--watch</c> has the self-test go slowly enough to follow by eye, left to itself it is over in a few seconds.</item>
    /// <item><c>--view table</c> brings the file of <c>--open</c> up in a view of its own, design, code or table, once it can be.</item>
    /// <item><c>--console "moves.jab" "len(keys(moves))"</c> runs lines in the HIDL console once the file's values are in.</item>
    /// <item><c>--line 42</c> opens the file of <c>--open</c> at that line, which is how Dusk opens a path out of a terminal.</item>
    /// <item><c>--folder C:\code</c> starts on a workspace of that folder, handed to the Dawn that's running when there is one.</item>
    /// <item><c>a.cs src\b.cs:12:3 ..\tools</c>, paths on their own anywhere in the line, open those files (the last one
    /// shown) at the line and column after them, and a folder among them is <c>--folder</c> when that isn't said.</item>
    /// <item><c>--goto b.cs:12:3</c> (or <c>-g</c>) is one more file at a line and column, the way code -g has it.</item>
    /// <item><c>--window</c> is a window of its own whatever is running, a file or a folder isn't handed over (the terminal's dawn -window).</item>
    /// <item><c>--fresh</c> starts clean, on the explorer, with no workspace, tabs, backups or last file of before. Settings and keys are read as ever.</item>
    /// <item><c>--mcp</c> puts the MCP server up for agents whatever the settings say (Hex.exe started by a client with it is the bridge to it instead, see McpBridge).</item>
    /// <item><c>--face noon</c> starts in Agentic, <c>--face dawn</c> in the editor, the face the workspace was left in otherwise.</item>
    /// <item><c>--no-ai</c> starts without any of the agents whatever the settings say, No AI for the one run (see HexAi).</item>
    /// <item><c>--exit</c> closes the editor once its checks or its self-test are done, with an exit code of 1 if anything failed.</item>
    /// </list>
    /// </summary>
    public static void Run(string[] args)
    {
        try
        {
            Start(args);
        }
        finally
        {
            // The test's folders go with it, once the engine and its terminals have let go of them. One that won't go
            // is a day old for the sweep of a later run
            foreach (string folder in selfTestFolders)
                TryClear(folder);
        }
    }

    // The self test's part that checks No AI, which has the whole run without the agents
    internal const string NO_AI_SUITE = "no-ai";

    // The folders this run's self test works in, taken away as it ends
    private static string[] selfTestFolders = [];

    private static void Start(string[] args)
    {
        bool selfTest = args.Contains(ARGUMENT_SELF_TEST);
        bool nativeShortcuts = selfTest && args.Contains("native-shortcuts");

        // A workspace or the layouts to check that were named are meant from wherever the editor was started, the
        // rest (its skin, its fonts, the layouts folder nobody named) is next to it
        string workspace = ValueOf(args, ARGUMENT_WORKSPACE) is { } named ? Path.GetFullPath(named) : DEFAULT_WORKSPACE;
        string[] check = [.. ValuesOf(args, ARGUMENT_CHECK).Select(Path.GetFullPath)];

        // The files and the folder named, on their own or with --open, --goto and --folder, are from there too
        string started = Directory.GetCurrentDirectory();
        var (files, folderNamed) = HexLaunch.Parse(args, started);
        string? folder = ValueOf(args, ARGUMENT_FOLDER) is { } asked && Directory.Exists(Path.Combine(started, asked)) ? Path.GetFullPath(Path.Combine(started, asked)) : folderNamed;
        Directory.SetCurrentDirectory(Hex.Core.HexHost.ProgramFolder);

        if (selfTest)
        {
            // What runs before this one left behind, a crash or a kill never got to tidy up after itself
            SweepSelfTestFolders();

            // The test makes and saves layouts of its own, somewhere nobody's work is in the way of it. Always a folder
            // of this run's own, two tests going at once took the one shared folder out from under each other
            workspace = Path.Combine(Path.GetTempPath(), SELF_TEST_FOLDER + System.Environment.ProcessId);
            TryClear(workspace);

            // And the terminal's files (Dusk's, %AppData%\Dusk) in a home of the test's own, so nobody's settings, keys
            // or plugins are read or written by it
            string home = Path.Combine(Path.GetTempPath(), SELF_TEST_HOME + System.Environment.ProcessId);
            TryClear(home);
            Directory.CreateDirectory(home);
            Environment.SetEnvironmentVariable("DUSK_HOME", home);
            selfTestFolders = [workspace, home];
        }

        // A file or a folder to open and a Dawn running already, it goes to that one and this one leaves, unless this
        // one was asked to be a window of its own
        bool window = args.Contains(ARGUMENT_WINDOW);
        if (!selfTest && !window && HandOver(folder, files))
            return;

        // Started with nothing to open and a Hex running already, the one asleep between uses (DawnResident) or one with its
        // window up: that one makes its editor or comes to the front, in a moment, and this one goes
        if (args.Length == 0 && Hex.Shell.HexInstance.Tell(DawnScene.RemotePipe, "show"))
            return;

        // Started with the system to sleep until it's wanted (HexStartup), not when a Hex is running already
        bool background = !selfTest && args.Contains(HexStartup.ARGUMENT);
        if (background && Hex.Shell.HexInstance.Tell(DawnScene.RemotePipe, DawnResident.PING))
            return;

        Directory.CreateDirectory(workspace);

        // The agents' chats, pictures and caches that were in the settings folder moved into the data folder, once, before
        // anything reads them. Only this Hex, the one that's staying, a Hex that handed over above never got here
        if (!selfTest && check.Length == 0)
            Hex.Core.HexData.MoveOld();

        // What the editor's first moments wait on, started on the pool while the engine, the device and the window are made:
        // WSL's distributions for the terminal's shells, and the editor's own layouts read (and the reader's code compiled)
        // before the editor loads them on the thread that's holding the window back
        Horizon.UI.Components.TerminalProfiles.Prefetch();
        // The editor's statics (its strings read, the settings' catalog made), a tenth of a second the editor's making
        // started with, run on the pool while the engine is made. The editor waits for them only if they aren't done
        _ = Task.Run(() => System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(DawnScene).TypeHandle));
        Horizon.UI.UILayout.Prewarm(EditorLayouts());
        // And the grammars: the first is the TextMate registry made as well, a fifth of a second the editor's making
        // waited on for the agents' box (Markdown) and again for the first file of code
        _ = Task.Run(() =>
        {
            // Each read once as well, a grammar gets going (its rules, the languages it holds) the first time it reads, which
            // the agents' first message and the first file of code waited on otherwise. The editor reads on the pool too
            foreach (string kind in (string[])["message.md", "file.cs"])
                Horizon.UI.Scripting.CodeLanguages.ForFile(kind)?.Read("x\n", 1);
        });

        var options = new HexOptions(
            Open: files.Count > 0 ? files[^1].Path : null,
            Files: [.. files],
            Workspace: workspace,
            SelfTest: selfTest,
            Wheel: args.Contains(ARGUMENT_WHEEL),
            Check: check,
            Exit: args.Contains(ARGUMENT_EXIT),
            Exercise: args.Contains(ARGUMENT_EXERCISE),
            Browse: args.Contains(ARGUMENT_BROWSE),
            Watch: args.Contains(ARGUMENT_WATCH),
            View: ValueOf(args, ARGUMENT_VIEW),
            Console: ValuesOf(args, ARGUMENT_CONSOLE) is { Length: > 0 } lines ? lines : null,
            Line: files.Count > 0 ? files[^1].Line : 0,
            Folder: folder,
            Fresh: args.Contains(ARGUMENT_FRESH),
            Mcp: args.Contains(ARGUMENT_MCP),
            Face: ValueOf(args, ARGUMENT_FACE),
            // The self test of No AI is a run without the agents, there's no taking them out of one that has them
            NoAi: args.Contains(ARGUMENT_NO_AI) || (selfTest && ValuesOf(args, ARGUMENT_SELF_TEST).Contains(NO_AI_SUITE, StringComparer.OrdinalIgnoreCase)),
            Suites: ValuesOf(args, ARGUMENT_SELF_TEST),
            // The editor testing itself starts out the same size every time, unless it is told which
            Scale: float.TryParse(ValueOf(args, ARGUMENT_SCALE), CultureInfo.InvariantCulture, out float scale) ? scale : selfTest ? 1.0f : null);

        // The editor lays itself out to whatever window it gets, so it takes all there is
        Vector2? size = ParseSize(ValueOf(args, ARGUMENT_SIZE));

        using var engine = new GameEngine(GameEngineConfiguration.Default with
        {
            // A text editor, nobody plays it with a controller
            Gamepads = false,
            // Its settings and caches next to Hex.exe, Program.Main says so already and this says it to the engine
            FilesBesideProgram = true,
            WindowConfiguration = WindowManagerConfiguration.Default1600x900 with
            {
                WindowTitle = "Hex",
                WindowIcon = HexIdentity.ICON,
                CustomFrame = true,
                // Drawn at the scale of whatever screen it's on like any other program, a self test the same size anywhere
                ScaleToDisplay = !selfTest,
                AppId = "hex-dawn",


                Mode = selfTest && !nativeShortcuts ? RunMode.Game : RunMode.Application,
                Behaviour = ApplicationBehaviour.All,
                WindowSize = size ?? WindowManagerConfiguration.Default1600x900.WindowSize,
                Maximized = size is null,
                StartHidden = background,
                VSync = true,
                FramesPerSecond = selfTest && !options.Watch ? 240.0 : 120.0,
                UpdatesPerSecond = selfTest && !options.Watch && !nativeShortcuts ? 240.0 : 120.0
            }
        });

        // F12 is go to definition, the engine took a screenshot with it as well and left a folder of them by Hex.exe
        engine.ScreenshotKey = null;

        Horizon.Core.Diagnostics.StartupTrace.Mark("engine made");

        // The terminal in the same window, Ctrl+Alt+Shift+D, when Hex.exe says how to make it. Not in a self test, which
        // is the one mode and nothing else, unless it asks for the suite that goes between the two
        bool bothTested = selfTest && options.Suites?.Contains("dusk-noon", StringComparer.Ordinal) == true;
        DawnScene MakeEditor(HexOptions with)
        {
            var editor = new DawnScene(with);
            if ((!selfTest || bothTested) && Other is { } other)
                Hex.Shell.HexModes.Host(Hex.Shell.HexMode.Dawn, editor, (mode, folder) => mode == Hex.Shell.HexMode.Dawn ? editor : other(folder));
            return editor;
        }

        // Every editor after the first made the same way, in this process, when it sleeps between uses
        if (!selfTest)
            DawnResident.Prepare(MakeEditor, options);

#if DEBUG
        // The engine's own debugger is on F8, F9 and F10 in a Debug build, which are the C# debugger's here, so it's on
        // Pause and the rest of it on its buttons
        if (engine.Debugger is { } debugger)
        {
            debugger.ToggleKey = Horizon.Input.Key.Pause;
            debugger.PauseKey = null;
            debugger.StepKey = null;
        }
#endif

        // Started with the system there's no editor yet, only the device and the hidden window. Made in a call of its own
        // so nothing here holds on to it: an editor put away (DawnResident) was kept alive by this frame for the whole run
        Begin(engine, () => background ? DawnResident.StartAsleep() : MakeEditor(options));
        engine.Run();
        Hex.Shell.HexModes.Shutdown();

        // The language servers go with the editor. serve-d stayed on after it, with its DCD, one more of each a run
        Horizon.UI.Scripting.LanguageServers.StopAll();

        // Where the time went, with HEX_PROFILE set
        DawnScene.Profile.Report();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Begin(GameEngine engine, Func<Scene> first)
    {
        engine.SetScene(first());
        Horizon.Core.Diagnostics.StartupTrace.Mark("editor made");
    }

    // The layouts the editor loads as it starts, its own and the shell's parts it includes, the welcome page's among them
    private static IEnumerable<string> EditorLayouts()
    {
        foreach (string folder in (string[])["Assets/hex", "Assets/hex/shell"])
        {
            if (!Directory.Exists(folder))
                continue;
            foreach (string file in Directory.EnumerateFiles(folder, "*.hor"))
                yield return file.Replace('\\', '/');
        }
    }

    /// <summary>How Hex.exe makes the terminal for the same window, in a folder, see Hex.Shell.HexModes.</summary>
    public static Func<string?, Scene>? Other { get; set; }

    /// <summary>The editor as a scene of its own, for the window the terminal is in, on a folder (null for none).</summary>
    public static Scene Scene(string? folder)
    {
        Directory.CreateDirectory(DEFAULT_WORKSPACE);
        return new DawnScene(new HexOptions(
            Open: null,
            Workspace: DEFAULT_WORKSPACE,
            SelfTest: false,
            Wheel: false,
            Check: [],
            Exit: false,
            Folder: folder is not null && Directory.Exists(folder) ? Path.GetFullPath(folder) : null));
    }

    /// <summary>
    /// Helper method to hand the folder and the files named to the Dawn that's running, the folder first and then every
    /// file at its line and column, false when there's none to take them and this one starts instead.
    /// </summary>
    private static bool HandOver(string? folder, List<HexLaunchFile> files)
    {
        if (folder is null && files.Count == 0)
            return false;

        bool told = folder is not null && Hex.Shell.HexInstance.Tell(DawnScene.RemotePipe, $"folder|{folder}");
        if (folder is not null && !told)
            return false;

        foreach (HexLaunchFile file in files)
        {
            if (!Hex.Shell.HexInstance.Tell(DawnScene.RemotePipe, $"open|{file.Path}|{file.Line}|{file.Column}"))
                return told;
            told = true;
        }

        return told;
    }

    private static string? ValueOf(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>
    /// Everything that follows an argument up to the next one that starts with two dashes.
    /// </summary>
    private static string[] ValuesOf(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0)
            return [];

        return [.. args.Skip(index + 1).TakeWhile(value => !value.StartsWith("--"))];
    }

    /// <summary>
    /// Helper method to empty the self test's folder, false when something has it open. The git suite's repository has
    /// git's read only objects in it, which Directory.Delete won't touch, so they're made writable first.
    /// </summary>
    private static bool TryClear(string folder)
    {
        if (!Directory.Exists(folder))
            return true;

        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Helper method to take away the self test folders of runs that are long over, a day old or more. One still going
    /// is younger than that, and one that won't go is left for the next time.
    /// </summary>
    private static void SweepSelfTestFolders()
    {
        try
        {
            DateTime before = DateTime.UtcNow.AddDays(-1);
            foreach (string folder in Directory.EnumerateDirectories(Path.GetTempPath(), "horizon-hex-selftest-*"))
            {
                // Only the ones that end in a process id, the texts suite's game folder is of another kind
                string name = Path.GetFileName(folder);
                if (!int.TryParse(name[(name.LastIndexOf('-') + 1)..], out _))
                    continue;

                if (Directory.GetLastWriteTimeUtc(folder) < before)
                    TryClear(folder);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static Vector2? ParseSize(string? text)
    {
        if (text?.Split('x', 'X') is [var width, var height]
            && float.TryParse(width, CultureInfo.InvariantCulture, out float x)
            && float.TryParse(height, CultureInfo.InvariantCulture, out float y)
            && x >= 320 && y >= 200)
        {
            return new Vector2(x, y);
        }

        return null;
    }
}
