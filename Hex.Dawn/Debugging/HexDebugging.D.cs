using System.Diagnostics;

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
        building = true;
        debuggedProject = file;
        restart = () => StartDebuggingD(file);
        context.Panels.ShowOutput();
        context.Notices.Say(context.Strings.Said("debugging.building", ("project", Path.GetFileName(file))));

        var breakpointsNow = breakpoints.Where(pair => pair.Value.Count > 0).ToDictionary(pair => pair.Key, pair => (IReadOnlyCollection<int>)pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        _ = Task.Run(() => BuildAndLaunchD(file, debugger, breakpointsNow));
    }

    private async Task BuildAndLaunchD(string file, string debugger, Dictionary<string, IReadOnlyCollection<int>> breakpointsNow)
    {
        var (program, folder, built, said) = await BuildD(file);
        if (!ReportBuild(said, folder, built, Path.GetFileName(file)))
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

    /// <summary>A file of its own for what the program writes, made empty, read as the program goes.</summary>
    private string ProgramOutputFile()
    {
        string file = Path.Combine(Path.GetTempPath(), "hex-debug-" + Guid.NewGuid().ToString("N")[..12] + ".txt");
        File.WriteAllText(file, string.Empty);
        (outputFile, outputRead, outputUntil) = (file, 0, 0);
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
            using var file = new FileStream(outputFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length <= outputRead)
            {
                // Nothing more by the time it's over, it's done with
                if (outputUntil > 0 && Environment.TickCount64 > outputUntil)
                    DeleteProgramOutput();
                return;
            }

            file.Position = outputRead;
            using var reader = new StreamReader(file);
            string written = reader.ReadToEnd();

            // Only whole lines while it goes, the rest waits for its end. Once it's over the last line is said too
            int end = outputUntil > 0 ? written.Length - 1 : written.LastIndexOf('\n');
            if (end >= 0)
            {
                outputRead += System.Text.Encoding.UTF8.GetByteCount(written.AsSpan(0, end + 1));
                foreach (string line in written[..(end + 1)].TrimEnd('\n').Split('\n'))
                    context.Notices.Note(line.TrimEnd('\r'));
            }

            if (outputUntil > 0)
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
        try { File.Delete(outputFile!); }
        catch (IOException) { }
        outputFile = null;
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
        if (HexLanguages.DubProjectOf(Path.GetFullPath(file)) is { } project)
        {
            var (built, said) = await Run("dub", project, "build");
            if (built != 0)
                return (string.Empty, project, built, said);

            // Where dub put it and what it called it, its own word on it
            var (_, described) = await Run("dub", project, "describe", "--data-list", "--data=target-path", "--data=target-name");
            string[] lines = described.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string program = lines.Length >= 2 ? Path.Combine(lines[0], lines[1] + exe) : Path.Combine(project, Path.GetFileName(project) + exe);
            return (program, project, 0, said);
        }

        string folder = Path.GetDirectoryName(Path.GetFullPath(file))!;
        string name = Path.GetFileNameWithoutExtension(file) + exe;
        string compiler = LanguageServers.IsAvailable("dmd") ? "dmd" : "ldmd2";
        var (exit, output) = await Run(compiler, folder, "-i", "-g", Path.GetFileName(file), "-of=" + name);
        return (Path.Combine(folder, name), folder, exit, output);
    }

    // A program with some arguments in a folder, its exit code and what it wrote
    private static async Task<(int Exit, string Said)> Run(string command, string folder, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(command) { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
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
