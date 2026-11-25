using System.Diagnostics;

using Horizon.Engine;

namespace Hex.Shell;

/// <summary>
/// Dawn and Dusk in the one window, swapped with Ctrl+Alt+Shift+D (or the toggle on the title bar, the View menu, the
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

    /// <summary>
    /// The interface size of the modes while they share the window, whichever set it last, null until one has. Each takes
    /// it up as it comes back on screen, Dawn sized itself by the window and Dusk by its own setting and the two didn't match.
    /// </summary>
    public static float? InterfaceSize { get; set; }

    /// <summary>
    /// The face the editor is to show as it comes back on screen, Noon when it was picked from Dusk, taken (and put back
    /// to null) by the editor in its OnReturned.
    /// </summary>
    public static HexFace? FaceWanted { get; set; }

    /// <summary>
    /// How a swap goes, the old mode's last frame laid over the new one and faded out (see Horizon's CrossfadeTransition),
    /// null for a cut. Asked at every swap, so it follows the interface animation setting as it changes. Set by the editor.
    /// </summary>
    public static Func<SceneTransition?>? Transition { get; set; }

    /// <summary>
    /// How long the part of a mode that changes takes to slide its last few pixels in as it comes back on screen, as the
    /// last transition handed out had it, 0 for not moving.
    /// </summary>
    public static float ArriveTime { get; set; }

    /// <summary>
    /// The other mode made and set up ahead of being asked for, at the next frame, for a pointer over its button or the
    /// keys of the swap held down. Nothing once it's made, or when the modes aren't in the one window.
    /// </summary>
    public static void Warm(HexMode mode, string? folder = null)
    {
        if (make is null || made.ContainsKey(mode) || GameEngine.Instance?.SceneManager is not { } scenes)
            return;

        Scene scene = made[mode] = make(mode, folder);
        scenes.Preload(scene);
    }

    /// <summary>Whether a mode's scene has been made, on screen, kept or set up ahead (see <see cref="Warm"/>).</summary>
    public static bool Made(HexMode mode) => made.ContainsKey(mode);

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

    // The mode a swap was asked for to bring up, -1 for whichever is the other, so a swap made by hand in between isn't undone
    private static volatile int bringing = -1;

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

    /// <summary>
    /// Asks for a mode to be put on screen on the next update of the scene that is, from any thread. Nothing when it's
    /// on screen already or the modes aren't in the one window.
    /// </summary>
    public static void Bring(HexMode mode)
    {
        if (make is null || Current == mode)
            return;

        bringing = (int)mode;
        swapWanted = true;
        GameEngine.Instance?.WindowManager?.Wake();
    }

    // What each mode does while the other is on screen, the editor's agents going on with their turns under Dusk
    private static readonly Dictionary<HexMode, Action<float>> background = [];

    /// <summary>
    /// A step a mode takes on every update of the other mode while it's out of sight, for work that mustn't wait for it to
    /// come back (agents reading their output, their calls about agents answered). Only while the modes share the window.
    /// </summary>
    public static void InBackground(HexMode mode, Action<float> step) => background[mode] = step;

    /// <summary>
    /// Every update of the scene on screen, the other mode's background step taken, then a swap the window's thread asked
    /// for, made on the thread a swap is made on.
    /// </summary>
    public static void Update(float dt)
    {
        if (make is not null && made.ContainsKey(Other) && background.TryGetValue(Other, out Action<float>? step))
            step(dt);

        if (!swapWanted)
            return;

        swapWanted = false;
        int wanted = bringing;
        bringing = -1;
        if (wanted < 0 || wanted != (int)Current)
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

    /// <summary>
    /// Forgets what a mode's scene left with this (its step in the background, its say in closing), as the scene is thrown
    /// away with the program going on, so nothing holds on to it or calls it.
    /// </summary>
    public static void Forget(HexMode mode)
    {
        background.Remove(mode);
        closing.Remove(mode);
        if (closing.Count == 0 && GameEngine.Instance?.WindowManager is { } window)
            window.CloseRequested = null;
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
        bool fresh = !made.TryGetValue(to, out Scene? scene);
        if (scene is null)
            made[to] = scene = make(to, folder);

        // Making a mode leaves the garbage of setting it up, taken under the cover as the engine does for any scene. Going
        // back to one that's already there makes none, and the same blocking, compacting collection on every swap was a
        // pause for nothing each time Ctrl+Alt+Shift+D was pressed
        scenes.CollectAfterChange = fresh;
        Current = to;
        scenes.SetScene(scene, Transition?.Invoke());
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

    /// <summary>
    /// Injectable restart launcher for unit and self tests. When set, <see cref="Restart"/> calls this instead of
    /// spawning an external OS process.
    /// </summary>
    internal static Func<HexMode, string?, bool>? RestartLauncher { get; set; }

    /// <summary>
    /// Starts a fresh Hex process in <paramref name="mode"/> (preserving <paramref name="folder"/> when open) and
    /// invokes <paramref name="closeWindow"/> when launch succeeds so the current process exits cleanly.
    /// </summary>
    public static bool Restart(HexMode mode, string? folder, Action closeWindow)
    {
        ArgumentNullException.ThrowIfNull(closeWindow);
        bool started = RestartLauncher is { } launcher
            ? launcher(mode, folder)
            : Start(mode, folder);
        if (!started)
            return false;

        closeWindow();
        return true;
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
