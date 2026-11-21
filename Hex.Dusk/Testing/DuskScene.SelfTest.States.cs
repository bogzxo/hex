using Hex.Shell;
using Horizon.UI;
using Key = Horizon.Input.Key;
using Horizon.UI.Components;

namespace Hex.Dusk;

// The self test of a shell's state being legible. A tab's icon says a command is running, that a tab not open rang its
// bell (until it's opened), and that its program ended badly. A shell that ends with an error keeps its pane, its
// cursor gone and a bar along the bottom saying the code, and Enter starts it again; one that ends well still goes.
// The find bar sits over the pane it searches and leaves every pane the size it was.
internal sealed partial class DuskScene
{
    private void TestStates(DuskScript test)
    {
        const string ESC = "\u001b", BEL = "\u0007";
        TerminalTab? marked = null, other = null;
        test.Run(() =>
        {
            marked = panel.NewTab(Fixture("states", "terminal"));
            marked.FocusedView!.Feed($"{ESC}c{ESC}]133;A{BEL}> {ESC}]133;B{BEL}sleep 5\r\n{ESC}]133;C{BEL}");
        });
        test.Settle(0.2f);
        test.Check("states: a command running lights the tab's icon in the accent", () =>
            marked!.State == TerminalTabState.Busy && marked.FocusedView!.Screen.CommandRunning);
        test.Run(() => marked!.FocusedView!.Feed($"{ESC}]133;D;0{BEL}{ESC}]133;A{BEL}> {ESC}]133;B{BEL}"));
        test.Settle(0.2f);
        test.Check("states: done, the tab is quiet again", () => marked!.State == TerminalTabState.Idle);

        // A bell while another tab is open marks the tab until it's opened
        test.Run(() =>
        {
            other = panel.NewTab(Fixture("other", "terminal"));
            marked!.FocusedView!.Feed("\a");
        });
        test.Settle(0.2f);
        test.Check("states: a bell in a tab that isn't open puts a bell on it", () => marked!.State == TerminalTabState.Rang && other!.State == TerminalTabState.Idle);
        test.Run(() => Shot("states-bell"));
        test.Run(() => panel.Select(marked));
        test.Settle(0.2f);
        test.Check("states: opening it takes the bell off", () => marked!.State == TerminalTabState.Idle);
        test.Run(() => panel.Select(other));
        test.Settle(0.2f);
        test.Check("states: and it stays off once seen", () => marked!.State == TerminalTabState.Idle);
        test.Run(() =>
        {
            panel.CloseTab(marked!);
            panel.CloseTab(other!);
        });
        test.Settle(settle);

        // A real shell that ends badly keeps its pane, says so, and Enter starts it again
        TerminalView? shell = null;
        int rowsBefore = 0;
        test.Run(() =>
        {
            panel.CloseOnExit = true;
            shell = panel.FocusedView;
            rowsBefore = shell!.Rows;
            shell.Send(OperatingSystem.IsWindows() ? "exit 3\r" : "exit 3\n");
        });
        test.WaitFor(() => shell!.EndedWith is not null, 8.0f);
        test.Settle(0.3f);
        test.Check("states: a shell that ended with an error keeps its pane though panes close on exit", () =>
            panel.Tabs.Count == 1 && panel.FocusedView == shell && shell!.EndedWith == 3);
        test.Check("states: its bar says the code in Hex's words, and its tab is marked", () =>
            shell!.EndedSaid == HexText.Get("terminal_page.exited_code", ("code", "3")) && panel.Current!.State == TerminalTabState.Failed);
        test.Check("states: the bar has room of its own, the cells give it a row or so", () => shell!.Rows < rowsBefore && shell.Rows >= rowsBefore - 2);
        test.Run(() => Shot("states-ended"));
        test.Settle(0.1f);
        test.Run(() => shell!.Press(new UIKeyPress(Key.Enter, false, false, false, false)));
        test.WaitFor(() => shell!.IsRunning && shell.Screen.Text().Trim().Length > 0, 10.0f);
        test.Settle(0.3f);
        test.Check("states: Enter starts it again in the same pane, the bar and the mark gone", () =>
            shell!.IsRunning && shell.EndedWith is null && panel.Current!.State != TerminalTabState.Failed && panel.FocusedView == shell && shell.Rows == rowsBefore);

        // One that ends well still goes with its pane, a second tab so Dusk keeps one
        TerminalTab? well = null;
        test.Run(() =>
        {
            well = panel.NewTab(Fixture("well", "terminal"));
            well.FocusedView!.End(0);
        });
        test.Settle(settle + 0.2f);
        test.Check("states: a program that ended well closes its pane as it did", () => !panel.Tabs.Contains(well!));

        // Find over the pane, nothing under it resized
        (int Columns, int Rows) size = default;
        test.Run(() =>
        {
            size = (panel.FocusedView!.Columns, panel.FocusedView.Rows);
            panel.BeginFind();
        });
        test.Settle(0.3f);
        test.Check("states: find opens over the top right of the pane it searches", () =>
            panel.Finding && panel.FindBounds.Width > 0 && panel.FocusedView!.Bounds.Contains(panel.FindBounds.Max - new System.Numerics.Vector2(1, 1))
            && panel.FindBounds.Max.X > panel.FocusedView.Bounds.Center.X);
        test.Check("states: and leaves the shell the size it was", () => (panel.FocusedView!.Columns, panel.FocusedView.Rows) == size);
        test.Run(() =>
        {
            if (compositor.Focus is TextBox box)
            {
                box.Text = "no-such-words-anywhere";
                box.OnChanged?.Invoke(box.Text);
            }
        });
        test.Settle(0.4f);
        test.Run(() => Shot("states-find-none"));
        test.Run(() => panel.CloseFind());
        test.Run(() => panel.CloseOnExit = host.Settings.CloseOnExit);
        test.Wait();
    }
}
