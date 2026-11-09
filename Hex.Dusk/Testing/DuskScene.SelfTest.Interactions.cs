using System.Numerics;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Key = Horizon.Input.Key;

namespace Hex.Dusk;

internal sealed partial class DuskScene
{
    private void TestInteractions(DuskScript test)
    {
        UIDialog shown = null!;
        TextBox query = null!;
        int picked = 0, behindClicks = 0;
        Button behind = null!;
        Vector2 Outside() => chrome.ToWorld(behind.Bounds.Center);
        void Remember() { shown = chrome.Dialog!; query = Descendants(shown).OfType<TextBox>().First(); }
        test.Run(() =>
        {
            behind = chrome.AddComponent(new Button("Behind palette") {
                Anchor = Origin.BottomLeft, Pivot = Origin.BottomLeft, Position = new Vector2(70, 50),
                Size = new Vector2(140, 32), OnPressed = () => behindClicks++ });
            panel.FocusedView!.Focus(); Run("palette"); Remember();
        });
        test.Settle(0.3f);
        test.Run(() => Shot("interaction-command-palette"));
        test.Click(Outside);
        test.Check("interaction: outside click closes palette, restores terminal focus and does not click through", () =>
            !shown.IsOpen && compositor.Focus == panel.FocusedView && behindClicks == 0);
        test.Click(Outside);
        test.Check("interaction: the next workspace click works", () => behindClicks == 1);
        test.Run(() => { panel.FocusedView!.Focus(); Run("palette"); Remember(); query.Text = "zzzz-no-match-987654"; query.OnChanged?.Invoke(query.Text); });
        test.Settle(0.3f);
        test.Run(() => Engine.Input.Keyboard.Tap([Key.Enter]));
        test.Wait();
        test.Check("interaction: no-match Enter preserves the palette", () => shown.IsOpen && compositor.Focus == query);
        test.Click(() => chrome.ToWorld(Descendants(shown).OfType<Label>().First(label => label.Visible && label.Text.StartsWith("Nothing matches")).Bounds.Center));
        test.Check("interaction: empty-result click preserves typing focus", () => shown.IsOpen && compositor.Focus == query);
        test.Run(() => Engine.Input.Keyboard.Tap([Key.Escape]));
        test.Wait();
        test.Check("interaction: Escape returns to the terminal", () => !shown.IsOpen && compositor.Focus == panel.FocusedView);
        test.Run(() => { Run("palette"); Remember(); });
        test.Settle(0.3f);
        test.RightClick(Outside);
        test.Check("interaction: outside right click cancels without a workspace menu", () => !shown.IsOpen && chrome.Popup is null && compositor.Focus == panel.FocusedView);
        test.Run(() =>
        {
            ChooseFrom("Interaction picker", "Click a result or press Enter. Escape or click outside to cancel.",
                new List<(string Text, Action Picked)> { ("First", () => picked = 1), ("Second", () => picked = 2) });
            Remember();
        });
        test.Settle(0.3f);
        test.Run(() => Shot("interaction-picker"));
        test.Click(() => { var list = Descendants(shown).OfType<ListBox>().First(); return chrome.ToWorld(new Vector2(list.Bounds.Center.X, list.Bounds.Max.Y - list.RowHeightSeen * 1.5f)); });
        test.Check("interaction: one click runs an unselected result and restores terminal focus", () => picked == 2 && !shown.IsOpen && compositor.Focus == panel.FocusedView);
        test.Run(() => chrome.Root.Remove(behind));
    }
}
