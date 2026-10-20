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
        if (args.Any(arg => arg is "--help" or "-h" or "-?" or "/?" or "/h"))
        {
            // A Release Hex is a window with no console of its own, the help goes to the one it was typed into
            if (OperatingSystem.IsWindows() && !Console.IsOutputRedirected)
                AttachConsole(ATTACH_PARENT_PROCESS);
            Console.WriteLine();
            Console.WriteLine(HELP);
            return 0;
        }

        // Started from the Dock or a desktop's launcher, the PATH a terminal would have, off this thread
        Horizon.Core.Processes.ProcessEnvironment.ImportLoginShell();

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
            while (!HexInstance.Tell(HexScene.RemotePipe, $"open|{file}|0"))
            {
                if (DateTime.UtcNow > giveUp)
                    return;
                Thread.Sleep(250);
            }
        }
    }

    private const int ATTACH_PARENT_PROCESS = -1;

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

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
          --scale <n>              how big the UI is drawn
          --size <w>x<h>           the window's size
          --selftest [part...]     go through the terminal and say what works, --exit to leave after

        Both keep their settings in the settings folder next to Hex.exe. Inside either, Ctrl+Shift+P is everything
        there is to do, and in Dusk, tui typed at a prompt lists the programs with a screen of their own.
        """;
}
