using System.Runtime.InteropServices;

using Hex.Dawn;
using Hex.Dusk;
using Hex.Shell;

namespace Hex;

/// <summary>
/// Hex, one program and two of them. Started plainly it is Dawn, the editor (see DawnProgram for what it takes), started
/// with <c>--dusk</c> or as an exe called dusk it is Dusk, the terminal (see DuskProgram). Each says to Windows which it
/// is before its window is made, so the taskbar has a button and a pin for each.
/// <para>
/// A path on its own is what was dropped on Hex.exe (or typed after it), a file opened in Dawn and a folder opened as its
/// workspace, or for Dusk the folder its first terminal starts in. <c>--help</c> says all of that.
/// </para>
/// </summary>
internal static partial class Program
{
    public static int Main(string[] args)
    {
        // Everything Hex keeps (settings, backups, compiled shaders, the driver's pipeline cache) goes next to the exe,
        // before anything asks where. A copy carried about on a stick has it all with it
        Horizon.Core.Platform.PlatformPaths.BesideProgram = true;
        Horizon.Core.Diagnostics.StartupTrace.Mark("main");

        // The old exe and dlls a build of Hex over itself moved aside go, see HexSelfBuild
        Hex.Core.Projects.HexSelfBuild.CleanUpSoon();

        if (args.Any(arg => arg is "--help" or "-h" or "-?" or "/?" or "/h"))
        {
            if (OperatingSystem.IsWindows())
                ToTheParentConsole();
            Console.WriteLine();
            Console.WriteLine(HELP);
            Console.Out.Flush();
            return 0;
        }

        // HIDL's language server on stdin and stdout, for an editor to start (VS Code, or Hex's own settings), no window
        if (args.Contains("--lsp"))
            return Hex.Core.HidlServer.Serve();

        // An MCP client's way in, its stdin and stdout passed to the Hex that has the folder open (started if none has),
        // no window of its own. Typed at a prompt rather than started by a client it's Hex with the server on instead
        if (args.Contains("--mcp") && !args.Contains("--window") && Hex.Core.McpBridge.StdinIsAClient())
            return Hex.Core.McpBridge.Run(args, Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Hex.exe"));

#if DEBUG
        // A Debug Hex's console for its log, made here now Windows doesn't hand one out by itself (app.manifest)
        if (OperatingSystem.IsWindows())
            ConsoleForTheLog(args);
#endif

        // Started from the Dock or a desktop's launcher, the PATH a terminal would have, off this thread
        Horizon.Core.Processes.ProcessEnvironment.ImportLoginShell();

        // The terminal's components known to layouts before the first module is made, both modes have them
        Horizon.UI.Components.TerminalKinds.Register();

        HexMode mode = HexIdentity.ModeOf(args, Environment.ProcessPath);
        HexIdentity.Claim(mode);

        string[] rest = [.. args.Where(arg => !arg.Equals("--dusk", StringComparison.OrdinalIgnoreCase))];
        rest = Dropped(rest, mode, out List<string> more);

        // Either mode can swap to the other in its own window (Ctrl+Alt+D), each made the first time it's wanted
        DawnProgram.Other = DuskProgram.Scene;
        DuskProgram.Other = DawnProgram.Scene;

        if (mode == HexMode.Dusk)
        {
            DuskProgram.Run(rest);
        }
        else
        {
            // more than one file dropped, the rest go to the Dawn that opens the first once it's listening
            if (more.Count > 0)
                _ = Task.Run(() => HandOver(more));
            DawnProgram.Run(rest);
        }

        return Environment.ExitCode;
    }

    /// <summary>
    /// The paths on their own made into what the modes take, the first file <c>--open</c> and a folder <c>--folder</c> for
    /// Dawn, a folder <c>--cwd</c> for Dusk. A path is what comes before the first option, so never an option's value
    /// (<c>--size 1600x900</c>). Made whole here, both modes go and work in the exe's own folder.
    /// </summary>
    private static string[] Dropped(string[] args, HexMode mode, out List<string> more)
    {
        more = [];

        // Dawn reads its own paths, anywhere in the line and with a line and column after them, see HexLaunch
        if (mode == HexMode.Dawn)
            return args;

        int first = Array.FindIndex(args, arg => arg.StartsWith('-'));
        if (first < 0)
            first = args.Length;
        var kept = new List<string>(args[first..]);

        string? file = null, folder = null;
        foreach (string path in args[..first])
        {
            string full;
            try
            {
                full = Path.GetFullPath(path.Trim('"'));
            }
            catch (Exception)
            {
                continue;
            }

            if (Directory.Exists(full))
                folder ??= full;
            else if (File.Exists(full) && mode == HexMode.Dawn)
            {
                if (file is null)
                    file = full;
                else
                    more.Add(full);
            }
        }

        if (mode == HexMode.Dusk)
        {
            if (folder is not null && !kept.Contains("--cwd"))
                kept.AddRange(["--cwd", folder]);
            return [.. kept];
        }

        if (file is not null && !kept.Contains("--open"))
            kept.AddRange(["--open", file]);
        if (folder is not null && !kept.Contains("--folder"))
            kept.AddRange(["--folder", folder]);
        return [.. kept];
    }

    // The files after the first one dropped, told to Dawn as soon as it's listening, which takes a moment as it starts
    private static void HandOver(List<string> files)
    {
        DateTime giveUp = DateTime.UtcNow.AddSeconds(20);
        foreach (string file in files)
        {
            while (!HexInstance.Tell(DawnScene.RemotePipe, $"open|{file}|0"))
            {
                if (DateTime.UtcNow > giveUp)
                    return;
                Thread.Sleep(250);
            }
        }
    }

    /// <summary>
    /// A Release Hex is a window with no console of its own, so the help goes to the one it was typed into. It has no
    /// standard out at all then, which .NET calls redirected (asking Console.IsOutputRedirected was why --help printed
    /// nothing), so it's the handle itself that's looked at. One that's there is a pipe or a file and is left be.
    /// </summary>
    private static void ToTheParentConsole()
    {
        nint output = GetStdHandle(STD_OUTPUT_HANDLE);
        if (output != 0 && output != -1)
            return;
        if (!AttachConsole(ATTACH_PARENT_PROCESS))
            return;

        // The console it's on now, written to straight, the std handles a GUI exe starts with stay empty after attaching
        var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(CreateFileW("CONOUT$", GENERIC_WRITE, FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0), true);
        if (handle.IsInvalid)
            return;
        Console.SetOut(new StreamWriter(new FileStream(handle, FileAccess.Write)) { AutoFlush = true });
    }

    /// <summary>
    /// A console for a Debug Hex's log when it was started with none to inherit and nowhere else for its output to go,
    /// from Explorer or a shortcut, the way it always had one. The manifest says detached so a console isn't made for
    /// every start, the bridge of an MCP client and the default terminal's -Embedding start among them, both of which
    /// handed their console to Dusk and left a stray one about.
    /// </summary>
    private static void ConsoleForTheLog(string[] args)
    {
        if (args.Any(arg => arg is "--mcp" or "--lsp" or "-Embedding" or "/Embedding") || GetConsoleWindow() != 0)
            return;

        nint output = GetStdHandle(STD_OUTPUT_HANDLE);
        if (output == 0 || output == -1)
            AllocConsole();
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetConsoleWindow();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllocConsole();

    private const int ATTACH_PARENT_PROCESS = -1, STD_OUTPUT_HANDLE = -11;
    private const uint GENERIC_WRITE = 0x40000000, FILE_SHARE_WRITE = 0x2, OPEN_EXISTING = 3;

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int which);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    private const string HELP = """
        Hex, Dawn the editor and Dusk the terminal, one program.

          Hex [file...] [folder] [options]       Dawn, the files opened and the folder as its workspace
          Hex --dusk [folder] [options]          Dusk, its first terminal started in the folder

        Dropping files or a folder on Hex.exe is the same as naming them. A file goes to the Dawn that's running
        already when there is one, unless --window says to open a window of its own.

        Dawn
          --open <file>            a file to open (what a path on its own is)
          --line <n>               the line to put the caret on in it
          --folder <folder>        a folder to open as the workspace (what a folder on its own is)
          --window                 a window of its own rather than the Dawn that's running
          --view <design|code|table>  which view a file comes up in
          --workspace <folder>     where layouts go when nobody says otherwise
          --scale <n>              how big the UI is drawn, 1 as it comes
          --size <w>x<h>           the window's size, maximised otherwise
          --check <file...>        check that layouts load and come back out the same, say so, and leave
          --selftest [part...]     click through the editor and say what works, --exit to leave after

        Dusk
          --dusk                   Dusk rather than Dawn (an exe called dusk.exe is Dusk too)
          --cwd <folder>           the folder the first terminal starts in (what a folder on its own is)
          --profile <id>           which shell the first terminal is
          --window                 a window of its own rather than a tab of the Dusk that's running
          --scale <n>              how big the UI is drawn
          --size <w>x<h>           the window's size
          --selftest [part...]     go through the terminal and say what works, --exit to leave after

        HIDL
          --lsp                    HIDL's language server on stdin and stdout, for an editor to start, no window

        Agents (MCP)
          --mcp                    started by an MCP client, the way in to the Hex that has the folder open (one is
                                   started when none has), stdin and stdout the client's. Typed at a prompt or with
                                   --window, Dawn with its MCP server on whatever the settings say
          --folder <folder>        with --mcp, the folder it's for, where it was started otherwise

        Both keep their settings in the settings folder next to Hex.exe. Inside either, Ctrl+Shift+P is everything
        there is to do, and in Dusk, tui typed at a prompt lists the programs with a screen of their own.
        """;
}
