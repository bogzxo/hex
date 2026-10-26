using System.Text;

using Horizon.UI.Components;

namespace Hex.Dusk;

// The self test of the scroll bar and the minimap down the right of a terminal. A few hundred coloured rows written into
// it by hand, the thumb dragged to the top, the track pressed under it for a page, more written while it's read up there
// (it stays on what it shows), the minimap pressed near its bottom and its slider dragged back up, the view at the
// bottom following what's written, the bar gone on the alternate screen, and the minimap switched off in the settings.
internal sealed partial class DuskScene
{
    private void TestScroll(DuskScript test)
    {
        TerminalView View() => panel.FocusedView!;

        // The text of the row at the top of the view
        string TopText() => View().Screen.RowAt(View().TopRow).Text().TrimEnd();

        // A card an earlier part left in the corner (an error's stays until it's closed) sits over the bottom of the bar
        // and the minimap, and would take the presses there
        test.Run(() =>
        {
            foreach (UINotification card in toasts.Cards)
                card.Close();
            View().Feed(ScrollRows("dusk-scroll", 0, 400));
        });
        test.Settle(0.5f);
        test.Check("the scroll bar and the minimap are there with a scrollback, the view at the bottom", () =>
            View().Minimap && View().ScrollBarTrack.Width > 0.0f && View().ScrollBarThumb.Height < View().ScrollBarTrack.Height
            && View().MinimapBounds.Width > 0.0f && View().MinimapRowsDrawn > 0 && View().ScrolledUp == 0);
        test.Run(() => Shot("scroll_bottom"));

        // The thumb taken by its middle and dragged to the top of the track
        test.Drag(At(() => View().ScrollBarThumb), At(() => View().ScrollBarTrack, 0.5f, 0.995f));
        test.Wait();
        test.Check("the thumb dragged to the top of the track goes to the top of the scrollback", () =>
            View().TopRow == 0 && View().ScrolledUp == View().Screen.Scrollback.Count);
        test.Run(() => Shot("scroll_top"));

        // A press on the track under the thumb, and let go of before it pages again
        test.Click(At(() => View().ScrollBarTrack, 0.5f, 0.02f));
        test.Wait();
        test.Check("a press on the track under the thumb goes down a page", () => View().TopRow == View().Rows - 1);

        // Written to while it's read up there, it stays on the same text
        string reading = string.Empty;
        test.Run(() =>
        {
            reading = TopText();
            View().Feed(ScrollRows("dusk-more", 0, 60));
        });
        test.Wait();
        test.Check("more written while it's read up in the scrollback leaves it on what it shows", () =>
            reading.Length > 0 && TopText() == reading && View().ScrolledUp > 0);

        // The minimap pressed near its bottom, then its slider dragged to its top
        test.Click(At(() => View().MinimapBounds, 0.5f, 0.03f));
        test.Wait();
        test.Check("a press near the minimap's bottom goes a long way down", () => View().TopRow > View().Screen.Scrollback.Count / 2);
        test.Run(() => Shot("scroll_minimap"));
        test.Drag(At(() => View().MinimapBounds, 0.5f, 0.03f), At(() => View().MinimapBounds, 0.5f, 0.995f));
        test.Wait();
        test.Check("and the minimap dragged to its top goes back up", () => View().TopRow == 0);

        // Back at the bottom, it follows what's written
        test.Run(() =>
        {
            View().ScrollToEnd(top: false);
            View().Feed(ScrollRows("dusk-follow", 0, 40));
        });
        test.Wait();
        test.Check("at the bottom it follows what's written", () =>
            View().ScrolledUp == 0 && View().TopRow == View().Screen.Scrollback.Count && View().Screen.AllText().Contains("dusk-follow-039"));

        // A program with the whole screen has no scrollback to go through, no bar and a slider-less minimap of its screen
        test.Run(() => View().Feed("\u001b[?1049h\u001b[2J\u001b[Hdusk-alternate"));
        test.Wait();
        test.Check("the alternate screen has no bar, and the minimap draws only its screen", () =>
            View().ScrollBarTrack.Width == 0.0f && View().MinimapRowsDrawn == View().Rows);
        test.Click(At(() => View().MinimapBounds, 0.5f, 0.5f));
        test.Wait();
        test.Check("and a press on its minimap does nothing", () => View().ScrolledUp == 0 && View().Screen.AlternateScreen);
        test.Run(() => View().Feed("\u001b[?1049l"));
        test.Wait();
        test.Check("back on the main screen the bar is back", () => View().ScrollBarTrack.Width > 0.0f);

        // Switched off in the settings, the cells have its room
        int columns = 0;
        test.Run(() =>
        {
            columns = View().Columns;
            settings.Minimap = false;
            host.ApplySettings();
        });
        test.Wait();
        test.Check("terminal_minimap off takes the minimap away and the cells get its room", () =>
            !View().Minimap && View().MinimapBounds.Width == 0.0f && View().Columns > columns && View().ScrollBarTrack.Width > 0.0f);
        // Its rows written by hand are nothing the shell knows of, the next part gets a shell of its own
        test.Run(() =>
        {
            settings.Minimap = true;
            host.ApplySettings();
            foreach (TerminalTab tab in panel.Tabs.ToArray())
                panel.CloseTab(tab);
        });
        test.Settle(settle);
    }

    /// <summary>Rows written by hand, numbered, in the eight colours by turns with runs of different lengths after them, for the minimap to show.</summary>
    internal static string ScrollRows(string name, int from, int count)
    {
        var rows = new StringBuilder();
        for (int i = from; i < from + count; i++)
            rows.Append($"\u001b[3{i % 7 + 1}m{name}-{i:000}\u001b[0m {new string('=', i * 7 % 50)} \u001b[1;3{(i + 3) % 7 + 1}mend\u001b[0m\r\n");
        return rows.ToString();
    }
}
