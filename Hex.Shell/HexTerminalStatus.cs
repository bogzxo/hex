using Horizon.UI.Components;

namespace Hex.Shell;

internal static class HexTerminalStatus
{
    /// <summary>A stable escape from scrollback in either mode. No row counting or text formatting per tick.</summary>
    public static StatusItem AddLive(StatusBar bar, TerminalPanel panel, Func<bool>? visible = null)
    {
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
