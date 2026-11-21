using System.Diagnostics;

using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// An agent's run stopped takes everything it started with it, the processes whose parent has already gone included,
/// which killing its tree never found: a self test a shell started and left, still open hours later.
/// </summary>
public sealed class HexAgentJobTests
{
    [Fact]
    public void Stopping_a_run_ends_what_it_started_after_its_parent_has_gone()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string said = Path.Combine(Path.GetTempPath(), $"hex-agent-job-{Guid.NewGuid():N}.txt");
        // A PowerShell starts another that writes down its id and waits, and goes at once, leaving it with no parent
        string powershell = HexAgentProcess.Find("powershell") ?? "powershell.exe";
        string child = $"$PID | Set-Content -Path '{said}'; Start-Sleep 120";
        var run = HexAgentProcess.Start(powershell,
            ["-NoProfile", "-Command", $"Start-Process -WindowStyle Hidden -FilePath '{powershell}' -ArgumentList '-NoProfile','-Command','{child.Replace("'", "''")}'"],
            Path.GetTempPath(), stdin: null);

        Process? orphan = null;
        try
        {
            var waited = Stopwatch.StartNew();
            while (waited.Elapsed < TimeSpan.FromSeconds(30) && orphan is null)
            {
                if (File.Exists(said) && int.TryParse(File.ReadAllText(said).Trim(), out int id))
                    orphan = Process.GetProcessById(id);
                else
                    Thread.Sleep(100);
            }
            Assert.NotNull(orphan);

            // Its parent is gone by now, the run's own process ended
            waited.Restart();
            while (!run.Exited && waited.Elapsed < TimeSpan.FromSeconds(10))
                Thread.Sleep(50);
            Assert.True(run.Exited);
            Assert.False(orphan.HasExited);

            run.Dispose();
            Assert.True(orphan.WaitForExit(5000), "the process the run left behind is still going after the run was stopped");
        }
        finally
        {
            try
            {
                if (orphan is { HasExited: false })
                    orphan.Kill();
            }
            catch (InvalidOperationException) { }
            File.Delete(said);
            run.Dispose();
        }
    }
}
