using System.Globalization;
using System.Numerics;

using Horizon.Core;
using Horizon.Engine;

namespace Hex.Dawn;

public static class DawnProgram
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
    /// <item><c>--window</c> is a window of its own whatever is running, a file or a folder isn't handed over (the terminal's dawn -window).</item>
    /// <item><c>--exit</c> closes the editor once its checks or its self-test are done, with an exit code of 1 if anything failed.</item>
    /// </list>
    /// </summary>
    public static void Run(string[] args)
    {
        bool selfTest = args.Contains(ARGUMENT_SELF_TEST);

        // A workspace or the layouts to check that were named are meant from wherever the editor was started, the
        // rest (its skin, its fonts, the layouts folder nobody named) is next to it
        string workspace = ValueOf(args, ARGUMENT_WORKSPACE) is { } named ? Path.GetFullPath(named) : DEFAULT_WORKSPACE;
        string[] check = [.. ValuesOf(args, ARGUMENT_CHECK).Select(Path.GetFullPath)];
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);

        if (selfTest)
        {
            // The test makes and saves layouts of its own, somewhere nobody's work is in the way of it
            workspace = Path.Combine(Path.GetTempPath(), "horizon-hex-selftest");
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);

            // And the terminal's files (Dusk's, %AppData%\Dusk) in a home of the test's own, so nobody's settings, keys
            // or plugins are read or written by it
            string home = Path.Combine(Path.GetTempPath(), "horizon-hex-selftest-dusk");
            if (Directory.Exists(home))
                Directory.Delete(home, recursive: true);
            Directory.CreateDirectory(home);
            Environment.SetEnvironmentVariable("DUSK_HOME", home);
        }

        // A file or a folder to open and a Dawn running already, it goes to that one and this one leaves, unless this
        // one was asked to be a window of its own
        bool window = args.Contains(ARGUMENT_WINDOW);
        if (!selfTest && !window && ValueOf(args, ARGUMENT_OPEN) is { } toOpen && Path.IsPathRooted(toOpen) && File.Exists(toOpen)
            && Hex.Shell.HexInstance.Tell(DawnScene.RemotePipe, $"open|{Path.GetFullPath(toOpen)}|{(int.TryParse(ValueOf(args, ARGUMENT_LINE), out int lineAsked) ? lineAsked : 0)}"))
            return;

        string? folder = ValueOf(args, ARGUMENT_FOLDER) is { } asked && Directory.Exists(asked) ? Path.GetFullPath(asked) : null;
        if (!selfTest && !window && folder is not null && Hex.Shell.HexInstance.Tell(DawnScene.RemotePipe, $"folder|{folder}"))
            return;

        Directory.CreateDirectory(workspace);

        var options = new HexOptions(
            Open: ValueOf(args, ARGUMENT_OPEN),
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
            Line: int.TryParse(ValueOf(args, ARGUMENT_LINE), out int line) ? line : 0,
            Folder: folder,
            Suites: ValuesOf(args, ARGUMENT_SELF_TEST),
            // The editor testing itself starts out the same size every time, unless it is told which
            Scale: float.TryParse(ValueOf(args, ARGUMENT_SCALE), CultureInfo.InvariantCulture, out float scale) ? scale : selfTest ? 1.0f : null);

        // The editor lays itself out to whatever window it gets, so it takes all there is
        Vector2? size = ParseSize(ValueOf(args, ARGUMENT_SIZE));

        using var engine = new GameEngine(GameEngineConfiguration.Default with
        {
            // A text editor, nobody plays it with a controller
            Gamepads = false,
            WindowConfiguration = WindowManagerConfiguration.Default1600x900 with
            {
                WindowTitle = "Hex",
                WindowIcon = HexIdentity.ICON,
                CustomFrame = true,
                AppId = "hex-dawn",


                Mode = selfTest ? RunMode.Game : RunMode.Application,
                Behaviour = ApplicationBehaviour.All,
                WindowSize = size ?? WindowManagerConfiguration.Default1600x900.WindowSize,
                Maximized = size is null,
                VSync = true,
                FramesPerSecond = selfTest && !options.Watch ? 240.0 : 120.0,
                UpdatesPerSecond = selfTest && !options.Watch ? 240.0 : 120.0
            }
        });

        var scene = new DawnScene(options);

        // The terminal in the same window, Ctrl+Alt+D, when Hex.exe says how to make it. Never in a self test, which
        // is the one mode and nothing else
        if (!selfTest && Other is { } other)
            Hex.Shell.HexModes.Host(Hex.Shell.HexMode.Dawn, scene, (mode, folder) => mode == Hex.Shell.HexMode.Dawn ? scene : other(folder));

#if DEBUG
        // The engine's own debugger is on F8, F9 and F10 in a Debug build, which are the C# debugger's here, so it's on
        // Pause and the rest of it on its buttons
        if (engine.Debugger is { } debugger)
        {
            debugger.ToggleKey = Silk.NET.Input.Key.Pause;
            debugger.PauseKey = null;
            debugger.StepKey = null;
        }
#endif

        engine.SetScene(scene);
        engine.Run();
        Hex.Shell.HexModes.Shutdown();

        // Where the time went, with HEX_PROFILE set
        DawnScene.Profile.Report();
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
