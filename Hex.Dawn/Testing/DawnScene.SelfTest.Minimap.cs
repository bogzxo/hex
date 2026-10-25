using System.Numerics;

namespace Hex.Dawn;

// The self test, the minimap down the right of the code. A click near its bottom goes far down a long file, a drag of
// its slider back to the top scrolls back up. With HEX_SELFTEST_SHOTS set it leaves a picture of it as well.
internal sealed partial class DawnScene
{
    private void TestMinimap(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "minimap_long.cs");

        // The line at the top of the code as it's drawn
        int TopLine() => code.PositionAt(new Vector2(code.Bounds.Min.X + 80.0f, code.Bounds.Max.Y - 12.0f)).Line;

        // A place in the minimap, so far down it as a share of its height
        Vector2 InMinimap(float down) =>
            chrome.ToWorld(new Vector2(code.Bounds.Max.X - code.Padding.Right - 6.0f - 48.0f, code.Bounds.Max.Y - code.Padding.Top - (code.Bounds.Height - code.Padding.Total.Y) * down));

        test.Run(() =>
        {
            File.WriteAllText(file, BigCSharp(1200));
            settings.Minimap = true;
            Open(file);
            code.GoTo(0, 0);
        });
        test.Settle(0.5f);
        test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, "minimap.png"));
        });
        test.Settle(0.3f);
        test.Check("minimap: it's on and the code starts at the top", () => code.Minimap && TopLine() == 0);

        test.Click(() => InMinimap(0.95f));
        test.Settle(0.3f);
        test.Check("minimap: a click near its bottom goes a long way down the file", () => TopLine() > 300);

        // The slider is where the code in sight is, dragged back to the top it takes the code back there
        test.Drag(() => InMinimap(0.95f), () => InMinimap(0.01f));
        test.Settle(0.3f);
        test.Check("minimap: and its slider dragged to the top scrolls back up", () => TopLine() < 5);

        test.Run(() =>
        {
            if (document.Path is { } open && PlatformPaths.Same(open, file))
                CloseNow();
        });
        test.Wait();
    }
}
