using System.Diagnostics;

using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// An agent's run stopped takes everything it started with it, the processes whose parent has already gone included,
/// which killing its tree never found: a self test a shell started and left, still open hours later. And Hex gone,
/// however it went, takes every run with it. On Windows a job holds a run (HexAgentJob), on Linux and macOS a session
/// and group of its own and a watcher of Hex's (HexAgentGroup).
/// </summary>
public sealed class HexAgentJobTests
{
    [Fact]
    public void Stopping_a_run_ends_what_it_started_after_its_parent_has_gone()
    {
        string said = Path.Combine(Path.GetTempPath(), $"hex-agent-job-{Guid.NewGuid():N}.txt");
        var run = StartLeaving(said);

        Process? orphan = null;
        try
        {
            orphan = Left(said);

            // Its parent is gone by now, the run's own process ended
            var waited = Stopwatch.StartNew();
            while (!run.Exited && waited.Elapsed < TimeSpan.FromSeconds(10))
                Thread.Sleep(50);
            Assert.True(run.Exited);
            Assert.False(orphan.HasExited);

            run.Dispose();
            Assert.True(Ends(orphan), "the process the run left behind is still going after the run was stopped");
        }
        finally
        {
            Finish(orphan, said);
            run.Dispose();
        }
    }

    [Fact]
    public void Hex_killed_ends_what_its_runs_left()
    {
        // Hex is this test assembly run as a program of its own (HexAgentHost), which starts a run that leaves a process
        // behind, and is then killed
        string said = Path.Combine(Path.GetTempPath(), $"hex-agent-host-{Guid.NewGuid():N}.txt");
        string assembly = typeof(HexAgentJobTests).Assembly.Location;
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host)
            ? host
            : Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet" ? Environment.ProcessPath! : HexAgentProcess.Find("dotnet") ?? "dotnet";
        var info = new ProcessStartInfo(dotnet) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (string argument in (string[])["exec", assembly, HexAgentHost.Command, said])
            info.ArgumentList.Add(argument);

        using Process hex = Process.Start(info)!;
        Process? orphan = null;
        try
        {
            orphan = Left(said);
            Assert.False(orphan.HasExited);

            hex.Kill();
            Assert.True(hex.WaitForExit(10_000));
            Assert.True(Ends(orphan), "the process an agent's run left behind is still going after Hex was killed");
        }
        finally
        {
            try
            {
                if (!hex.HasExited)
                    hex.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            Finish(orphan, said);
        }
    }

    /// <summary>
    /// A run that starts a process which writes its id to <paramref name="said"/> and waits two minutes, and goes at once,
    /// leaving it with no parent.
    /// </summary>
    internal static HexAgentProcess StartLeaving(string said)
    {
        if (OperatingSystem.IsWindows())
        {
            string powershell = HexAgentProcess.Find("powershell") ?? "powershell.exe";
            string child = $"$PID | Set-Content -Path '{said}'; Start-Sleep 120";
            return HexAgentProcess.Start(powershell,
                ["-NoProfile", "-Command", $"Start-Process -WindowStyle Hidden -FilePath '{powershell}' -ArgumentList '-NoProfile','-Command','{child.Replace("'", "''")}'"],
                Path.GetTempPath(), stdin: null);
        }

        // In the background with nothing of the run's pipes, so the run's own output ends when the sh does
        return HexAgentProcess.Start("/bin/sh", ["-c", "sleep 120 </dev/null >/dev/null 2>&1 & echo $! > \"$1\"", "sh", said], Path.GetTempPath(), stdin: null);
    }

    // The process the run left, once it's said who it is
    private static Process Left(string said)
    {
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (File.Exists(said) && int.TryParse(File.ReadAllText(said).Trim(), out int id))
                return Process.GetProcessById(id);
            Thread.Sleep(100);
        }

        throw new TimeoutException("the run never said what it left behind");
    }

    // Whether it ended within ten seconds
    private static bool Ends(Process orphan)
    {
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            orphan.Refresh();
            if (orphan.HasExited)
                return true;
            Thread.Sleep(100);
        }

        return false;
    }

    private static void Finish(Process? orphan, string said)
    {
        try
        {
            if (orphan is { HasExited: false })
                orphan.Kill();
        }
        catch (InvalidOperationException) { }
        File.Delete(said);
    }
}

/// <summary>
/// The test assembly run as a program (dotnet exec Hex.Tests.dll agent-host file), standing in for Hex: it starts a run
/// that leaves a process behind and waits to be killed.
/// </summary>
internal static class HexAgentHost
{
    public const string Command = "agent-host";

    public static void Main(string[] arguments)
    {
        if (arguments is not [Command, string said])
            return;

        _ = HexAgentJobTests.StartLeaving(said);
        Thread.Sleep(Timeout.Infinite);
    }
}
