using System.Diagnostics;
using System.Text.RegularExpressions;

using Horizon.UI.Scripting;

namespace Hex.Dawn;

// Debugging D through LLVM's lldb-dap, the debugger VS Code's lldb-dap extension is. F5 on a .d file with a breakpoint in
// one builds it with -g where its run task would (a lone file next to itself with dmd -i, a dub project with dub build),
// what the build says wrong goes in Problems the way C#'s does, and the program runs under lldb-dap stopping at the
// breakpoints, the same steps and stack and variables C# has. Without a breakpoint F5 runs it in the terminal as before.
// lldb-dap is looked for on the PATH and where LLVM installs itself.
internal sealed partial class HexDebugging
{
    // What lldb-dap's launch is the kind of
    private const string LLDB = "lldb-dap";

    internal static bool IsD(string? path) => path is not null && Path.GetExtension(path).ToLowerInvariant() is ".d" or ".di";

    /// <summary>Whether a file is one there's a debugger for here, C# with netcoredbg, D with lldb-dap.</summary>
    internal bool CanDebug(string? path) => (IsCSharp(path) && Netcoredbg() is not null) || (IsD(path) && LldbDap() is not null);

    /// <summary>Whether any D file has a breakpoint, what has F5 debug a D file rather than run it.</summary>
    internal bool DBreakpoints() => breakpoints.Any(pair => IsD(pair.Key) && pair.Value.Count > 0);

    /// <summary>Where lldb-dap is, the PATH first then where LLVM puts itself, null when it's nowhere.</summary>
    internal static string? LldbDap()
    {
        string exe = OperatingSystem.IsWindows() ? "lldb-dap.exe" : "lldb-dap";
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string there = Path.Combine(folder.Trim('"'), exe);
            if (File.Exists(there))
                return there;
        }

