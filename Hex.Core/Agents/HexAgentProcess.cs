using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Hex.Core;

/// <summary>
/// An agent's program run for a turn of a chat, Claude Code's claude or Codex's codex or whatever an extension says. It's
/// found on PATH the way a shell would find it (a .cmd or .bat through cmd, which is how npm installs things on Windows),
/// started in a folder with the prompt written to its stdin, and every line it prints queued for the editor's thread to
/// take when it ticks. Stopping it kills it and everything it started.
/// </summary>
internal sealed class HexAgentProcess : IDisposable
{
    // The program as .NET started it, on Windows (held in a job there) or where it couldn't be started in a group
    private readonly Process? process;

    // The program started in a session and group of its own, on Linux and macOS
    private readonly HexAgentGroup? group;

    private readonly StreamWriter input;

    /// <summary>What it printed, a line at a time, whether it was on stderr with each.</summary>
    public ConcurrentQueue<(string Line, bool Error)> Lines { get; } = new();

    /// <summary>Its exit code once it's gone, and every line it printed before that queued.</summary>
    public int? ExitCode { get; private set; }

    public bool Exited => ExitCode is not null;

    private int readersLeft = 2;

    /// <summary>
    /// Called from the reading threads when a line came or the program ended, so a window that sleeps while nothing
    /// happens (Dusk on screen and quiet) wakes to take it in. Set once by whoever ticks the agents.
    /// </summary>
    public static Action? Woken { get; set; }

    private HexAgentProcess(Process process)
    {
        this.process = process;
        input = process.StandardInput;
    }

    private HexAgentProcess(HexAgentGroup group)
    {
        this.group = group;
        input = new StreamWriter(group.Input!, new UTF8Encoding(false));
    }

    /// <summary>
    /// Where a program is, a whole path as it is, a name looked for on PATH with each of PATHEXT's endings (.exe, .cmd,
    /// .bat on Windows, nothing elsewhere). Null when it isn't anywhere.
    /// </summary>
    public static string? Find(string program, string? path = null, string? endings = null, HexAgentProgramPaths? fallback = null)
    {
        if (program.Length == 0)
            return null;
        if (Path.IsPathRooted(program) || program.Contains('/') || program.Contains('\\'))
            return File.Exists(program) ? Path.GetFullPath(program) : null;

        bool windows = fallback?.Windows ?? OperatingSystem.IsWindows();
        string[] tried = windows
            ? [.. (endings ?? Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(ending => ending.ToLowerInvariant())]
            : [string.Empty];
        if (Path.HasExtension(program))
            tried = [string.Empty, .. tried];

        char separator = windows ? ';' : ':';
        var folders = (path ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(separator, StringSplitOptions.RemoveEmptyEntries);
        if (FindOn(folders, program, tried) is { } found)
            return found;

        fallback ??= path is null ? HexAgentProgramPaths.Current() : null;
        return fallback is null ? null : FindOn(fallback.Folders(), program, tried);
    }

    // The first of a program's names in a set of folders.
    private static string? FindOn(IEnumerable<string> folders, string program, string[] tried)
    {
        foreach (string folder in folders)
        {
            foreach (string ending in tried)
            {
                try
                {
                    string candidate = Path.Combine(folder.Trim().Trim('"'), program + ending);
                    if (File.Exists(candidate))
                        return Path.GetFullPath(candidate);
                }
                catch (ArgumentException)
                {
                    // A folder on PATH with something in its name a path can't have
                }
            }
        }

        return null;
    }

    /// <summary>
    /// How a program is started with its arguments. A .cmd or .bat goes through cmd with the whole line quoted the way
    /// cmd /s takes it, anything else is started itself with its arguments passed one by one.
    /// </summary>
    public static ProcessStartInfo StartInfo(string found, IReadOnlyList<string> arguments, string folder)
    {
        var info = new ProcessStartInfo
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false)
        };

        if (OperatingSystem.IsWindows() && Path.GetExtension(found).ToLowerInvariant() is ".cmd" or ".bat")
        {
            info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            info.Arguments = $"/d /s /c \"{CmdQuote(found)} {string.Join(' ', arguments.Select(CmdQuote))}\"";
            // npm's shim may also need a Node executable installed after Hex started.
            info.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") + ";" + HexAgentProgramPaths.WindowsPath();
        }
        else
        {
            info.FileName = found;
            foreach (string argument in arguments)
                info.ArgumentList.Add(argument);
        }

        return info;
    }

