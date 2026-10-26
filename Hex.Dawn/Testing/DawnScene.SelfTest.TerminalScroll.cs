using System.Numerics;
using System.Text;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test of the scroll bar and the minimap of the panel's terminal. Rows written into it by hand, the thumb
// dragged to the top, the track pressed under it for a page, more written while it's read up there, the minimap pressed
// near its bottom and dragged back up, the view at the bottom following what's written, and pictures of it at 100% and
// 150% with HEX_SELFTEST_SHOTS set.
internal sealed partial class DawnScene
{
    private void TestTerminalScroll(HexPointerScript test)
    {
        TerminalView? view = null;
        TerminalView View() => view!;

        // A point in a rectangle of the terminal's, so far across and up it as shares, where the pointer goes
        Func<Vector2> On(Func<UIRect> bounds, float x, float y) => () =>
        {
            UIRect rect = bounds();
            return View().Module!.ToWorld(new Vector2(rect.Min.X + rect.Width * x, rect.Min.Y + rect.Height * y));
        };

        string TopText() => View().Screen.RowAt(View().TopRow).Text().TrimEnd();

        // A tab of its own, so the rows written by hand are nothing a shell in use knows of
        test.Run(() =>
        {
            if (!bottom.Shown)
                ToggleTerminalPage();
            terminalPanel.NewTab(terminals.UsualProfile, testFolder);
        });
        test.WaitFor(() => terminalPanel.FocusedView?.Screen.Text().Trim().Length > 0, 10.0f);
        test.Run(() =>
        {
            view = terminalPanel.FocusedView;
            View().Feed(TerminalRows("hex-scroll", 300));
        });
        test.Settle(0.3f);
        test.Check("terminal scroll: the panel's terminal has the minimap (on as it comes) and a scroll bar over its scrollback", () =>
            terminals.Settings.Minimap && View().Minimap && View().MinimapRowsDrawn > 0 && View().ScrollBarTrack.Width > 0.0f
            && View().ScrollBarThumb.Height < View().ScrollBarTrack.Height && View().ScrolledUp == 0);
        TerminalShot(test, "terminal_scroll.png");

        test.Drag(On(() => View().ScrollBarThumb, 0.5f, 0.5f), On(() => View().ScrollBarTrack, 0.5f, 0.995f));
        test.Wait();
        test.Check("terminal scroll: the thumb dragged to the top of the track goes to the top of the scrollback", () => View().TopRow == 0);

        test.Click(On(() => View().ScrollBarTrack, 0.5f, 0.02f));
        test.Wait();
        test.Check("terminal scroll: a press on the track under the thumb goes down a page", () => View().TopRow == View().Rows - 1);

        string reading = string.Empty;
        test.Run(() =>
        {
            reading = TopText();
            View().Feed(TerminalRows("hex-more", 50));
        });
        test.Wait();
        test.Check("terminal scroll: more written while it's read up there leaves it on what it shows", () => reading.Length > 0 && TopText() == reading && View().ScrolledUp > 0);

        test.Click(On(() => View().MinimapBounds, 0.5f, 0.03f));
        test.Wait();
        test.Check("terminal scroll: a press near the minimap's bottom goes a long way down", () => View().TopRow > View().Rows * 2);
        test.Drag(On(() => View().MinimapBounds, 0.5f, 0.03f), On(() => View().MinimapBounds, 0.5f, 0.995f));
        test.Wait();
        test.Check("terminal scroll: and the minimap dragged to its top goes back up", () => View().TopRow == 0);

        test.Run(() =>
        {
            View().ScrollToEnd(top: false);
            View().Feed(TerminalRows("hex-follow", 30));
        });
        test.Wait();
        test.Check("terminal scroll: at the bottom it follows what's written", () =>
            View().ScrolledUp == 0 && View().TopRow == View().Screen.Scrollback.Count && View().Screen.AllText().Contains("hex-follow-029"));

        // At 150%, the screen this is mostly looked at on
        float scaleWas = 1.0f;
        test.Run(() =>
        {
            scaleWas = compositor.Scale;
            compositor.Scale = 1.5f * compositor.Scale / compositor.UIScale;
            View().ScrollToRow(View().Screen.Scrollback.Count / 2);
        });
        test.Settle(0.3f);
        test.Hover(On(() => View().ScrollBarTrack, 0.5f, 0.5f));
        test.Settle(0.3f);
        TerminalShot(test, "terminal_scroll_150.png");
        test.Check("terminal scroll: at 150% the bar and the minimap are still drawn", () =>
            View().ScrollBarTrack.Width > 0.0f && View().MinimapRowsDrawn > 0);
        test.Run(() =>
        {
            compositor.Scale = scaleWas;
            terminalPanel.CloseTab(terminalPanel.Current!);
        });
        test.Settle(0.3f);
    }

    /// <summary>Rows written by hand, numbered, in the colours by turns with runs of different lengths after them.</summary>
    private static string TerminalRows(string name, int count)
    {
        var rows = new StringBuilder();
        for (int i = 0; i < count; i++)
            rows.Append($"\u001b[3{i % 7 + 1}m{name}-{i:000}\u001b[0m {new string('=', i * 7 % 50)} \u001b[1;3{(i + 3) % 7 + 1}mend\u001b[0m\r\n");
        return rows.ToString();
    }

    // A picture of the screen into HEX_SELFTEST_SHOTS, when it's set
    private void TerminalShot(HexPointerScript test, string name)
    {
        test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, name));
        });
        test.Settle(0.3f);
    }
}
