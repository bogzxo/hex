using System.Collections.Concurrent;
using System.Runtime;
using System.Runtime.InteropServices;

using Horizon.Engine;

namespace Hex.Dawn;

/// <summary>
/// Hex staying running between uses (the stay_running setting). The window's x puts the editor away whole, everything it
/// had going stopped and its scene thrown away, and leaves the engine with its device and the window, hidden, under an
/// empty scene that does nothing at all: no ticks, no frames, the memory given back. Starting Hex again (or a file or a
/// folder sent to it) makes a new editor in the same process, the device and the window already there and every bit of
/// code already loaded, and shows the window again. The pipe other starts talk down is the process's, see DawnScene.Remote.
/// </summary>
internal static class DawnResident
{
    // How the program makes the editor and hosts it with Dusk, and what the editor of a new start is made with
    private static Func<HexOptions, DawnScene>? make;
    private static HexOptions? options;

    // What other starts asked for while asleep, taken by the empty scene on its next tick
    private static readonly ConcurrentQueue<string> asked = new();

    /// <summary>What a start with the system says to find out whether a Hex is running, which nobody acts on.</summary>
    public const string PING = "ping";

    /// <summary>Whether Hex is asleep, its window hidden and no editor in it.</summary>
    public static bool Asleep { get; private set; }

    /// <summary>The scene of a start with the system, asleep from the first, the pipe heard by it.</summary>
    public static Scene StartAsleep()
    {
        Asleep = true;
        DawnScene.Remote.Hear(Heard);
        return new DormantScene();
    }

    /// <summary>Set as the editor of a wake up is made, so it shows the window once it has something to show in it.</summary>
    public static bool Waking { get; set; }

    /// <summary>Whether this run can go to sleep, the program having said how to make another editor.</summary>
    public static bool Possible => make is not null;

    /// <summary>Called once by the program, how a new editor is made (and hosted with Dusk), and with what.</summary>
    public static void Prepare(Func<HexOptions, DawnScene> makeEditor, HexOptions startedWith)
    {
        make = makeEditor;
        // A new editor starts on the workspace of last time, nothing of this start's command line
        options = startedWith with { Open = null, Files = null, Line = 0, Folder = null, Fresh = false, Browse = false, View = null, Console = null, Face = null };
    }

    /// <summary>
    /// Puts the editor away and Hex to sleep. Called by the editor on its own thread once it has stopped what it had going
    /// (see DawnScene.Unload). The window is hidden first, the swap to the empty scene throws the editor away.
    /// </summary>
    public static void Sleep(GameEngine engine)
    {
        Asleep = true;
        engine.WindowManager.Hide();
        DawnScene.Remote.Hear(Heard);

        // Dusk in the window goes as well, and with nothing hosting the two the editor isn't kept when it's left
        Hex.Shell.HexModes.Shutdown();
        engine.SceneManager.CollectAfterChange = false;
        engine.SetScene(new DormantScene(), null);
    }

    // A line from another start while asleep, from the pipe's thread
    private static void Heard(string line)
    {
        if (line == PING)
            return;
        asked.Enqueue(line);
        GameEngine.Instance?.WindowManager.Wake();
    }

    /// <summary>The empty scene's tick: whatever was asked wakes Hex with a new editor.</summary>
    internal static void Update(GameEngine engine)
    {
        if (!asked.TryDequeue(out string? first) || make is null || options is null)
            return;

        HexOptions wake = options;
        var files = new List<HexLaunchFile>();
        for (string? line = first; line is not null; line = asked.TryDequeue(out string? next) ? next : null)
        {
            string[] parts = line.Split('|');
            if (parts is ["folder", var folder, ..] && Directory.Exists(folder))
                wake = wake with { Folder = Path.GetFullPath(folder) };
            else if (parts is ["open", var path, ..] && path.Length > 0)
                files.Add(new HexLaunchFile(path, parts.Length > 2 && int.TryParse(parts[2], out int at) ? at : 0, parts.Length > 3 && int.TryParse(parts[3], out int across) ? across : 0));
        }

        if (files.Count > 0)
            wake = wake with { Open = files[^1].Path, Line = files[^1].Line, Files = [.. files] };

        Asleep = false;
        Waking = true;
        engine.WindowManager.Dormant = false;
        engine.SceneManager.CollectAfterChange = false;
        engine.SetScene(make(wake), null);
    }

    /// <summary>The empty scene is in, the editor gone: nothing ticks from now on, and what memory can be is given back.</summary>
    internal static void Settled(GameEngine engine)
    {
        // Hidden already, said again so the engine holds its frames (a start with the system never showed it at all)
        engine.WindowManager.Hide();
        engine.WindowManager.Dormant = true;
        DawnScene.OffThread("giving memory back while asleep", () =>
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            Console.WriteLine($"[Hex] asleep, {GC.GetTotalMemory(false) / (1024 * 1024)} MB of objects kept, {GC.GetGCMemoryInfo().TotalCommittedBytes / (1024 * 1024)} MB committed");
            if (Asleep && OperatingSystem.IsWindows())
                SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, -1, -1);
        });
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(nint process, nint least, nint most);
}

/// <summary>What's on screen while Hex sleeps, nothing, see DawnResident.</summary>
internal sealed class DormantScene : Scene
{
    private bool settled;

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);
        if (!settled)
        {
            settled = true;
            DawnResident.Settled(Engine);
        }

        DawnResident.Update(Engine);
    }
}
