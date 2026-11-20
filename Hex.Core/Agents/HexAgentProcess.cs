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
    private readonly Process process;

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

    private HexAgentProcess(Process process) => this.process = process;

    /// <summary>
    /// Where a program is, a whole path as it is, a name looked for on PATH with each of PATHEXT's endings (.exe, .cmd,
    /// .bat on Windows, nothing elsewhere). Null when it isn't anywhere.
    /// </summary>
    public static string? Find(string program, string? path = null, string? endings = null)
    {
        if (program.Length == 0)
            return null;
        if (Path.IsPathRooted(program) || program.Contains('/') || program.Contains('\\'))
            return File.Exists(program) ? Path.GetFullPath(program) : null;

        string[] tried = OperatingSystem.IsWindows()
            ? [.. (endings ?? Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(ending => ending.ToLowerInvariant())]
            : [string.Empty];
        if (Path.HasExtension(program))
            tried = [string.Empty, .. tried];

        if (FindOn(path ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty, program, tried) is { } found)
            return found;

        // Installed since Hex started (winget, npm -g), it's on the system's PATH as it is now, which Hex's own copy of
        // it from its start hasn't got
        if (path is null && OperatingSystem.IsWindows())
        {
            string now = Environment.ExpandEnvironmentVariables(
                $"{Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)};{Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)}");
            return FindOn(now, program, tried);
        }

        return null;
    }

    // The first of a program's names in the folders of a PATH
    private static string? FindOn(string path, string program, string[] tried)
    {
        foreach (string folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string ending in tried)
            {
                try
                {
                    string candidate = Path.Combine(folder.Trim('"'), program + ending);
                    if (File.Exists(candidate))
                        return candidate;
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

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var running = new HexAgentProcess(process);
        process.Start();

        // Said before the readers start, which close it as the program goes: set after them, a program that went at once
        // was left taking input
        running.inputOpen = keepInput;
        new Thread(() => running.Read(process.StandardOutput, false)) { IsBackground = true, Name = "agent stdout" }.Start();
        new Thread(() => running.Read(process.StandardError, true)) { IsBackground = true, Name = "agent stderr" }.Start();

        try
        {
            if (stdin is not null)
                process.StandardInput.Write(stdin);
            if (keepInput)
                process.StandardInput.Flush();
            else
                process.StandardInput.Close();
        }
        catch (IOException)
        {
            // It went before it read what it was asked, which its own output says why
            lock (running.inputGate)
                running.inputOpen = false;
        }

        return running;
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
                process.StandardInput.Write(line.TrimEnd('\r', '\n') + "\n");
                process.StandardInput.Flush();
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
                process.StandardInput.Close();
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
                process.WaitForExit();
                ExitCode = process.ExitCode;
            }
            catch (Exception)
            {
                ExitCode = -1;
            }

            Woken?.Invoke();
        }
    }

    /// <summary>Kills it and every process it started.</summary>
    public void Stop()
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    public void Dispose()
    {
        Stop();
        process.Dispose();
    }
}
