using System.Text.RegularExpressions;

using Horizon.Core.Processes;
using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;

namespace Hex.Dusk;

// The self test of a program with a screen of its own run in the shell, micro, when it's on the PATH, and through it
// everything a console TUI asks of a terminal. Every key goes through Dusk's own encoding (TerminalKeys, as a key
// pressed would), the mouse as the terminal reports it, a paste as Dusk pastes, and what micro made of it is read off
// its status line, where it says the line and the column the cursor is on, and out of the file it saved. Its own config
// folder, so somebody's bindings don't change what the keys do. Up and Down were the history's once and put an old line
// in place of the one being edited, which is where this started.
internal sealed partial class DuskScene
{
    private void TestMicro(DuskScript test)
    {
        TerminalView View() => panel.FocusedView!;

        string? micro = ProcessEnvironment.Path().Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder.Trim('"'), "micro.exe")).FirstOrDefault(File.Exists);
        TerminalProfile? shell = panel.Profiles.Find(profile => profile.Id is "pwsh" or "powershell");
        if (micro is null || shell is null)
        {
            test.Check("micro: not on this machine, nothing to try", () => true);
            return;
        }

        string folder = Path.Combine(Path.GetTempPath(), "dusk-micro-selftest");
        string config = Path.Combine(folder, "config");
        string file = Path.Combine(folder, "notes.txt");
        string code = Path.Combine(folder, "colours.go");
        string[] lines = [.. Enumerable.Range(1, 200).Select(i => i == 120 ? "a needle in here" : $"line {i}")];

        // Keys as Dusk sends them for a key pressed, the text as typed
        void Press(Key key, TerminalKeys.Modifiers modifiers = TerminalKeys.Modifiers.None) =>
            View().Type(TerminalKeys.Key(key, modifiers, View().Screen.ApplicationCursorKeys, View().Screen.KeyboardFlags)!);
        void Control(Key key) => Press(key, TerminalKeys.Modifiers.Control);
        void Typed(string text) => View().Type(TerminalKeys.Character(text, TerminalKeys.Modifiers.None, View().Screen.KeyboardFlags));
        void Click(int column, int row, int button = 0)
        {
            var screen = View().Screen;
            View().Type(TerminalKeys.Mouse(button, column, row, TerminalKeys.Modifiers.None, true, false, screen.MouseSgr));
            if (button < 64)
                View().Type(TerminalKeys.Mouse(button, column, row, TerminalKeys.Modifiers.None, false, false, screen.MouseSgr));
        }

        // Where micro says the cursor is, off its status line, (line,col)
        (int Line, int Column) Where()
        {
            var screen = View().Screen;
            for (int y = screen.Grid.Count - 1; y >= 0; y--)
            {
                Match found = Regex.Match(screen.Grid[y].Text(), @"\((\d+),(\d+)\)");
                if (found.Success)
                    return (int.Parse(found.Groups[1].Value), int.Parse(found.Groups[2].Value));
            }

            return (0, 0);
        }

        int StatusLines() => View().Screen.Grid.Count(row => Regex.IsMatch(row.Text(), @"\(\d+,\d+\)"));
        // What micro saved, nothing while it's still writing it
        string Saved()
        {
            try
            {
                return File.Exists(file) ? File.ReadAllText(file).Replace("\r", string.Empty) : string.Empty;
            }
            catch (IOException)
            {
                return string.Empty;
            }
        }
        string Screen() => View().Screen.Text();
        void Step(Action action, Func<bool> until)
        {
            test.Run(action);
            test.WaitFor(until, 3.0f);
            test.Settle(0.05f);
        }

        test.Run(() =>
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "settings.json"), "{ \"mouse\": true, \"autosave\": 0, \"savecursor\": false, \"saveundo\": false }");
            File.WriteAllText(file, string.Join('\n', lines) + "\n");
            File.WriteAllText(code, "package main\n\nimport \"fmt\"\n\n// a comment\nfunc main() {\n\tfmt.Println(\"hello\", 42)\n}\n");
            panel.NewTab(shell, folder);
        });
        test.WaitFor(() => View().Profile == shell && View().Screen.MarksSeen && View().Screen.AtPrompt, 15.0f);
        test.Check("micro: at the prompt Up is the history's", () => TerminalHost.AtShellPrompt(View().Screen));

        // Starting, the alternate screen and the status line
        test.Run(() => View().Send($"micro -config-dir \"{config}\" \"{file}\"\r"));
        test.WaitFor(() => Where() == (1, 1) && Screen().Contains("line 3"), 15.0f);
        test.Settle(0.5f);
        test.Check("micro: starts with the file and its status line, the cursor at 1,1", () => Where() == (1, 1) && Screen().Contains("line 1") && Screen().Contains("notes.txt"));
        test.Check("micro: while it runs the arrows aren't the history's", () =>
            !TerminalHost.AtShellPrompt(View().Screen) && !host.KeyPressed(new UIKeyPress(Key.Down, false, false, false, false)));
        test.Check("micro: and the system says the shell has a program going, whatever the marks say", () => View().Process?.RunsAProgram == true);

        // Moving about
        Step(() => { Press(Key.Right); Press(Key.Right); Press(Key.Right); }, () => Where() == (1, 4));
        test.Check("micro: Right moves the cursor along", () => Where() == (1, 4));
        Step(() => Press(Key.Left), () => Where() == (1, 3));
        test.Check("micro: Left moves it back", () => Where() == (1, 3));
        Step(() => { Press(Key.Down); Press(Key.Down); }, () => Where().Line == 3);
        test.Check("micro: Down goes down a line", () => Where().Line == 3);
        Step(() => Press(Key.Up), () => Where().Line == 2);
        test.Check("micro: Up comes back up", () => Where().Line == 2);
        Step(() => Press(Key.End), () => Where() == (2, 7));
        test.Check("micro: End goes to the end of the line", () => Where() == (2, 7));
        Step(() => Press(Key.Home), () => Where() == (2, 1));
        test.Check("micro: Home to its start", () => Where() == (2, 1));
        Step(() => Press(Key.Right, TerminalKeys.Modifiers.Control), () => Where().Column > 1);
        test.Check("micro: Ctrl+Right goes a word along", () => Where() is (2, > 1 and <= 7));
        Step(() => Press(Key.PageDown), () => Where().Line > 10);
        test.Check("micro: Page Down goes a screen down", () => Where().Line > 10);
        Step(() => Press(Key.PageUp), () => Where().Line < 10);
        test.Check("micro: Page Up comes back", () => Where().Line < 10);
        Step(() => Press(Key.End, TerminalKeys.Modifiers.Control), () => Where().Line >= 200);
        test.Check("micro: Ctrl+End goes to the end of the file, the screen scrolled with it", () => Where().Line >= 200 && Screen().Contains("line 200"));
        Step(() => Press(Key.Home, TerminalKeys.Modifiers.Control), () => Where() == (1, 1));
        test.Check("micro: Ctrl+Home back to the start", () => Where() == (1, 1) && Screen().Contains("line 1"));

        // Typing and taking away
        Step(() => Typed("hello "), () => Screen().Contains("hello line 1"));
        test.Check("micro: what's typed goes in where the cursor is", () => Screen().Contains("hello line 1") && Where() == (1, 7));
        Step(() => { Press(Key.Backspace); Press(Key.Backspace); }, () => Screen().Contains("hellline 1"));
        test.Check("micro: Backspace takes away before the cursor", () => Screen().Contains("hellline 1"));
        Step(() => Press(Key.Delete), () => Screen().Contains("helline 1"));
        test.Check("micro: Delete takes away after it", () => Screen().Contains("helline 1"));
        Step(() => Press(Key.Enter), () => Where().Line == 2);
        test.Check("micro: Enter splits the line", () => Where() == (2, 1) && Screen().Contains("ine 1"));
        Step(() => Press(Key.Tab), () => Where().Column > 1);
        test.Check("micro: Tab indents", () => Where().Column > 1);

        // Accents and wide letters typed go into the file whole. Micro shows the wide ones as ? through ConPTY, Windows'
        // console host does that to it before Dusk sees a byte, so they're checked in the file it saves
        Step(() => Typed("é漢"), () => Screen().Contains("é"));
        test.Check("micro: an accent typed is shown as typed", () => Screen().Contains("é"));
        Step(() => Control(Key.S), () => Saved().Contains('é'));
        test.Check("micro: Ctrl+S saves, accents and wide letters in the file as typed", () => Saved().StartsWith("hell\n\té漢ine 1\n", StringComparison.Ordinal));

        // Undo and redo
        Step(() => Control(Key.Z), () => !Screen().Contains("é"));
        test.Check("micro: Ctrl+Z undoes", () => !Screen().Contains("é"));
        Step(() => Control(Key.Y), () => Screen().Contains("é"));
        test.Check("micro: Ctrl+Y redoes", () => Screen().Contains("é"));

        // Selecting with shift, copying and pasting
        Step(() => { Press(Key.Down); Press(Key.Home); for (int i = 0; i < 4; i++) Press(Key.Right, TerminalKeys.Modifiers.Shift); }, () => Where() == (3, 5));
        test.Check("micro: Shift and the arrows select", () => Where() == (3, 5));
        Step(() => { Control(Key.C); Press(Key.End); Control(Key.V); }, () => Screen().Contains("line 2line"));
        test.Check("micro: Ctrl+C copies what's selected and Ctrl+V pastes it", () => Screen().Contains("line 2line"));

        // A paste from outside, bracketed when micro asked for that, its lines kept as they were
        Step(() => { Press(Key.End); Press(Key.Enter); View().Type(TerminalKeys.Paste("pasted one\r    pasted two", View().Screen.BracketedPaste)); }, () => Screen().Contains("pasted two"));
        test.Check("micro: a paste goes in whole, its indentation as it was", () => Screen().Contains("pasted one") && Screen().Contains("    pasted two") && !Screen().Contains("        pasted two"));

        // Finding and the command bar
        Step(() => Control(Key.F), () => Screen().Contains("Find"));
        Step(() => { Typed("needle"); Press(Key.Enter); }, () => Where().Line > 100);
        test.Check("micro: Ctrl+F finds, the cursor on what was found", () => Where().Line > 100 && Screen().Contains("a needle in here"));
        Step(() => Press(Key.Escape), () => !Screen().Contains("Find:"));
        Step(() => { Control(Key.E); Typed("goto 150"); Press(Key.Enter); }, () => Where().Line == 150);
        test.Check("micro: Ctrl+E is its command bar, goto 150 goes there", () => Where().Line == 150);

        // The mouse, a click puts the cursor there and the wheel scrolls
        Step(() => { Control(Key.E); Typed("goto 1"); Press(Key.Enter); }, () => Where().Line == 1);
        test.Check("micro: asked for the mouse", () => View().Screen.MouseMode != TerminalMouseMode.None);
        // The line clicked is the one whose number is in the gutter of the row clicked, a few rows down
        int clickedLine = -1;
        Step(() =>
        {
            Match number = Regex.Match(View().Screen.Grid[6].Text(), @"^\s*(\d+)");
            clickedLine = number.Success ? int.Parse(number.Groups[1].Value) : -1;
            Click(8, 6);
        }, () => Where().Line == clickedLine);
        test.Check("micro: a click puts the cursor on the line clicked", () => clickedLine > 1 && Where().Line == clickedLine);
        Step(() => { for (int i = 0; i < 5; i++) Click(10, 5, 65); }, () => !View().Screen.Grid[0].Text().Contains("line 2"));
        test.Check("micro: the wheel scrolls it", () => !View().Screen.Grid.Take(3).Any(row => Regex.IsMatch(row.Text(), @"\bline [2-4]$")));

        // Splits of its own, and help
        Step(() => { Control(Key.E); Typed("vsplit"); Press(Key.Enter); }, () => StatusLines() >= 1 && Regex.Matches(Screen(), @"\(\d+,\d+\)").Count >= 2);
        test.Check("micro: vsplit makes two panes side by side, a status line each", () => Regex.Matches(Screen(), @"\(\d+,\d+\)").Count >= 2);
        Step(() => Control(Key.Q), () => Regex.Matches(Screen(), @"\(\d+,\d+\)").Count == 1);
        test.Check("micro: Ctrl+Q closes the one pane", () => Regex.Matches(Screen(), @"\(\d+,\d+\)").Count == 1);
        // Its help is a pane of its own over the file, a second status line
        bool HelpShown() => Regex.Matches(Screen(), @"\(\d+,\d+\)").Count >= 2 || Screen().Contains("Ctrl-q") || Screen().Contains("Default Keys");
        test.Run(() => Control(Key.G));
        test.WaitFor(HelpShown, 8.0f);
        test.Settle(0.1f);
        test.Check("micro: Ctrl+G opens its help", HelpShown);
        Step(() => Control(Key.Q), () => Screen().Contains("notes.txt"));

        // The pane made narrower, micro drawn again to the new width
        int widthWas = 0;
        test.Run(() => widthWas = View().Screen.Columns);
        Step(() => Run("split_right"), () => panel.Current!.Views.Count() == 2);
        Step(() => panel.FocusPane(panel.Current!.Views.First()), () => View().Screen.Columns < widthWas);
        test.WaitFor(() => Where().Line > 0 && Screen().Contains("notes.txt"), 3.0f);
        test.Check("micro: a narrower pane has micro drawn again at its width", () =>
            View().Screen.Columns < widthWas && Where().Line > 0 && Screen().Contains("notes.txt"));
        Step(() => { panel.FocusPane(panel.Current!.Views.Last()); Run("close_pane"); }, () => panel.Current!.Views.Count() == 1);

        // Leaving with changes asks, and leaving puts the shell's screen back
        Step(() => Typed("unsaved"), () => Screen().Contains("unsaved"));
        Step(() => Control(Key.Q), () => Screen().Contains("Save changes"));
        test.Check("micro: Ctrl+Q with changes asks whether to save them", () => Screen().Contains("Save changes"));
        Step(() => Typed("n"), () => View().Screen.AtPrompt);
        test.WaitFor(() => View().Screen.AtPrompt && !View().Screen.AlternateScreen, 10.0f);
        test.Check("micro: and n leaves without them, back at the shell's own screen", () =>
            View().Screen.AtPrompt && !View().Screen.AlternateScreen && !Saved().Contains("unsaved"));
        test.Check("micro: and back at the prompt Up is the history's again", () => TerminalHost.AtShellPrompt(View().Screen));
        test.WaitFor(() => View().Process?.RunsAProgram == false, 5.0f);
        test.Check("micro: and the shell has no program going", () => View().Process?.RunsAProgram == false);

        // Colours, a file of Go in micro's own colours
        test.Run(() => View().Send($"micro -config-dir \"{config}\" \"{code}\"\r"));
        test.WaitFor(() => Screen().Contains("Println") && Where().Line > 0, 15.0f);
        test.Settle(0.4f);
        test.Check("micro: colours code, more than a couple of colours on the screen", () =>
        {
            var colours = new HashSet<TerminalColor>();
            foreach (var row in View().Screen.Grid)
                foreach (var cell in row.Cells)
                    if (!cell.IsBlank)
                        colours.Add(cell.Style.Foreground);
            return colours.Count >= 3;
        });
        Step(() => Control(Key.Q), () => View().Screen.AtPrompt);
        test.WaitFor(() => View().Screen.AtPrompt, 10.0f);
        test.Run(() =>
        {
            if (panel.Tabs.Count > 1)
                panel.CloseTab(panel.Current!);
        });
        test.Settle(0.3f);
    }
}