        string[] known = OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LLVM", "bin", exe)]
            : ["/opt/homebrew/opt/llvm/bin/lldb-dap", "/usr/local/opt/llvm/bin/lldb-dap", "/usr/bin/lldb-dap", "/usr/local/bin/lldb-dap"];
        return known.FirstOrDefault(File.Exists);
    }

    /// <summary>A D file built with -g and run under lldb-dap with the breakpoints there are, off the editor's thread.</summary>
    private void StartDebuggingD(string file)
    {
        if (LldbDap() is not { } debugger)
        {
            context.Notices.Say(context.Strings.Said("debugging.no_lldb"), error: true);
            return;
        }

        SaveAllCode();
        string entry = DEntryOf(file);
        lastDEntry = entry;
        building = true;
        debuggedProject = entry;
        restart = () => StartDebuggingD(entry);
        context.Panels.ShowOutput();
        context.Notices.Say(context.Strings.Said("debugging.building", ("project", Path.GetFileName(entry))));

        var breakpointsNow = breakpoints.Where(pair => pair.Value.Count > 0).ToDictionary(pair => pair.Key, pair => (IReadOnlyCollection<int>)pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        _ = Task.Run(() => BuildAndLaunchD(entry, debugger, breakpointsNow));
    }

    // The D file debugged last that has main, F5 in a module of it debugs it again
    private string? lastDEntry;

    // A main of D's, void or int, extern(C) or not
    private static readonly Regex MainDeclared = new(@"^\s*(?:extern\s*\(\s*C\s*\)\s*)?(?:void|int|noreturn)\s+main\s*\(", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// The file a D program starts from that a file is part of, what F5 in it builds. A dub project's file is its own (dub
    /// knows the rest), a file with main is, the one debugged last when this file is under its folder, then the first
    /// file with main in its folder or up to three over it (not past the workspace). F5 in util.d built util.d alone,
    /// and the linker said "entry point must be defined".
    /// </summary>
    internal string DEntryOf(string file)
    {
        string full = Path.GetFullPath(file);
        if (HexLanguages.DubProjectOf(full) is not null || HasMain(full))
            return full;

        if (lastDEntry is { } last && File.Exists(last) && full.StartsWith(Path.GetDirectoryName(last)! + Path.DirectorySeparatorChar, PlatformPaths.Comparison) && HasMain(last))
            return last;

        string? folder = Path.GetDirectoryName(full);
        for (int up = 0; up < 4 && folder is not null; up++, folder = Path.GetDirectoryName(folder))
        {
            try
            {
                foreach (string other in Directory.EnumerateFiles(folder, "*.d").Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (HasMain(other))
                        return other;
                }
            }
            catch (IOException)
            {
                break;
            }

            if (context.Workspace.Roots.Any(root => PlatformPaths.Same(root, folder)))
                break;
        }

        return full;
    }

    private static bool HasMain(string path)
    {
        try
        {
            return MainDeclared.IsMatch(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// What a D program stopped on was, by where in druntime it stopped, null for no D of the runtime's. lldb only says
    /// "Exception 0xc0000096 encountered", the D says what it was once the program goes on to its end.
    /// </summary>
    internal string? DExceptionOf(IReadOnlyList<DebugFrame> frames)
    {
        foreach (DebugFrame frame in frames)
        {
            string? what = frame.Name switch
            {
                var name when name.Contains("_d_arraybounds", StringComparison.Ordinal) => "debugging.d_index",
                var name when name.Contains("_d_assert", StringComparison.Ordinal) => "debugging.d_assert",
                var name when name.Contains("onOutOfMemoryError", StringComparison.Ordinal) => "debugging.d_memory",
                var name when name.Contains("_d_throw", StringComparison.Ordinal) => "debugging.d_throw",
                _ => null
            };
            if (what is not null)
                return context.Strings.Said(what);
        }

        return null;
    }

    private async Task BuildAndLaunchD(string file, string debugger, Dictionary<string, IReadOnlyCollection<int>> breakpointsNow)
    {
        var (program, folder, built, said) = await BuildD(file);
        if (!ReportBuild(said, folder, built, Path.GetFileName(file), file))
            return;

        if (!File.Exists(program))
        {
            debugWork.Enqueue(() =>
            {
                building = false;
                context.Notices.Say(context.Strings.Said("debugging.no_program", ("project", Path.GetFileName(file)), ("program", program)), error: true);
            });
            return;
        }

        debuggedProject = program;
        await LaunchUnder(DebugSession.Start(debugger, []), program, folder, breakpointsNow, LLDB, ProgramOutputFile());
    }

    // The file what a program under lldb-dap writes goes to, and how far into it has been said. lldb-dap on Windows says
    // nothing of it, "sum is 14" went nowhere
    private string? outputFile;
    private long outputRead, outputLooked;

    // When the file's let go of once its session's over, the debugger holds it a moment after the program ends, 0 while
    // the program goes
    private long outputUntil;

    // How long after the end the last of it is waited for
    private const int OUTPUT_DRAIN = 2000;

    // How often the file is looked at, a few times a second, not every frame
    private const int OUTPUT_EVERY = 200;

    // The file kept open while it's read, and what's read of it each time, kept for the next
    private FileStream? outputStream;
    private byte[] outputBytes = new byte[4096];

    // The most read at one look, a program that writes a flood is said a megabyte a look
    private const int OUTPUT_MOST = 1 << 20;

    /// <summary>A file of its own for what the program writes, made empty, read as the program goes.</summary>
    private string ProgramOutputFile()
    {
        string file = Path.Combine(Path.GetTempPath(), "hex-debug-" + Guid.NewGuid().ToString("N")[..12] + ".txt");
        File.WriteAllText(file, string.Empty);

        // Read from on the editor's thread, made on the build's. The last session's goes first if it wasn't let go of yet
        debugWork.Enqueue(() =>
        {
            if (outputFile is not null)
                DeleteProgramOutput();
            (outputFile, outputRead, outputUntil) = (file, 0, 0);
        });
        return file;
    }

    /// <summary>Every tick while something's debugged, what the program wrote since the last look, a line in the output each.</summary>
    private void ReadProgramOutput()
    {
        if (outputFile is null || Environment.TickCount64 - outputLooked < OUTPUT_EVERY)
            return;

        outputLooked = Environment.TickCount64;
        try
        {
            // Opened once for the session and asked how long it is, a stream and a reader made five times a second
            // for nothing new was garbage the whole time something was debugged
            outputStream ??= new FileStream(outputFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = outputStream.Length;
            if (length <= outputRead)
            {
                // Nothing more by the time it's over, it's done with
                if (outputUntil > 0 && Environment.TickCount64 > outputUntil)
                    DeleteProgramOutput();
                return;
            }

            int count = (int)Math.Min(length - outputRead, OUTPUT_MOST);
            if (outputBytes.Length < count)
                outputBytes = new byte[count];
            outputStream.Position = outputRead;
            int read = outputStream.ReadAtLeast(outputBytes.AsSpan(0, count), count, throwOnEndOfStream: false);

            // Only whole lines while it goes, the rest waits for its end. Once it's over the last line is said too
            int end = outputUntil > 0 ? read : outputBytes.AsSpan(0, read).LastIndexOf((byte)'\n') + 1;
            if (end > 0)
            {
                outputRead += end;
                foreach (string line in System.Text.Encoding.UTF8.GetString(outputBytes, 0, end).TrimEnd('\n').Split('\n'))
                    context.Notices.Note(line.TrimEnd('\r'));
            }

            if (outputUntil > 0 && outputRead >= length)
                DeleteProgramOutput();
        }
        catch (IOException)
        {
            // Being written this moment, or still held by the debugger, it's read next time
            if (outputUntil > 0 && Environment.TickCount64 > outputUntil)
                DeleteProgramOutput();
        }
    }

    private void DeleteProgramOutput()
    {
        outputStream?.Dispose();
        outputStream = null;
        try { File.Delete(outputFile!); }
        catch (IOException) { }
        outputFile = null;
    }

    /// <summary>Hex is closing, the session ended and its file gone now, there's no next tick to wait for the last of it.</summary>
    internal void Quit()
    {
        EndDebugging();
        if (outputFile is not null)
            DeleteProgramOutput();
    }

    /// <summary>
    /// The files of programs debugged before that were never let go of (Hex closed while the debugger held one) cleared
    /// away as Hex starts, any over a minute old, a younger one could be another Hex's that's debugging now.
    /// </summary>
    private static void SweepProgramOutputs()
    {
        try
        {
            foreach (string left in Directory.EnumerateFiles(Path.GetTempPath(), "hex-debug-*.txt"))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(left) > TimeSpan.FromMinutes(1))
                {
                    try { File.Delete(left); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
        catch (IOException) { }
    }

    /// <summary>
    /// The program's session is over, the last of what it wrote read as soon as the debugger lets go of the file (every
    /// tick, for a couple of seconds at most), then the file goes. Read at once it was still held, and nothing it wrote
    /// was ever said.
    /// </summary>
    private void ForgetProgramOutput()
    {
        if (outputFile is null || outputUntil > 0)
            return;

        outputUntil = Environment.TickCount64 + OUTPUT_DRAIN;
        outputLooked = 0;
    }

    /// <summary>
    /// A D file built with what it needs to be debugged (-g), a dub project's with dub build (a debug build already) and
    /// a lone file next to itself with the compiler, the way its run task builds it. The program, its folder, the
    /// build's exit code and what it said.
    /// </summary>
    private static async Task<(string Program, string Folder, int Exit, string Said)> BuildD(string file)
    {
        string exe = OperatingSystem.IsWindows() ? ".exe" : string.Empty;

        // MSVC's link puts a jump in front of every function when it links a bit at a time, lldb steps into the jump, finds
        // no code of D's there and steps on out of main. F11 was a step out
        string[] linked = OperatingSystem.IsWindows() ? ["-L/INCREMENTAL:NO"] : [];
        if (HexLanguages.DubProjectOf(Path.GetFullPath(file)) is { } project)
        {
            // dub takes DFLAGS for its build's flags instead of its own (a debug build's are -g -debug)
            (string, string)? flags = OperatingSystem.IsWindows() ? ("DFLAGS", "-g -debug -L/INCREMENTAL:NO") : null;
            var (built, said) = await Run("dub", project, flags, "build");
            if (built != 0)
                return (string.Empty, project, built, said);

            // Where dub put it and what it called it, its own word on it
            var (_, described) = await Run("dub", project, flags, "describe", "--data-list", "--data=target-path", "--data=target-name");
            string[] lines = described.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string program = lines.Length >= 2 ? Path.Combine(lines[0], lines[1] + exe) : Path.Combine(project, Path.GetFileName(project) + exe);
            return (program, project, 0, said);
        }

        string folder = Path.GetDirectoryName(Path.GetFullPath(file))!;
        string name = Path.GetFileNameWithoutExtension(file) + exe;
        string compiler = LanguageServers.IsAvailable("dmd") ? "dmd" : "ldmd2";
        // The object files go in a folder of Hex's own, the program and what it's debugged with (the .pdb) next to the code
        string objects = Path.Combine(Path.GetTempPath(), "hex-d-objects");
        var (exit, output) = await Run(compiler, folder, null, ["-i", "-g", Path.GetFileName(file), "-of=" + name, "-od=" + objects, .. linked]);
        return (Path.Combine(folder, name), folder, exit, output);
    }

    // A program with some arguments in a folder, with a variable of its environment set or not, its exit code and what it wrote
    private static async Task<(int Exit, string Said)> Run(string command, string folder, (string Name, string Value)? variable, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(command) { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            if (variable is { } set)
                start.Environment[set.Name] = set.Value;
            foreach (string argument in arguments)
                start.ArgumentList.Add(argument);
            using Process process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, await output + await errors);
        }
        catch (Exception e)
        {
            return (-1, e.Message);
        }
    }
}
