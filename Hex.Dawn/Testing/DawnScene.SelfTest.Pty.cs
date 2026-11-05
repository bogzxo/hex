using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test, the shell itself under the terminal page, what the engine's pseudo terminal does on whatever this runs
// on (a tablet's /system/bin/sh as the app's own user, a desktop's usual shell). It starts in the folder it was asked to,
// a line typed reaches it and what it writes comes back, it hears a new size, and it ends when it's told to with the
// terminal knowing. Every check is said with what the shell actually wrote, so a failure says why.
internal sealed partial class DawnScene
{
    private void TestPty(HexPointerScript test)
    {
        // The lines below are a POSIX shell's, PowerShell and cmd have no stty or id
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("[Hex self-test] pty: a POSIX shell's suite, nothing to check on Windows");
            return;
        }

        // A folder with a space in it, the shell has to have been started in it rather than have cd'd there
        string folder = Path.Combine(testFolder, "pty start");
        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            explorer.OpenFolder(folder);
            terminalPanel.AnimationTime = 0.0f;
            foreach (TerminalTab tab in terminalPanel.Tabs.ToArray())
                terminalPanel.CloseTab(tab);
        });
        test.Wait();
        test.Run(ToggleTerminalPage);
        test.Wait();
        test.WaitFor(() => terminalPanel.FocusedView?.Screen.Text().Trim().Length > 0, 15.0f);
        test.Check("pty: a shell starts and writes its prompt", () => terminalPanel.FocusedView?.Process is { IsRunning: true } && terminalPanel.FocusedView.Screen.Text().Trim().Length > 0);
        test.Run(() => Console.WriteLine($"[Hex self-test] pty: shell '{terminalPanel.FocusedView?.Process?.Command}', folder '{folder}'"));

        // pwd with a marker either side so the prompt and the echo of the line itself don't count. Both sides physical
        // (pwd -P), on Android /data/user/0 is a link to /data/data
        test.Run(() => terminalPanel.FocusedView?.Send($"echo \"<pwd>$(pwd -P)</pwd>\"; echo \"<want>$(cd '{folder}' && pwd -P)</want>\"\r"));
        test.WaitFor(() => ShellSaid("<pwd>", "</pwd>") is not null && ShellSaid("<want>", "</want>") is not null, 8.0f);
        test.Check("pty: the shell started in the folder it was asked to", () => ShellSaid("<pwd>", "</pwd>") is { Length: > 0 } at && at == ShellSaid("<want>", "</want>"));
        test.Run(() => Console.WriteLine($"[Hex self-test] pty: pwd said '{ShellSaid("<pwd>", "</pwd>")}', the folder is '{ShellSaid("<want>", "</want>")}'"));

        test.Run(() => terminalPanel.FocusedView?.Send("echo \"<id>$(id -u)</id>\"\r"));
        test.WaitFor(() => ShellSaid("<id>", "</id>") is not null, 8.0f);
        test.Check("pty: the shell runs as a user, a line in and what it wrote back", () => ShellSaid("<id>", "</id>") is { Length: > 0 });
        test.Run(() => Console.WriteLine($"[Hex self-test] pty: id -u said '{ShellSaid("<id>", "</id>")}'"));

        // The size the terminal laid itself out to, then a new one told to the pty and asked of the shell again
        int columns = 0, rows = 0;
        test.Run(() =>
        {
            columns = terminalPanel.FocusedView!.Columns;
            rows = terminalPanel.FocusedView.Rows;
            terminalPanel.FocusedView.Send("echo \"<size>$(stty size)</size>\"\r");
        });
        test.WaitFor(() => ShellSaid("<size>", "</size>") is not null, 8.0f);
        test.Check("pty: the shell has the terminal's size", () => ShellSaid("<size>", "</size>") == $"{rows} {columns}");
        test.Run(() => Console.WriteLine($"[Hex self-test] pty: stty size said '{ShellSaid("<size>", "</size>")}', the view is {rows} {columns}"));

        test.Run(() =>
        {
            terminalPanel.FocusedView!.Process!.Resize(columns - 7, rows - 3);
            terminalPanel.FocusedView.Send("echo \"<resized>$(stty size)</resized>\"\r");
        });
        test.WaitFor(() => ShellSaid("<resized>", "</resized>") is not null, 8.0f);
        test.Check("pty: a new size reaches the shell", () => ShellSaid("<resized>", "</resized>") == $"{rows - 3} {columns - 7}");
        test.Run(() => Console.WriteLine($"[Hex self-test] pty: after the resize stty size said '{ShellSaid("<resized>", "</resized>")}'"));

        Horizon.Core.Processes.PseudoTerminal? shell = null;
        test.Run(() =>
        {
            shell = terminalPanel.FocusedView!.Process;
            terminalPanel.FocusedView.Send("exit 7\r");
        });
        test.WaitFor(() => shell is { IsRunning: false }, 8.0f);
        test.Check("pty: exit ends the shell with its code", () => shell is { IsRunning: false, ExitCode: 7 });
        test.Run(() => Console.WriteLine($"[Hex self-test] pty: running {shell?.IsRunning}, exit code {shell?.ExitCode}"));
    }

    // What the shell wrote between two markers, the last time, null when it hasn't
    private string? ShellSaid(string open, string close)
    {
        if (terminalPanel.FocusedView is not { } view)
            return null;

        string all = view.Screen.AllText().Replace("\n", string.Empty).Replace("\r", string.Empty);
        int end = all.LastIndexOf(close, StringComparison.Ordinal);
        if (end < 0)
            return null;
        int start = all.LastIndexOf(open, end, StringComparison.Ordinal);

        // The line as typed has the markers too, with $( after the opening one, which is the echo and not the answer
        string said = start < 0 ? string.Empty : all[(start + open.Length)..end];
        return start < 0 || said.Contains("$(") ? null : said.Trim();
    }
}
