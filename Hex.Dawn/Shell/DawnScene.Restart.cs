using System.Diagnostics;

namespace Hex.Dawn;

// Restart into the New Build, after a task built Hex over itself (HexTasks.SelfBuild). The new exe is started on the
// same workspace as this one closes, the way closing the window closes it, files with changes asked about first.
internal sealed partial class DawnScene
{
    // The exe to start and what with as the window closes, null for a close that's only a close
    private (string Exe, IReadOnlyList<string> Arguments, Action? Abandoned, Action? Started)? restartInto;

    private void RestartInto(string exe, IReadOnlyList<string> arguments) => RestartInto(exe, arguments, null, null);

    private void RestartInto(string exe, IReadOnlyList<string> arguments, Action? abandoned, Action? started)
    {
        AbandonRestart();
        restartInto = (exe, arguments, abandoned, started);
        AskBeforeClosing();
    }

    private void AbandonRestart()
    {
        var abandoned = restartInto?.Abandoned;
        restartInto = null;
        abandoned?.Invoke();
    }

    // Called as the window closes for good, the new Hex started if a restart asked for it
    private bool StartRestart()
    {
        if (restartInto is not { } restart)
            return true;

        restartInto = null;
        try
        {
            var start = new ProcessStartInfo(restart.Exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(restart.Exe)! };
            foreach (string argument in restart.Arguments)
                start.ArgumentList.Add(argument);

            // A script and its screenshots drive the Hex they were given to, the new one would play them all over again
            start.Environment.Remove("HORIZON_INPUT_SCRIPT");
            start.Environment.Remove("HORIZON_SCREENSHOT");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The installer or replacement process could not be started.");
            restart.Started?.Invoke();
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            restart.Abandoned?.Invoke();
            Note(Said("tasks.restart_failed", ("why", e.Message)), error: true);
            return false;
        }
    }
}
