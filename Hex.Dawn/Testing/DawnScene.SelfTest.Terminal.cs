using Horizon.UI;
using Horizon.UI.Components;

using Key = Silk.NET.Input.Key;

namespace Hex.Dawn;

// The self test, the terminal page. A shell brought up with F6, typed into and split, the keys of Hex's that work from a
// terminal and the ones that don't, the console as a profile of it, a problem put in the Problems panel by the terminal,
// and the terminal's files being the shared ones in the test's own home.
internal sealed partial class DawnScene
{
    private void TestTerminal(HexPointerScript test)
    {
        test.Check("terminal: the files are the shared ones, in the test's own home", () =>
            terminals.Settings.Path?.Contains("horizon-hex-selftest-dusk") == true && terminals.Options.KeysFiles[^1].Contains("horizon-hex-selftest-dusk"));
        test.Check("terminal: the core plugin is loaded out of the engine, with the commands Dusk has", () =>
            terminals.Plugins.Any(plugin => plugin.Name == "Core" && plugin.Failed is null) && terminals.Commands.Contains("split_right") && terminals.Commands.KeyOf("new_tab") == "Ctrl+Shift+T");
        test.Check("terminal: Hex's own keys are terminal commands as well", () => terminals.Commands.KeyOf("hex_terminal") == "F6" && terminals.Commands.KeyOf("hex_panel") == "Ctrl+J");
        test.Check("terminal: the console and the HIDL REPL are profiles", () =>
            terminals.ProfileCalled(HexRunning.CONSOLE_PROFILE) is { Kind: TerminalProfileKind.Embedded } && terminals.ProfileCalled(TerminalHost.HIDL_PROFILE) is not null);

        test.Run(() =>
        {
            // Off first, a tab closed with them on folds away for a moment and is still there
            terminalPanel.AnimationTime = 0.0f;
            foreach (TerminalTab tab in terminalPanel.Tabs.ToArray())
                terminalPanel.CloseTab(tab);
        });
        test.Wait();
        test.Run(ToggleTerminalPage);
        test.Wait();
        test.Check("terminal: F6 brings up the page with a shell in it, the keyboard in it", () =>
            bottom.Shown && bottom.Selected == CONSOLE_PAGE && terminalPanel.Tabs.Count == 1 && compositor.Focus == terminalPanel.FocusedView
            && terminalPanel.FocusedView?.Process is { IsRunning: true });
        test.WaitFor(() => terminalPanel.FocusedView?.Screen.Text().Trim().Length > 0, 10.0f);
        test.Check("terminal: the shell wrote its prompt", () => terminalPanel.FocusedView?.Screen.Text().Trim().Length > 0);

        test.Run(() => terminalPanel.FocusedView?.Send("echo hex-terminal\r"));
        test.WaitFor(() => TerminalSays("hex-terminal", 2), 8.0f);
        test.Check("terminal: a line runs in it and comes back", () => TerminalSays("hex-terminal", 2));

        int documentsBefore = 0;
        test.Run(() => documentsBefore = documents.Count);
        test.Run(() => terminals.KeyPressed(new UIKeyPress(Key.W, true, false, false, false)));
        test.Wait();
        test.Check("terminal: Ctrl+W in a terminal is the shell's, no file of Hex's closes", () => documents.Count == documentsBefore);

        test.Run(() => terminals.KeyPressed(new UIKeyPress(Key.D, true, true, false, false)));
        test.Wait();
        test.Check("terminal: Ctrl+Shift+D splits it, the same key as Dusk's", () => terminalPanel.Current?.Tree.Count == 2);
        test.Run(() => terminals.Run("close_pane"));
        test.Wait();

        test.Run(() => terminals.KeyPressed(new UIKeyPress(Key.J, true, false, false, false)));
        test.Wait();
        test.Check("terminal: Ctrl+J from a terminal puts the panel away", () => !bottom.Shown);
        test.Run(() => ToggleTerminalFocus());
        test.Wait();
        test.Check("terminal: Ctrl+` brings it back with the keyboard in the terminal", () => bottom.Shown && compositor.Focus is TerminalView);

        test.Check("terminal: the palette has the terminal's commands", () => AllCommands().Any(command => command.Text.StartsWith("Terminal: Split Right")));

        test.Run(() => running.AskConsole("40 + 2"));
        test.WaitFor(() => running.consoleAsked is null && running.LastConsoleLine() == "42", 3.0f);
        test.Check("terminal: the console is a tab of it, a line run in the file", () => running.LastConsoleLine() == "42" && running.ConsoleView() is { } view && terminalPanel.TabOf(view) is not null);

        test.Run(() =>
        {
            settings.RunToTerminal = true;
            running.RunCode();
        });
        test.Wait();
        test.Check("terminal: with run_to_terminal F5 says what the file did in the console", () => running.ConsoleSaid($"{document.Name}, run") && running.runOutput.Count > 0 && running.ConsoleSaid(running.runOutput[^1]));
        test.Run(() => settings.RunToTerminal = false);

        test.Run(() => AddTerminalProblem("Program.cs", 12, 5, "CS1002: ; expected", false));
        test.Wait();
        test.Check("terminal: a problem the terminal finds is in the Problems panel", () => problemList.Any(problem => problem.Text.Contains("CS1002") && problem.Line == 11));
        test.Run(ClearTerminalProblems);
        test.Wait();
        test.Check("terminal: and goes when it is cleared", () => !problemList.Any(problem => problem.Text.Contains("CS1002")));

        // A compiler's error in a shell's output, into the Problems panel, and out again when the next line is run
        test.Run(() => terminalPanel.Select(0));
        // Said in two halves, so the line typed isn't itself the error: PowerShell's on Windows, sh's anywhere else
        test.Run(() => terminalPanel.FocusedView?.Send(OperatingSystem.IsWindows()
            ? "Write-Output ('C:\\hex\\Program.cs(12,5): error CS1002: ; expect' + 'ed')\r"
            : "printf '%s\\n' '/tmp/hex/Program.cs(12,5): error CS1002: ; expect''ed'\r"));
        test.WaitFor(() => problemList.Any(problem => problem.Text.Contains("CS1002")), 8.0f);
        test.Check("terminal: a compiler's error in a shell's output is in the Problems panel", () =>
            problemList.Any(problem => problem.Text == "; expected (CS1002)" && problem.Line == 11 && problem.Path.EndsWith("Program.cs")));
        // What a Return typed into the shell tells the host, the line goes to the shell and the old problems go
        test.Run(() => terminalPanel.FocusedView?.OnInput?.Invoke("\r"));
        test.Wait();
        test.Check("terminal: and goes once another line is run", () => !problemList.Any(problem => problem.Text.Contains("CS1002")));

        // A link in a terminal opens in the editor at its line
        string linked = Path.Combine(testFolder, "linked.hor");
        test.Run(() =>
        {
            File.WriteAllText(linked, "let one = 1;\nlet two = 2;\nlet three = 3;\n");
            var link = TerminalLinks.Find($"see {linked}:3", testFolder).Single();
            terminals.OpenLink(link);
        });
        test.Wait();
        test.Check("terminal: a path with a line in a terminal opens in the editor at the line", () =>
            document.Path is { } path && Path.GetFullPath(path) == Path.GetFullPath(linked) && code.Document.Caret.Line == 2);

        // The tabs kept in the workspace and put back
        test.Run(() =>
        {
            terminals.Run("split_right");
            explorer.KeepWorkspace();
        });
        test.Wait();
        test.Check("terminal: the workspace keeps the terminal's tabs", () => explorer.workspace.Terminal is { } kept && Horizon.HIDL.HIDLWriter.Write(kept).Contains("split: \"right\""));
        test.Run(() =>
        {
            foreach (TerminalTab tab in terminalPanel.Tabs.ToArray())
                terminalPanel.CloseTab(tab);
            OpenTerminals();
        });
        test.Wait();
        test.Check("terminal: and puts them back", () => terminalPanel.Tabs.Count >= 1 && terminalPanel.Tabs.Any(tab => tab.Tree.Count == 2));

        test.Check("terminal: the object a plugin gets has Hex's own on it", () => terminals.AnyObject is { } api && api.Properties.ContainsKey("file") && api.Properties.ContainsKey("problem"));

        // The dawn command typed into the shell, a file handed to Dawn and a file edited in place of the pane
        string handed = Path.Combine(testFolder, "from_the_terminal.txt"), edited = Path.Combine(testFolder, "edited_in_place.txt");
        TerminalView? shell = null;
        test.Run(() =>
        {
            File.WriteAllText(handed, "from the terminal\n");
            shell = terminalPanel.FocusedView;
        });
        test.WaitFor(() => shell?.Screen.AtPrompt == true, 10.0f);
        test.Run(() => shell?.Send($"dawn -open \"{handed}\"\r"));
        test.WaitFor(() => documents.Any(open => open.Path is { } path && Path.GetFullPath(path) == handed), 8.0f);
        test.Check("terminal: dawn -open in its shell opens the file in Dawn", () => documents.Any(open => open.Path is { } path && Path.GetFullPath(path) == handed));
        test.WaitFor(() => shell?.Screen.AtPrompt == true, 8.0f);
        test.Run(() => shell?.Send($"dawn \"{edited}\"\r"));
        test.WaitFor(() => shell?.InlineEditor is not null, 8.0f);
        test.Check("terminal: dawn file edits it in place of the pane", () => shell?.InlineEditor is not null && shell.InlinePath == edited);
        test.Run(() => shell?.CloseInline(force: true));
        test.Check("terminal: and Ctrl+Q has the shell back", () => shell?.InlineEditor is null);
    }

    private bool TerminalSays(string text, int times)
    {
        if (terminalPanel.FocusedView is not { } view)
            return false;

        string all = view.Screen.AllText();
        int count = 0;
        for (int at = all.IndexOf(text, StringComparison.Ordinal); at >= 0; at = all.IndexOf(text, at + text.Length, StringComparison.Ordinal))
            count++;
        return count >= times;
    }
}
