using Hex.Shell;

namespace Hex.Dusk;

internal sealed partial class DuskScene
{
    // Solstice in Dusk: the terminal's own theme of it, the violet where Dawn has orange, and the panes flush
    private void TestSolstice(DuskScript test)
    {
        test.Run(() => PickLook(HexLooks.Solstice));
        test.Settle(0.4f);
        test.Check("Solstice puts Dusk in its own theme of it, the accent violet", () =>
            settings.Theme == "solstice_dusk" && compositor.Skin is { Theme: "solstice_dusk" } skin
            && skin.Palette.Accent.Z > 0.9f && skin.Palette.Accent.X < 0.8f && panel.FocusedView?.Palette == panel.Palette);
        test.Check("Solstice has the panes flush and the controls round", () =>
            compositor.Skin is { } skin && HexLooks.Flush(skin) && skin.CornerRadius > 0.0f && panel.PaneRadius == 0.0f);
        test.Run(() => Shot("solstice-terminal"));
        test.Settle(0.2f);
        test.Run(() => PickLook(HexLooks.SolsticeDay));
        test.Settle(0.4f);
        test.Check("Solstice Day is the paper in Dusk, violet as well", () => compositor.Skin is { Theme: "solstice_day_dusk" } skin && skin.Palette.LightField);
        test.Run(() => Shot("solstice-day-terminal"));
        test.Settle(0.2f);
        test.Run(() => PickLook(HexLooks.Graphite));
        test.Settle(0.4f);
    }
}
