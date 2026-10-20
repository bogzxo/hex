using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dusk;

// The self test of the commands as blocks and the keys that get about them, with the real shell started with its
// integration script, so its marks are real. No real keys, the commands are run as their keys would run them.
internal sealed partial class DuskScene
{
    private void TestBlocks(DuskScript test)
    {
        TerminalView View() => panel.FocusedView!;

        // What fails, writes a screen and more, and clears it, in cmd's words on Windows and sh's anywhere else
        bool windows = OperatingSystem.IsWindows();
        string failing = windows ? "cmd /c exit 4" : "sh -c 'exit 4'";
        string rows = windows ? "cmd /c \"for /L %i in (1,1,80) do @echo dusk-row-%i\"" : "for i in $(seq 1 80); do echo dusk-row-$i; done";
        string loop = windows ? "for /L" : "for i in";
        string clear = windows ? "cls" : "clear";

        test.WaitFor(() => View().Screen.MarksSeen && View().Screen.AtPrompt, 10.0f);
        test.Check("the shell is started with its integration and says its marks", () => View().Screen.MarksSeen && View().Screen.AtPrompt);

        // Counted from what's there already, the parts before leave commands of their own (the tui part does)
        int before = 0;
        test.Run(() => before = View().Blocks().Count);
        test.Run(() => View().Send("echo dusk-block-one\r"));
        test.WaitFor(() => View().Blocks().Count >= before + 1 && View().Screen.AtPrompt, 8.0f);
        test.Run(() => View().Send(failing + "\r"));
        test.WaitFor(() => View().Blocks().Count >= before + 2 && View().Blocks()[^2].Exit is not null && View().Screen.AtPrompt, 8.0f);
        test.Run(() => Shot("blocks"));

        test.Check("every command is a block with what it was and how it ended", () =>
        {
            var blocks = View().Blocks();
            return blocks.Count >= 3 && View().Screen.CommandOf(blocks[^3]) == "echo dusk-block-one" && blocks[^3].Exit == 0
                && View().Screen.CommandOf(blocks[^2]) == failing && blocks[^2].Failed;
        });

        test.Run(() => Run("select_output"));
        test.Check("select_output selects nothing when the last command wrote nothing", () => !View().HasSelection);
        test.Run(() =>
        {
            var blocks = View().Blocks();
            View().SelectOutput(blocks[^3]);
        });
        test.Check("and the output of a block is what it wrote", () => View().SelectedText.Trim() == "dusk-block-one");

        // More than a screen of output, so there is somewhere to jump to
        test.Run(() => View().Send(rows + "\r"));
        test.WaitFor(() => Count(View(), "dusk-row-80") >= 1 && View().Screen.AtPrompt && View().LastRun() is { } run && View().Screen.CommandOf(run).Contains(loop), 10.0f);
        test.Run(() => Run("previous_command"));
        test.Check("previous_command brings the prompt of the command before to the top", () =>
            View().LastRun() is { } run && View().TopRow == run.PromptRow);
        test.Run(() => Run("next_command"));
        test.Check("and next_command goes back down to where the shell is", () => View().TopRow == View().Screen.Scrollback.Count);

        test.Run(() => Run("list_commands"));
        test.Wait();
        test.Check("list_commands lists them, the newest first, the failed one said so", () =>
            chrome.Dialog is { } dialog && Descendants(dialog).OfType<ListBox>().First().Items is { Length: >= 3 } items
            && items[0].Contains(loop) && items[1].Contains(failing) && items[1].Contains("ended 4") && items[2].Contains("echo dusk-block-one"));
        test.Run(() => chrome.Dialog?.Cancel());
        test.Wait();

        // Find
        test.Run(() => host.Find("dusk-block"));
        test.Check("find lights every match and steps to the last", () => View().Found.Count >= 2 && View().Found.At == View().Found.Count - 1 && View().HasSelection);
        test.Run(() => Run("find_next"));
        test.Check("F3 goes round to the first", () => View().Found.At == 0);
        test.Run(() => View().ClearFind());

        // Rerun
        test.Run(() => View().Send("echo dusk-rerun\r"));
        // the output shows before ConPTY's marks have settled, so wait for the block to say what it was
        test.WaitFor(() => Count(View(), "dusk-rerun") >= 2 && View().Screen.AtPrompt && View().LastRun() is { } run && View().Screen.CommandOf(run) == "echo dusk-rerun", 8.0f);
        test.Run(() => Run("rerun"));
        test.WaitFor(() => Count(View(), "dusk-rerun") >= 4, 8.0f);
        test.Check("rerun runs the last command again", () => Count(View(), "dusk-rerun") >= 4);

        // History search lists every line run
        test.Run(() => Run("history_search"));
        test.Wait();
        test.Check("Ctrl+R lists the lines run", () =>
            chrome.Dialog is { } dialog && Descendants(dialog).OfType<ListBox>().First().Items.Length > 0);
        test.Run(() => chrome.Dialog?.Cancel());
        test.Wait();

        // A line run in a split
        test.Run(() =>
        {
            host.PutOnPrompt(View(), "echo dusk-in-a-split");
            Run("run_in_split");
        });
        test.Settle(settle);
        test.WaitFor(() => Count(View(), "dusk-in-a-split") >= 2, 10.0f);
        test.Check("run_in_split runs the line in a new split on the right", () => Shape() == "[o|F]" && Count(View(), "dusk-in-a-split") >= 2);
        test.Check("and takes it off the prompt it was typed at", () => !panel.Current!.Tree.Leaves.First().Content!.Screen.Grid[panel.Current.Tree.Leaves.First().Content!.Screen.CursorY].Text().Contains("dusk-in-a-split"));

        // Typing into both panes at once
        test.Run(() => Run("broadcast"));
        test.Check("broadcast is on for the tab", () => host.IsBroadcasting(panel.Current!));
        test.Run(() =>
        {
            // What a key typed into the focused pane tells the host, the focused pane is sent it the way a key does
            View().Send("echo dusk-to-all");
            View().OnInput?.Invoke("echo dusk-to-all");
            View().Send("\r");
            View().OnInput?.Invoke("\r");
        });
        test.WaitFor(() => panel.Current!.Views.All(view => Count(view, "dusk-to-all") >= 2), 10.0f);
        test.Check("a line typed into one pane goes to both", () => panel.Current!.Views.All(view => Count(view, "dusk-to-all") >= 2));
        test.Run(() => Run("broadcast"));
        test.Check("and off again", () => !host.IsBroadcasting(panel.Current!));

        // cls (clear) in the real shell, the bars of the commands before go with what it blanks, its own too
        test.Run(() => View().Send(clear + "\r"));
        test.Settle(0.5f);
        test.WaitFor(() => View().Screen.AtPrompt, 10.0f);
        test.Settle(0.3f);
        test.Check("cls takes the bars of the commands before it off the screen", () =>
        {
            int top = View().Screen.Scrollback.Count;
            var onScreen = View().Blocks().Where(block => block.EndRow >= top).ToList();
            return onScreen.Count == 1 && onScreen[0].Exit is null && !onScreen.Any(block => block.PromptRow < top);
        });
    }
}
