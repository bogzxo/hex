using Horizon.UI.Components;
using Horizon.UI.Drawing;

namespace Hex.Dusk;

internal sealed partial class DuskScene
{
    private void TestSymbols(DuskScript test)
    {
        test.Run(() =>
        {
            panel.NewTab(new TerminalProfile("Symbols", string.Empty, "terminal", "symbols") { Kind = TerminalProfileKind.Embedded });
            var screen = panel.FocusedView!.Screen;
            screen.Feed("TUI symbols stay inside their cells\r\n");
            screen.Feed("|A▶B▸C▼D▲E◀F→G←H↑I↓J|\r\n");
            screen.Feed("\u001b[1m|A▶B▸C▼D▲E◀F→G←H↑I↓J|\u001b[0m bold\r\n");
            screen.Feed("\u001b[7m|A▶B▸C▼D▲E◀F→G←H↑I↓J|\u001b[0m inverse\r\n");
            screen.Feed("|A❯B⏵C➤D| prompt and action indicators\r\n");
        });
        test.Wait();
        test.Check("symbols: bundled atlas has all directional glyphs", () =>
            new[] { "triangle-right", "triangle-left", "triangle-up", "triangle-down", "arrow-right", "arrow-left", "arrow-up", "arrow-down" }
                .All(name => UISymbols.IsLoaded && UISymbols.TryGet(name, out _)));
        test.Run(() => Shot("terminal-direction-symbols"));
        test.Settle(0.3f);
        test.Run(() => panel.CloseTab(panel.Current!));
    }
}
