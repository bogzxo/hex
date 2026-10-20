using Horizon.UI.Components;

namespace Hex.Dusk;

// The self test of the PATH a new shell gets. Dusk's own is made stale (as good as Dusk having been open since before a
// program was put on the PATH), a new tab of PowerShell is opened, and its PATH has to be the system's as it is now, the
// stale one only on the end of it.
internal sealed partial class DuskScene
{
    private void TestPath(DuskScript test)
    {
        TerminalView View() => panel.FocusedView!;
        const string STALE = @"C:\dusk-stale-path";

        // Windows hands a process its PATH once, the registry has it afresh (ProcessEnvironment), which is what this is
        // about. Linux and macOS have no such thing, a shell's PATH is what its profile makes it
        if (!OperatingSystem.IsWindows())
            return;

        TerminalProfile? shell = panel.Profiles.Find(profile => profile.Id is "pwsh" or "powershell");
        test.Check("path: there is a PowerShell", () => shell is not null);
        if (shell is null)
            return;

        string? was = null;
        string first = (Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)[0];
        test.Run(() =>
        {
            was = Environment.GetEnvironmentVariable("PATH");
            Environment.SetEnvironmentVariable("PATH", STALE);
            panel.NewTab(shell);
        });
        test.WaitFor(() => View().Profile == shell && View().Screen.MarksSeen && View().Screen.AtPrompt, 15.0f);
        test.Run(() => Environment.SetEnvironmentVariable("PATH", was));
        test.Run(() => View().Send("'path-is ' + $env:Path.Split(';')[0] + ' ' + $env:Path.Split(';')[-1]\r"));
        test.WaitFor(() => View().Screen.Text().Contains("path-is " + first, StringComparison.OrdinalIgnoreCase), 8.0f);
        test.Check("path: a new shell has the system's PATH as it is now, not the one Dusk was started with", () =>
            View().Screen.Text().Contains($"path-is {first} {STALE}", StringComparison.OrdinalIgnoreCase));
    }
}
