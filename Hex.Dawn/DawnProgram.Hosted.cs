using Horizon.Core;
using Horizon.Engine;

namespace Hex.Dawn;

// Dawn started by a host that makes the engine itself, Hex.Android's activity. Run above is the desktop's way in, with
// its arguments, the Dawn that's running already and the exe's folder. A host has none of that, it hands over the
// configuration it would use, makes the engine and asks for the scene, then tells Dawn when the engine is gone.
public static partial class DawnProgram
{
    /// <summary>
    /// How the native self test finds the desktop's own window to post keys to. Hex.exe sets it, Dawn never names the
    /// desktop platform itself so the same Dawn runs on a tablet.
    /// </summary>
    public static Func<GameEngine, nint>? NativeWindow { get; set; }

    /// <summary>
    /// The editor's settings over the host's own (its screen size, its fullscreen). An application rather than a game,
    /// it draws when something changes and otherwise waits, the same as on a desktop.
    /// </summary>
    public static GameEngineConfiguration Hosted(GameEngineConfiguration host) => host with
    {
        // A text editor, nobody plays it with a controller
        Gamepads = false,
        WindowConfiguration = host.WindowConfiguration with
        {
            WindowTitle = "Hex",
            AppId = "hex-dawn",
            Mode = RunMode.Application,
            Behaviour = ApplicationBehaviour.All,
            VSync = true,
            FramesPerSecond = 120.0,
            UpdatesPerSecond = 120.0
        }
    };

    /// <summary>
    /// The editor, on a workspace folder of the host's (where layouts and new files go) and a folder to open in the
    /// explorer, null for the one it had open the last time. Call it once the engine is made.
    /// </summary>
    public static Scene HostedScene(GameEngine engine, string workspace, string? folder, string? open = null)
    {
        // F12 is go to definition here as on a desktop, never a screenshot
        engine.ScreenshotKey = null;

        Directory.CreateDirectory(workspace);
        return new DawnScene(new HexOptions(
            Open: open is not null && File.Exists(open) ? Path.GetFullPath(open) : null,
            Workspace: Path.GetFullPath(workspace),
            SelfTest: false,
            Wheel: false,
            Check: [],
            Exit: false,
            Folder: folder is not null && Directory.Exists(folder) ? Path.GetFullPath(folder) : null));
    }

    /// <summary>
    /// The editor testing itself on a host, the suites named (all of them for none), in a folder of the host's own that
    /// is emptied first. It leaves once it's done when <paramref name="exit"/> says so, with the engine's exit code 1 if
    /// anything failed, the same as <c>--selftest --exit</c>.
    /// </summary>
    public static Scene HostedSelfTest(GameEngine engine, string folder, string[] suites, bool exit)
    {
        engine.ScreenshotKey = null;
        TryClear(folder);
        Directory.CreateDirectory(folder);
        return new DawnScene(new HexOptions(
            Open: null,
            Workspace: Path.GetFullPath(folder),
            SelfTest: true,
            Wheel: false,
            Check: [],
            Exit: exit,
            Suites: suites,
            Scale: 1.0f));
    }

    // Set from the host's thread when the app goes to the back, taken by the editor on its next tick, see DawnScene.Hosted
    internal static volatile bool flushWanted;

    /// <summary>
    /// The app is going to the back, where Android may end its process without a word. What isn't saved is written to
    /// the backups now rather than at the next two second look, and the open files and the workspace kept, on the
    /// editor's own thread on its next tick. From any thread.
    /// </summary>
    public static void HostedLeaving(GameEngine engine)
    {
        flushWanted = true;
        engine.WindowManager.Wake();
    }

    /// <summary>What goes with the editor once the host's engine has stopped, the same as after Run.</summary>
    public static void HostedStopped()
    {
        Hex.Shell.HexModes.Shutdown();
        Horizon.UI.Scripting.LanguageServers.StopAll();
    }
}
