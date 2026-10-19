using Horizon.Core.Processes;
using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;

namespace Hex.Dusk;

// The self test of Claude Code in a terminal, when it's on the PATH. It's a TUI drawn on the main screen (Ink, no
// alternate screen and no application cursor keys asked for), which is what the arrows going to the shell's history and
// not to it got wrong. Started in an empty folder it asks whether to trust the folder, a list moved about with the arrows,
// and it's left with Escape before anything is said yes to.
internal sealed partial class DuskScene
{
    private void TestClaude(DuskScript test)
    {
        TerminalView View() => panel.FocusedView!;

        string? claude = ProcessEnvironment.Path().Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder.Trim('"'), "claude.exe")).FirstOrDefault(File.Exists);
        TerminalProfile? shell = panel.Profiles.Find(profile => profile.Id is "pwsh" or "powershell");
        if (claude is null || shell is null)
        {
            test.Check("claude: not on this machine, nothing to try", () => true);
            return;
        }

        string folder = Path.Combine(Path.GetTempPath(), "dusk-claude-selftest-" + Environment.ProcessId);
        void Press(Key key) => View().Type(TerminalKeys.Key(key, TerminalKeys.Modifiers.None, View().Screen.ApplicationCursorKeys, View().Screen.KeyboardFlags)!);

        // Which of the list's rows has the pointer in front of it, the first row with ❯ on it, and its text
        string Chosen() => View().Screen.Grid.Select(row => row.Text()).FirstOrDefault(text => text.Contains('❯')) ?? string.Empty;

        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            panel.NewTab(shell, folder);
        });
        test.WaitFor(() => View().Profile == shell && View().Screen.MarksSeen && View().Screen.AtPrompt, 15.0f);
        test.Run(() => View().Send("claude\r"));
        test.WaitFor(() => Chosen().Length > 0, 30.0f);
        test.Settle(1.0f);
        string before = string.Empty;
        test.Run(() =>
        {
            before = Chosen();
        });
        test.Check("claude: comes up with a list to choose from", () => before.Length > 0);
        test.Check("claude: while it runs the arrows are its own, not the history's", () =>
            !TerminalHost.AtShellPrompt(View().Screen) && !host.KeyPressed(new UIKeyPress(Key.Down, false, false, false, false)) && !host.KeyPressed(new UIKeyPress(Key.Up, false, false, false, false)));
        test.Run(() => Press(Key.Down));
        test.WaitFor(() => Chosen() != before, 3.0f);
        test.Check("claude: Down moves its choice down the list", () => Chosen().Length > 0 && Chosen() != before);
        test.Run(() => Press(Key.Up));
        test.WaitFor(() => Chosen() == before, 3.0f);
        test.Check("claude: and Up back", () => Chosen() == before);

        // Out without saying yes to anything, escape on the trust question leaves it
        test.Run(() => Press(Key.Escape));
        test.WaitFor(() => View().Screen.AtPrompt, 10.0f);
        test.Run(() =>
        {
            if (!View().Screen.AtPrompt)
                View().Send("\u0003\u0003");
        });
        test.WaitFor(() => View().Screen.AtPrompt, 10.0f);
        test.Check("claude: escape leaves it, back at the prompt", () => View().Screen.AtPrompt);
        test.Run(() =>
        {
            if (panel.Tabs.Count > 1)
                panel.CloseTab(panel.Current!);
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        });
        test.Settle(0.3f);
    }
}
