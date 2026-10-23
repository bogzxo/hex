using System.Diagnostics;

using Horizon.Engine;

namespace Hex.Shell;

/// <summary>
/// Dawn and Dusk in the one window, swapped with Ctrl+Alt+D (or the toggle on the title bar, the View menu, the
/// palette). Hex.exe hands this the way to make each mode's scene (<see cref="Host"/>), and a swap sets the other one,
/// made the first time it's asked for and kept from then on, so the editor's files and the terminal's shells are where
/// they were left when it comes back (both scenes are <see cref="Scene.Persistent"/> while this hosts them). Nothing
/// hosting them (a self test, a mode started on its own by something else), a swap starts the other mode as a program.
/// </summary>
public static class HexModes
{
    private static Func<HexMode, string?, Scene>? make;
    private static readonly Dictionary<HexMode, Scene> made = [];

    /// <summary>Which mode is on screen.</summary>
    public static HexMode Current { get; private set; }

    /// <summary>Whether both modes live in the one window, which is what makes their scenes persistent.</summary>
    public static bool Together => make is not null;

    /// <summary>Called on the scene that is about to be left for the other mode, to let go of the keyboard and the pointer.</summary>
    public static event Action<HexMode>? Leaving;

    /// <summary>Has the modes swapped in this window from now on, the scene of each made by <paramref name="scene"/> (in a folder, null for wherever).</summary>
    public static void Host(HexMode first, Scene shown, Func<HexMode, string?, Scene> scene)
    {
        make = scene;
        Current = first;
        made[first] = shown;
    }

    // What each mode says when the window's x is pressed, true to let it close (see OnClose), and a swap the window's
    // thread asked for, made on the next update of the scene on screen (see Update)
    private static readonly Dictionary<HexMode, Func<bool>> closing = [];
    private static volatile bool swapWanted;

    /// <summary>
    /// What a mode says when the window is to close, true to let it, false having asked its question (unsaved files,
    /// tabs with shells). With both modes in the window both are asked, the one on screen first, and one that's out of
    /// sight and says not yet is swapped to so its question can be seen. Window thread.
    /// </summary>
    public static void OnClose(HexMode mode, Func<bool> mayClose)
    {
        closing[mode] = mayClose;
        if (GameEngine.Instance?.WindowManager is { } window)
            window.CloseRequested = MayClose;
    }

    private static bool MayClose()
    {
        if (closing.TryGetValue(Current, out Func<bool>? here) && !here())
            return false;

        foreach (var (mode, mayClose) in closing)
        {
            if (mode != Current && made.ContainsKey(mode) && !mayClose())
            {
                swapWanted = true;
                GameEngine.Instance?.WindowManager?.Wake();
                return false;
            }
        }

        return true;
    }

    /// <summary>Every update of the scene on screen, a swap the window's thread asked for, made on the thread a swap is made on.</summary>
    public static void Update()
    {
        if (!swapWanted)
            return;

        swapWanted = false;
        Swap();
    }

    /// <summary>
    /// Disposes of the mode that's out of sight, once the window has closed and before the device goes. The scene on
    /// screen goes the way it always did, the other one nobody else knows about, and its shells would outlive the window.
    /// </summary>
    public static void Shutdown()
    {
        foreach (var (mode, scene) in made)
        {
            if (mode != Current && !scene.IsDisposed)
                scene.Dispose();
        }

        made.Clear();
        make = null;
    }

    /// <summary>The other mode.</summary>
    public static HexMode Other => Current == HexMode.Dawn ? HexMode.Dusk : HexMode.Dawn;

    /// <summary>
    /// Swaps to the other mode, in this window when the modes are hosted together, otherwise as a program of its own
    /// started in <paramref name="folder"/>. False when there was nothing to swap to.
    /// </summary>
    public static bool Swap(string? folder = null)
    {
        HexMode to = Other;
        if (make is null || GameEngine.Instance?.SceneManager is not { } scenes)
            return Start(to, folder);

        Leaving?.Invoke(Current);
        if (!made.TryGetValue(to, out Scene? scene))
            made[to] = scene = make(to, folder);

        Current = to;
        scenes.SetScene(scene, null);
        return true;
    }

    /// <summary>
    /// A Dusk window of its own on a folder, its first tab in a profile (its id, null for the usual one) with a command
    /// line typed into it once its shell is up. The Dusk that's running takes it as a new tab instead, when there is one.
    /// </summary>
    public static bool StartDusk(string folder, string? profile, string? run)
    {
        if (Environment.ProcessPath is not { } exe)
            return false;

        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            start.ArgumentList.Add("--dusk");
            start.ArgumentList.Add("--cwd");
            start.ArgumentList.Add(folder);
            if (profile is { Length: > 0 })
            {
                start.ArgumentList.Add("--profile");
                start.ArgumentList.Add(profile);
            }

            if (run is { Length: > 0 })
            {
                start.ArgumentList.Add("--run");
                start.ArgumentList.Add(run);
            }

            Process.Start(start);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Helper method to start a mode as a program of its own, Hex.exe with --dusk for the terminal.</summary>
    private static bool Start(HexMode mode, string? folder)
    {
        if (Environment.ProcessPath is not { } exe)
            return false;

        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            if (mode == HexMode.Dusk)
            {
                start.ArgumentList.Add("--dusk");
                if (folder is not null)
                {
                    start.ArgumentList.Add("--cwd");
                    start.ArgumentList.Add(folder);
                }
            }
            else if (folder is not null)
            {
                start.ArgumentList.Add("--folder");
                start.ArgumentList.Add(folder);
            }

            Process.Start(start);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
