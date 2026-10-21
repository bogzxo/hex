using System.Globalization;
using System.Numerics;

using Hex.Shell;

using Horizon.Core;
using Horizon.Engine;

namespace Hex.Dusk;

public static class DuskProgram
{
    private const string ARGUMENT_SIZE = "--size";
    private const string ARGUMENT_SELF_TEST = "--selftest";
    private const string ARGUMENT_EXIT = "--exit";
    private const string ARGUMENT_WATCH = "--watch";
    private const string ARGUMENT_PROFILE = "--profile";
    private const string ARGUMENT_CWD = "--cwd";
    private const string ARGUMENT_SCALE = "--scale";
    private const string ARGUMENT_APP = "--app";
    private const string ARGUMENT_REAL_KEYS = "--realkeys";
    private const string ARGUMENT_TOGGLE = "--toggle";

    /// <summary>
    /// Starts the terminal. Without arguments it comes up maximised with the machine's usual shell in a tab.
    /// <list type="bullet">
    /// <item><c>--profile cmd</c> starts with that profile (its id, pwsh, powershell, cmd, git_bash, wsl:Ubuntu) rather than the settings' one.</item>
    /// <item><c>--cwd folder</c> is where the first shell starts, the folder Dusk was started from otherwise.</item>
    /// <item><c>--size 1280x720</c> starts in a window of that size rather than maximised.</item>
    /// <item><c>--scale 1.25</c> draws the window's own parts that much bigger.</item>
    /// <item><c>--selftest</c> opens tabs, splits them, runs the commands and prints how that went, <c>--watch</c> slowly enough to follow.
    /// <c>--selftest keys shell</c> runs only those parts of it (start, plugins, tabs, splits, mouse, palette, themes, closing, shell, keys, history).
    /// <c>--app</c> runs it the way Dusk really runs, as an application that sleeps between frames, rather than as a game.
    /// The parts that press real keys (keys, history, repl) send them only while Dusk is the window in front, and run only
    /// when named or with <c>--realkeys</c>, so a plain run never types into anything of anybody's.</item>
    /// <item><c>--exit</c> closes once the self test is done, with an exit code of 1 if anything failed.</item>
    /// <item><c>--toggle</c> drops the Dusk that's running down from the top of the screen, or takes it back up, and leaves,
    /// for a key of the desktop's own where there's no key for everywhere of Hex's (Linux and macOS): bind
    /// <c>hex --dusk --toggle</c> in the keyboard settings. With no Dusk running it starts one.</item>
    /// </list>
    /// </summary>
    public static void Run(string[] args)
    {
        bool selfTest = args.Contains(ARGUMENT_SELF_TEST);

        // A folder that was named is meant from where Dusk was started, the rest (its skin, fonts, plugins) is next to it
        string startIn = ValueOf(args, ARGUMENT_CWD) is { } named ? Path.GetFullPath(named) : Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);

        // The self test keeps a home of its own, made new every run, so it never reads or writes whoever's settings,
        // keys, plugins, history or tabs, and can write those files itself to see them taken in
        if (selfTest)
        {
            string home = Path.Combine(Path.GetTempPath(), "dusk-selftest-home");
            if (Directory.Exists(home))
                Directory.Delete(home, recursive: true);
            Directory.CreateDirectory(home);
            Environment.SetEnvironmentVariable("DUSK_HOME", home);
            Environment.SetEnvironmentVariable("DUSK_HEX", "none");
        }

        // The drop down, for a key of the desktop's, said to the Dusk that's running
        if (!selfTest && args.Contains(ARGUMENT_TOGGLE) && Hex.Shell.HexInstance.Tell(Hex.Shell.HexMode.Dusk, "toggle"))
            return;

        // A Dusk running already takes it as a tab of its own, and this one leaves
        if (!selfTest && Hex.Shell.HexInstance.Tell(Hex.Shell.HexMode.Dusk, $"tab|{startIn}|{ValueOf(args, ARGUMENT_PROFILE) ?? string.Empty}"))
            return;

