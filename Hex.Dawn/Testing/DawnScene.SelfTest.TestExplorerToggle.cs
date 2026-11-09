namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestTestExplorerToggle(HexPointerScript test)
    {
        test.Run(() => { Side = HexSide.Explorer; left.Show(true); });
        test.Click(() => chrome.ToWorld(layout.Get<Horizon.UI.Components.Button>("act_tests").Bounds.Center));
        test.Check("test explorer: activity button opens the view", () => Side == HexSide.Tests && left.Shown);
        test.Click(() => chrome.ToWorld(layout.Get<Horizon.UI.Components.Button>("act_tests").Bounds.Center));
        test.Check("test explorer: second click closes the view", () => !left.Shown);
        test.Click(() => chrome.ToWorld(layout.Get<Horizon.UI.Components.Button>("act_tests").Bounds.Center));
        test.Check("test explorer: third click reopens the view", () => Side == HexSide.Tests && left.Shown);
        test.Run(testExplorer.Show);
        test.Check("test explorer: Show command keeps an open view open", () => Side == HexSide.Tests && left.Shown);
    }
}
