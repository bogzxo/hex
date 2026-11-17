#if DEBUG
using System.Diagnostics;

namespace Hex;

/// <summary>
/// The guard over a Hex restarting into a build of itself it just made. It runs from a copy of the build that was
/// running (known to work), starts the new one with everything it prints going to a log, and waits for it to say it
/// came up whole. A new build that dies or hangs before then is taken away, the good one put back in its place and
/// started again with the chats it was handed and the log, so the agent that made the build reads why it crashed and
/// fixes it. Only in a debug build, see DawnScene.McpDev.
/// </summary>
internal static class DevGuard
{
    /// <summary>The argument Hex is started with to be the guard, no window.</summary>
    public const string ARGUMENT = "--dev-guard";

    // How long a new build has to come up whole before it's taken as hung
    private static readonly TimeSpan HEALTHY_WITHIN = TimeSpan.FromSeconds(120);

    public static int Run(string[] args)
    {
        string? Value(string name) => Array.IndexOf(args, name) is >= 0 and var at && at + 1 < args.Length ? args[at + 1] : null;
        string bin = Value("--bin") ?? throw new ArgumentException("--bin");
        string good = Value("--good") ?? throw new ArgumentException("--good");
        string resume = Value("--resume") ?? throw new ArgumentException("--resume");
        string? folder = Value("--folder");
        bool mcp = args.Contains("--mcp");

        string healthy = resume + ".healthy";
        string log = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(bin))!, $"hex-dev-start-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        File.Delete(healthy);

        // Shared for reading, the build put back reads it to tell the agent why the new one crashed while this goes on writing
        using var written = new StreamWriter(new FileStream(log, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        written.WriteLine($"[guard] starting the new build in {bin}");
        Process started = Start(bin, folder, mcp, resume, healthy, crash: null, written);

        // Up whole, or gone, or hung
        var watch = Stopwatch.StartNew();
        while (!File.Exists(healthy) && !started.HasExited && watch.Elapsed < HEALTHY_WITHIN)
            Thread.Sleep(250);

        if (File.Exists(healthy))
        {
            // It works. What it prints goes on into the log for as long as it runs, the guard with it
            written.WriteLine($"[guard] the new build came up in {watch.Elapsed.TotalSeconds:0}s");
            WaitGone(started);
            written.WriteLine($"[guard] it closed with {started.ExitCode}");
            return 0;
        }

        if (!started.HasExited)
        {
            written.WriteLine($"[guard] the new build didn't come up in {HEALTHY_WITHIN.TotalSeconds:0}s, taken as hung");
            started.Kill(entireProcessTree: true);
            WaitGone(started);
        }
        else
            written.WriteLine($"[guard] the new build closed with {started.ExitCode} before it came up");

        // The good build back where it ran from, and started with the chats and why
        int putBack = Hex.Core.HexDevBuilds.Mirror(good, bin);
        written.WriteLine($"[guard] put {putBack} files of the build before it back and started that again");
        // What the good one prints goes on into the log too, so a build put back that has trouble of its own says why
        written.WriteLine("[guard] what the build put back printed");
        Process restored = Start(bin, folder, mcp, resume, healthy: null, crash: log, written);
        WaitGone(restored);
        written.WriteLine($"[guard] the build put back closed with {restored.ExitCode}");
        return 1;
    }

    /// <summary>
    /// Waits for Hex itself to be gone, not for the ends of what it printed. The agents it started inherit its output,
    /// so with them still running the pipe never ended and WaitForExit waited on them, the build that crashed never put
    /// back while an agent it had resumed was alive.
    /// </summary>
    private static void WaitGone(Process process)
    {
        while (!process.WaitForExit(500)) { }
    }

    // Hex started from a folder, its output into the log when there's one
    private static Process Start(string bin, string? folder, bool mcp, string resume, string? healthy, string? crash, StreamWriter? written)
    {
        var start = new ProcessStartInfo(Path.Combine(bin, OperatingSystem.IsWindows() ? "Hex.exe" : "Hex"))
        {
            UseShellExecute = false,
            WorkingDirectory = bin,
            RedirectStandardOutput = written is not null,
            RedirectStandardError = written is not null,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--window");
        if (mcp)
            start.ArgumentList.Add("--mcp");
        if (folder is not null)
        {
            start.ArgumentList.Add("--folder");
            start.ArgumentList.Add(folder);
        }

        // Only what this start means, nothing of a start before it the guard itself inherited
        start.Environment.Remove("HEX_DEV_CRASH");
        start.Environment.Remove("HEX_DEV_HEALTHY");
        start.Environment["HEX_DEV_RESUME"] = resume;
        if (healthy is not null)
            start.Environment["HEX_DEV_HEALTHY"] = healthy;
        if (crash is not null)
            start.Environment["HEX_DEV_CRASH"] = crash;

        Process process = Process.Start(start)!;
        if (written is not null)
        {
            process.OutputDataReceived += (_, line) => { if (line.Data is { } said) lock (written) written.WriteLine(said); };
            process.ErrorDataReceived += (_, line) => { if (line.Data is { } said) lock (written) written.WriteLine(said); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        return process;
    }
}
#endif