        var options = new DuskOptions(
            SelfTest: selfTest,
            Exit: args.Contains(ARGUMENT_EXIT),
            Watch: args.Contains(ARGUMENT_WATCH),
            Profile: ValueOf(args, ARGUMENT_PROFILE),
            StartIn: startIn,
            Scale: float.TryParse(ValueOf(args, ARGUMENT_SCALE), CultureInfo.InvariantCulture, out float scale) ? scale : null,
            Suites: ValuesOf(args, ARGUMENT_SELF_TEST),
            App: args.Contains(ARGUMENT_APP),
            RealKeys: args.Contains(ARGUMENT_REAL_KEYS));

        Vector2? size = ParseSize(ValueOf(args, ARGUMENT_SIZE));

        using var engine = new GameEngine(GameEngineConfiguration.Default with
        {
            // A terminal, nobody plays it with a controller
            Gamepads = false,
            // Its settings and caches next to Hex.exe, Program.Main says so already and this says it to the engine
            FilesBesideProgram = true,
            WindowConfiguration = WindowManagerConfiguration.Default1600x900 with
            {
                WindowTitle = "Dusk",
                WindowIcon = HexIdentity.ICON,
                CustomFrame = true,
                AppId = "hex-dusk",
                Mode = selfTest && !options.App ? RunMode.Game : RunMode.Application,
                Behaviour = ApplicationBehaviour.All,
                WindowSize = size ?? WindowManagerConfiguration.Default1600x900.WindowSize,
                Maximized = size is null,
                VSync = true,
                FramesPerSecond = selfTest && !options.Watch ? 240.0 : 120.0,
                UpdatesPerSecond = selfTest && !options.Watch ? 240.0 : 120.0
            }
        });

        var scene = new DuskScene(options);

        // The editor in the same window, Ctrl+Alt+D, when Hex.exe says how to make it, never in a self test
        if (!selfTest && Other is { } other)
            Hex.Shell.HexModes.Host(Hex.Shell.HexMode.Dusk, scene, (mode, folder) => mode == Hex.Shell.HexMode.Dusk ? scene : other(folder));

        engine.SetScene(scene);
        engine.Run();
        Hex.Shell.HexModes.Shutdown();
    }

    /// <summary>How Hex.exe makes the editor for the same window, on a folder, see Hex.Shell.HexModes.</summary>
    public static Func<string?, Scene>? Other { get; set; }

    /// <summary>The terminal as a scene of its own, for the window the editor is in, its first tab in a folder (null for here).</summary>
    public static Scene Scene(string? folder) =>
        new DuskScene(new DuskOptions(
            SelfTest: false,
            Exit: false,
            Watch: false,
            Profile: null,
            StartIn: folder is not null && Directory.Exists(folder) ? Path.GetFullPath(folder) : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Scale: null));

    private static string? ValueOf(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>Everything that follows an argument up to the next one that starts with two dashes.</summary>
    private static string[] ValuesOf(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index < 0 ? [] : [.. args.Skip(index + 1).TakeWhile(value => !value.StartsWith("--"))];
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

/// <summary>
/// What Dusk was started with, see <see cref="Program"/> for how each is asked for.
/// </summary>
/// <param name="SelfTest">Whether Dusk works through itself and prints how that went.</param>
/// <param name="Exit">Whether it closes once the self test is done.</param>
/// <param name="Watch">Whether the self test goes slowly enough to follow.</param>
/// <param name="Profile">The id of the profile the first tab opens with, null for the settings' one.</param>
/// <param name="StartIn">Where the first shell starts.</param>
/// <param name="Scale">How big the window's own parts are drawn, null for what the settings say.</param>
/// <param name="Suites">Which parts of the self test to run, empty for all of them.</param>
/// <param name="App">Whether the self test runs in application mode, as Dusk really runs.</param>
/// <param name="RealKeys">Whether the parts of the self test that press real keys run without being named, keys, history and repl.</param>
internal sealed record DuskOptions(bool SelfTest, bool Exit, bool Watch, string? Profile, string StartIn, float? Scale, string[]? Suites = null, bool App = false, bool RealKeys = false);
