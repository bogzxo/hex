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
            string padding = new string('x', 600);
            File.WriteAllText(file, string.Join('\n', Enumerable.Range(0, 1000).Select(i => $"Line {i} {padding}")));
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

        // Horizontal and diagonal middle-click autoscroll with word wrap disabled
        bool originalWordWrap = code.WordWrap;
        float beforeX = 0.0f;
        float midX = 0.0f;
        test.Run(() =>
        {
            originalWordWrap = code.WordWrap;
            code.WordWrap = false;
            code.ScrollXPixels = 0.0f;
            before = TopLine();
            beforeX = code.ScrollXPixels;
        });
        test.Settle(0.1f);
        test.MiddleClick(() => anchor);
        test.Hover(() => anchor + new Vector2(120, 0));
        test.Settle(0.3f);
        test.Check("middle scroll: moving right of the anchor scrolls horizontally without vertical drift", () =>
            code.ScrollXPixels > beforeX && TopLine() == before);
        test.Run(() =>
        {
            code.ScrollXPixels = 0.0f;
            midX = 0.0f;
            before = TopLine();
        });
        test.Hover(() => anchor + new Vector2(120, -120));
        test.Settle(0.3f);
        test.Check("middle scroll: diagonal displacement scrolls both horizontally and vertically", () =>
            code.ScrollXPixels > midX && TopLine() > before);
        test.MiddleClick(() => anchor);
        test.Run(() => code.WordWrap = originalWordWrap);

        // Slow, over a part of a page that passes it on to the scroll it holds (the settings' search over its rows, as
        // Agentic's margins pass it to the chat), the rows go by fractions of a pixel every frame. Turned into whole
        // notches of the wheel they stood still until a notch's worth had gathered, then jumped
        float rowsFrom = 0.0f;
        test.Run(() =>
        {
            OpenSettings();
            SettingsPage!.Find(string.Empty);
        });
        test.Settle(0.3f);
        test.Run(() =>
        {
            SettingsPage!.RowsScroll.Offset = 0.0f;
            rowsFrom = SettingsPage.RowsScroll.Offset;
            anchor = chrome.ToWorld(SettingsPage.Search.Bounds.Center);
        });
        test.MiddleClick(() => anchor);
        test.Hover(() => anchor - new Vector2(0, 14));
        test.Settle(0.25f);
        test.Check("middle scroll: slow over a margin moves the rows smoothly, less than a notch and not nothing", () =>
            SettingsPage!.RowsScroll.Offset - rowsFrom is > 0.5f and < 40.0f);
        test.MiddleClick(() => anchor);
        test.Run(CloseSettings);
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
