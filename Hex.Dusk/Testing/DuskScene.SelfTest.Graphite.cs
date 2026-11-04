using Hex.Shell;
using Horizon.UI.Components;

namespace Hex.Dusk;

internal sealed partial class DuskScene
{
    private void TestGraphite(DuskScript test)
    {
        test.Run(() => PickLook(HexLooks.Graphite));
        test.Settle(0.4f);
        test.Check("Graphite applies the shared profile to the terminal and chrome", () =>
            settings.Theme == "graphite" && compositor.Skin?.Theme == "graphite"
            && panel.FocusedView?.Palette == panel.Palette);
        test.Run(() => Shot("graphite-terminal"));
        test.Settle(0.2f);
        test.Run(() => Run("palette"));
        test.Settle(0.3f);
        test.Check("Graphite command palette opens", () => chrome.Dialog is { IsOpen: true });
        test.Run(() => Shot("graphite-palette"));
        test.Settle(0.2f);
        test.Run(() => chrome.Dialog?.Cancel());
        test.Settle(0.3f);
        test.Run(() => Run("settings"));
        test.Settle(0.3f);
        test.Check("Graphite settings opens", () => SettingsPage is not null);
        test.Run(() => Shot("graphite-settings"));
        test.Settle(0.2f);
        test.Run(CloseSettings);
        test.Settle(0.3f);
    }
}