    /// <summary>An argument quoted for cmd, in quotes when it has a space or something cmd would take for its own.</summary>
    internal static string CmdQuote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c is '"' or '&' or '|' or '<' or '>' or '^' or '(' or ')' or ',' or ';' or '='))
            return argument;
        return "\"" + argument.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>
    /// The program started, the prompt written to its stdin and closed, or kept open with <paramref name="keepInput"/> for
    /// more to be said to it with <see cref="Send"/> until <see cref="CloseInput"/>. Throws when it couldn't be started.
    /// </summary>
    public static HexAgentProcess Start(string found, IReadOnlyList<string> arguments, string folder, string? stdin, IReadOnlyDictionary<string, string>? environment = null, bool keepInput = false)
    {
        ProcessStartInfo info = StartInfo(found, arguments, folder);

        // Builds an agent runs leave no MSBuild nodes waiting behind them for a build that never comes. Several agents
        // building at once left three dozen of them, four and a half gigabytes, after every one had finished. Unless the
        // person's own environment says otherwise
        if (System.Environment.GetEnvironmentVariable("MSBUILDDISABLENODEREUSE") is null)
            info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
                info.Environment[name] = value;
        }

        HexAgentProcess running;
        StreamReader output, error;
        if (!OperatingSystem.IsWindows() && StartInGroup(info) is { } group)
        {
            running = new HexAgentProcess(group);
            output = new StreamReader(group.Output!, new UTF8Encoding(false));
            error = new StreamReader(group.Error!, new UTF8Encoding(false));
        }
        else
        {
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.Start();
            running = new HexAgentProcess(process);
            // Held in a job of its own at once, before it's had the time to start anything, so whatever it starts is held too
            running.job = HexAgentJob.Hold(process);
            output = process.StandardOutput;
            error = process.StandardError;
        }

        // Said before the readers start, which close it as the program goes: set after them, a program that went at once
        // was left taking input
        running.inputOpen = keepInput;
        new Thread(() => running.Read(output, false)) { IsBackground = true, Name = "agent stdout" }.Start();
        new Thread(() => running.Read(error, true)) { IsBackground = true, Name = "agent stderr" }.Start();

        try
        {
            if (stdin is not null)
                running.input.Write(stdin);
            if (keepInput)
                running.input.Flush();
            else
                running.input.Close();
        }
        catch (IOException)
        {
            // It went before it read what it was asked, which its own output says why
            lock (running.inputGate)
                running.inputOpen = false;
        }

        return running;
    }

    // Started in a session and group of its own (HexAgentGroup), null where the C library hasn't what that takes and
    // .NET starts it instead
    private static HexAgentGroup? StartInGroup(ProcessStartInfo info)
    {
        try
        {
            return HexAgentGroup.Start(info.FileName, [.. info.ArgumentList], info.WorkingDirectory, info.Environment);
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    // Whether its stdin is still open for more to be said to it
    private bool inputOpen;
    private readonly Lock inputGate = new();

    /// <summary>Whether more can be said to it, its stdin kept open and not closed since.</summary>
    public bool TakesInput
    {
        get
        {
            lock (inputGate)
                return inputOpen;
        }
    }

    /// <summary>A line written to its stdin while it runs, false when that's closed or it's gone.</summary>
    public bool Send(string line)
    {
        lock (inputGate)
        {
            if (!inputOpen)
                return false;

            try
            {
                input.Write(line.TrimEnd('\r', '\n') + "\n");
                input.Flush();
                return true;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                inputOpen = false;
                return false;
            }
        }
    }

    /// <summary>Its stdin closed, which tells an agent that reads more from it that nothing else is coming.</summary>
    public void CloseInput()
    {
        lock (inputGate)
        {
            if (!inputOpen)
                return;

            inputOpen = false;
            try
            {
                input.Close();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }
    }

    private void Read(StreamReader from, bool error)
    {
        try
        {
            while (from.ReadLine() is { } line)
            {
                Lines.Enqueue((line, error));
                Woken?.Invoke();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }

        // The exit's only said once both of what it prints are read to their ends, so no line comes after it
        if (Interlocked.Decrement(ref readersLeft) == 0)
        {
            try
            {
                if (group is not null)
                {
                    ExitCode = group.WaitForExit();
                }
                else
                {
                    process!.WaitForExit();
                    ExitCode = process.ExitCode;
                }
            }
            catch (Exception)
            {
                ExitCode = -1;
            }

            Woken?.Invoke();
        }
    }

    // The job its processes are held in, Windows only (HexAgentJob)
    private HexAgentJob? job;

    /// <summary>
    /// Kills it and every process it started: its group on Linux and macOS, its job on Windows, either of which has them
    /// all whatever ended in between, and its tree as well where it has no job, for what the job didn't take (a process
    /// started before it was held, or no jobs at all).
    /// </summary>
    public void Stop()
    {
        if (group is not null)
        {
            group.Kill();
            return;
        }

        try
        {
            if (!process!.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        job?.Dispose();
        job = null;
    }

    public void Dispose()
    {
        Stop();
        process?.Dispose();
        if (group is not null)
        {
            // Hex's end of its stdin let go of, the ends of what it prints by the readers when they've read them through
            lock (inputGate)
            {
                inputOpen = false;
                try
                {
                    input.Dispose();
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
