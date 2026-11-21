using Horizon.UI.Components;

namespace Hex.Shell;

internal static class HexTerminalStatus
{
    /// <summary>
    /// The words the engine's terminals say themselves in Hex's, an ended pane's bar and the find bar, for Dusk's panel and
    /// the terminal page's alike. Read once as the panel is made, the bar's words for a code are made as a pane ends.
    /// </summary>
    public static void Words(TerminalPanel panel)
    {
        string exited = HexText.Get("terminal_page.exited");
        panel.EndedWords = code => code == 0 ? exited : HexText.Get("terminal_page.exited_code", ("code", code.ToString()));
        panel.RestartHint = HexText.Get("terminal_page.restart_hint");
        panel.FindWords = new TerminalFindWords
        {
            Placeholder = HexText.Get("terminal_page.find_placeholder"),
            Previous = HexText.Get("terminal_page.find_previous"),
            Next = HexText.Get("terminal_page.find_next"),
            Close = HexText.Get("terminal_page.find_close"),
            Searching = HexText.Get("terminal_page.find_searching"),
            Many = HexText.Get("terminal_page.find_many"),
            None = HexText.Get("terminal_page.find_none"),
            Of = (at, count) => HexText.Get("terminal_page.find_of", ("at", at.ToString()), ("count", count.ToString()))
        };
    }

    /// <summary>A stable escape from scrollback in either mode. No row counting or text formatting per tick.</summary>
    public static StatusItem AddLive(StatusBar bar, TerminalPanel panel, Func<bool>? visible = null)
    {
        Words(panel);
        StatusItem item = bar.Add(HexText.Get("terminal_page.return_live"), right: true, pressed: () =>
        {
            if (panel.FocusedView is { } view)
            {
                view.ScrollToEnd(top: false);
                view.Focus();
            }
        }, tooltip: HexText.Get("terminal_page.return_live_tip"));
        item.IsVisible = () => (visible?.Invoke() ?? true) && panel.FocusedView is { ScrolledUp: > 0, Screen.AlternateScreen: false };
        return item;
    }
}
