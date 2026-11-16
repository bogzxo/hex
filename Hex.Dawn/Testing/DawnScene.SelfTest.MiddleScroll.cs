using System.Numerics;
using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestMiddleScroll(HexPointerScript test)
    {
        Vector2 anchor = default;
        float originalSpeed = settings.MiddleScrollSpeed;
        int before = 0;
        int TopLine() => code.PositionAt(new Vector2(code.CodeBounds.Min.X + 12, code.CodeBounds.Max.Y - 12)).Line;
        test.Run(() =>
        {
            string file = Path.Combine(testFolder, "middle-scroll.txt");
            File.WriteAllText(file, string.Join('\n', Enumerable.Range(0, 1000).Select(i => $"Line {i}")));
            Open(file);
            OpenSettings();
            SettingsPage!.Find("middle-mouse scroll speed");
        });
        test.Settle(0.3f);
        test.Check("middle scroll: speed is searchable in settings", () => SettingsPage!.IsRowShown("middle_scroll_speed"));
        test.Run(() => SettingsPage!.Enter("middle_scroll_speed", new NumberValue(2)));
        test.Check("middle scroll: settings apply immediately without changing wheel speed", () =>
            settings.MiddleScrollSpeed == 2 && compositor.MiddleScrollSpeed == 2 && compositor.ScrollSpeed == settings.ScrollSpeed);
        test.Run(CloseSettings);
        test.Run(() => { anchor = chrome.ToWorld(code.CodeBounds.Center); before = TopLine(); });
        test.MiddleClick(() => anchor);
        test.Check("middle scroll: a short click enables autoscroll", () => code.AutoScrolling);
        test.Hover(() => anchor - new Vector2(0, 120));
        test.Settle(0.3f);
        test.Check("middle scroll: moving below the anchor scrolls down without moving the caret", () => TopLine() > before && code.Document.Caret.Line == 0);
        test.MiddleClick(() => anchor);
        test.Check("middle scroll: a second click cancels autoscroll", () => !code.AutoScrolling);
        test.Run(() => before = TopLine());
        test.MiddleDrag(() => anchor, () => anchor - new Vector2(0, 120));
        test.Check("middle scroll: holding moves the view and releasing stops it", () => TopLine() > before && !code.AutoScrolling);
        test.Run(() => before = TopLine());
        test.Hover(() => anchor - new Vector2(0, 180));
        test.Settle(0.2f);
        test.Check("middle scroll: movement after release no longer scrolls", () => TopLine() == before);
        test.Run(() =>
        {
            OpenSettings();
            SettingsPage!.Enter("middle_scroll_speed", new NumberValue(originalSpeed));
            CloseSettings();
            CloseNow();
        });
        test.Wait();
    }
}
