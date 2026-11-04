using Hex.Shell;
using Horizon.UI.Components;

namespace Hex.Dusk;

internal sealed partial class DuskScene
{
    private void TestBell(DuskScript test)
    {
        test.Run(() => PickLook(HexLooks.Graphite));
        test.Settle(0.4f);
        test.Run(() => Shot("bell-before"));
        test.Run(() =>
        {
            panel.FocusedView!.Bell = TerminalBell.Flash;
            panel.FocusedView.Screen.Feed("\a");
        });
        test.Settle(0.02f);
        test.Run(() => Shot("bell-active"));
        test.Check("a bell does not switch the terminal to reverse video", () => !panel.FocusedView!.Screen.ReverseVideo);
        test.Settle(0.4f);
        test.Run(() => Shot("bell-after"));
        int end = 0;
        test.Run(() => panel.FocusedView!.Send("cursor-check"));
        test.Settle(0.3f);
        test.Run(() => end = panel.FocusedView!.Screen.CursorX);
        test.Run(() => panel.FocusedView!.Send("\u001b[D"));
        test.Settle(0.2f);
        test.Check("Left moves the shell cursor without reverse video", () =>
            panel.FocusedView!.Screen.CursorX == end - 1 && !panel.FocusedView.Screen.ReverseVideo);
        test.Run(() => Shot("cursor-left"));
        test.Run(() => panel.FocusedView!.Send("\u001b[C"));
        test.Settle(0.2f);
        test.Check("Right moves the shell cursor without reverse video", () =>
            panel.FocusedView!.Screen.CursorX == end && !panel.FocusedView.Screen.ReverseVideo);
        test.Run(() => Shot("cursor-right"));
        test.Run(() => panel.FocusedView!.Send("\u0003"));
    }
}
