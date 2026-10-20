using Horizon.UI.Components;

namespace Hex.Dusk;

// The self test of the line typed at a prompt being coloured, in a real PowerShell (which says what it has once it is
// first idle) and a real cmd (which says its marks through its prompt), or the machine's shell (bash, zsh) on Linux and
// macOS, where the PowerShell and cmd parts are left out. Nothing is run but what cmd is asked to.
internal sealed partial class DuskScene
{
    private void TestLine(DuskScript test)
    {
        TerminalView View() => panel.FocusedView!;

        test.WaitFor(() => View().Screen.AtPrompt, 10.0f);
        test.Check("the shell's line is coloured", () => View().LineColourer is TerminalShellLine);

        // PowerShell has to have said what it has before a word can be red, which it does when it is first idle
        test.Run(() => View().Send("gti-nope-xyz"));
        test.WaitFor(() => View().LineText == "gti-nope-xyz" && View().LineKindsText().StartsWith('m'), 15.0f);
        test.Run(() => Shot("line-missing"));
        test.Check("a command the shell hasn't got is red", () => View().LineKindsText() == new string('m', "gti-nope-xyz".Length));

        Clear(test);
        test.Run(() => View().Send("dotnet --version"));
        test.WaitFor(() => View().LineText == "dotnet --version", 5.0f);
        test.Check("one off the PATH is green and its flag a flag", () => View().LineKindsText() == "cccccc.fffffffff");

        Clear(test);
        if (!OperatingSystem.IsWindows())
            return;

        test.Run(() => View().Send($"Get-ChildItem '{Path.GetTempPath().TrimEnd('\\')}' -Force"));
        test.WaitFor(() => View().LineText.EndsWith(" -Force"), 5.0f);
        test.Check("a cmdlet is green and a string a string", () => View().LineKindsText() is { } kinds && kinds.StartsWith("ccccccccccccc.s") && kinds.EndsWith(".ffffff"));
        test.Run(() => Shot("line-powershell"));
        Clear(test);

        // cmd, its marks out of its prompt
        TerminalProfile? cmd = panel.Profiles.Find(profile => profile.Id == "cmd");
        test.Check("there is a cmd", () => cmd is not null);
        if (cmd is null)
            return;

        test.Run(() => panel.NewTab(cmd, Path.GetTempPath()));
        test.Settle(settle);
        test.WaitFor(() => View().Profile == cmd && View().Screen.MarksSeen && View().Screen.AtPrompt, 10.0f);
        test.Check("cmd says its marks through its prompt", () => View().Screen.MarksSeen && View().Screen.AtPrompt);
        test.Check("and its folder", () => string.Equals(View().WorkingDirectory?.TrimEnd('\\'), Path.GetTempPath().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

        test.Run(() => View().Send("echo dusk-cmd-line /a %TEMP%"));
        test.WaitFor(() => View().LineText == "echo dusk-cmd-line /a %TEMP%", 5.0f);
        test.Check("cmd's own words, switches and variables are coloured", () => View().LineKindsText() == "cccc...............ff.vvvvvv");
        test.Run(() => Shot("line-cmd"));

        int before = 0;
        test.Run(() =>
        {
            before = View().Blocks().Count;
            View().Send("\r");
        });
        test.WaitFor(() => View().Blocks().Count > before && View().Screen.AtPrompt, 8.0f);
        test.Check("a command in cmd is a block with its output worked out", () =>
            View().Blocks() is { Count: >= 2 } blocks && View().Screen.CommandOf(blocks[^2]) == "echo dusk-cmd-line /a %TEMP%"
            && View().Screen.OutputOf(blocks[^2]).Trim() == "dusk-cmd-line /a " + Environment.GetEnvironmentVariable("TEMP"));
        test.Check("and nothing is coloured once it ran", () => View().LineText.Length == 0);

        // The list of what finishes a word, cmd has nothing of the kind
        test.Check("cmd gets the list of what finishes a word", () => View().Completer is not null);
        test.Run(() => View().Type("t"));
        test.Run(() => View().Type("y"));
        test.WaitFor(() => View().Completions.Count > 0 && View().LineText == "ty", 5.0f);
        test.Run(() => Shot("line-completion"));
        test.Check("typing the start of a command lists the commands it could be, the shortest first", () =>
            View().Completions is { Count: > 0 } items && items[0].Text == "type" && items[0].Command);
        test.Run(() => View().Type("p"));
        test.WaitFor(() => View().Completions.Count > 0 && View().LineText == "typ", 5.0f);
        test.Run(() => View().AcceptCompletion());
        test.WaitFor(() => View().LineText == "type", 5.0f);
        test.Check("tab types the rest of it", () => View().LineText == "type" && View().Completions.Count == 0);
        Clear(test);
    }

    // What is typed at the prompt cleared, escape on its own and a moment for the shell to take it as that
    // The line at the prompt emptied: Escape in PowerShell (and cmd), Ctrl+E then Ctrl+U in bash and zsh, where Escape
    // is the start of a key with meta and would take the next letter typed with it
    private void Clear(DuskScript test)
    {
        test.Run(() => panel.FocusedView!.Send(OperatingSystem.IsWindows() ? "\u001b" : "\u0005\u0015"));
        test.Wait();
        test.Wait();
    }
}
