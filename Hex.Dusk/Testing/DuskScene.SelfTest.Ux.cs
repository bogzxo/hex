using Horizon.UI.Components;

namespace Hex.Dusk;

internal sealed partial class DuskScene
{
    private void TestUx(DuskScript test)
    {
        test.Check("ux: mode switch sits immediately beside the window controls", () =>
            modeToggle.Parent is { } modes && MathF.Abs(modes.Bounds.Max.X - top.ButtonBounds(0).Min.X) < 1);
        TerminalView View() => panel.FocusedView!;
        test.Run(() =>
        {
            // An embedded test pane keeps a real shell's asynchronous prompt repaint from replacing fixture text.
            panel.NewTab(new TerminalProfile("UX fixture", string.Empty, "terminal", "ux-fixture") { Kind = TerminalProfileKind.Embedded });
            for (int i = 0; i < 80; i++)
                View().Screen.Feed($"ux-line-{i}\r\n");
            View().ScrollToEnd(top: true);
        });
        test.Wait();
        test.Check("ux: scrollback offers a return to live output", () => statusBar.Items.Any(item => item.Text == Hex.Shell.HexText.Get("terminal_page.return_live") && item.IsVisible?.Invoke() == true));
        test.Run(() => host.Find());
        test.Wait();
        test.Check("ux: find opens inline and gives input to its query", () => panel.Finding && chrome.Dialog is null && compositor.Focus is TextBox);
        test.Run(() =>
        {
            var box = (TextBox)compositor.Focus!;
            box.Text = "ux-line-";
            box.OnChanged?.Invoke(box.Text);
        });
        test.Settle(0.2f);
        test.Check("ux: inline find searches scrollback", () => View().Found.Count >= 80);
        test.Run(() => Shot("ux-inline-find"));
        test.Settle(0.1f);
        test.Run(() => panel.CloseFind());
        test.Wait();
        test.Check("ux: closing find removes highlights and restores terminal input", () => !panel.Finding && compositor.Focus == View() && View().Found.Count == 0);
        test.Run(() => View().ScrollToEnd(top: false));
        test.Wait();
        test.Check("ux: return to live disappears at the bottom", () => !statusBar.Items.Any(item => item.Text == Hex.Shell.HexText.Get("terminal_page.return_live") && item.IsVisible?.Invoke() == true));
        test.Run(() => { panel.BeginFind(); panel.CloseTab(panel.Current!); });
        test.Wait();
        test.Check("ux: closing a searched pane cancels its bar and restores input to the remaining pane", () => !panel.Finding && compositor.Focus == panel.FocusedView);
    }
}
