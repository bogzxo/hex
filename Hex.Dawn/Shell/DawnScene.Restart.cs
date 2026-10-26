using System.Diagnostics;

namespace Hex.Dawn;

// Restart into the New Build, after a task built Hex over itself (HexTasks.SelfBuild). The new exe is started on the
// same workspace as this one closes, the way closing the window closes it, files with changes asked about first.
internal sealed partial class DawnScene
{
    // The exe to start and what with as the window closes, null for a close that's only a close
    private (string Exe, IReadOnlyList<string> Arguments)? restartInto;

    private void RestartInto(string exe, IReadOnlyList<string> arguments)
    {
        restartInto = (exe, arguments);
        AskBeforeClosing();
    }

    // Called as the window closes for good, the new Hex started if a restart asked for it
    private void StartRestart()
    {
        if (restartInto is not { } restart)
            return;

        restartInto = null;
        try
        {
            var start = new ProcessStartInfo(restart.Exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(restart.Exe)! };
            foreach (string argument in restart.Arguments)
                start.ArgumentList.Add(argument);

            // A script and its screenshots drive the Hex they were given to, the new one would play them all over again
            start.Environment.Remove("HORIZON_INPUT_SCRIPT");
            start.Environment.Remove("HORIZON_SCREENSHOT");
            Process.Start(start);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Note(Said("tasks.restart_failed", ("why", e.Message)), error: true);
        }
    }
}
