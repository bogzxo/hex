using System.Numerics;

namespace Hex.Dawn;

// The self test, the minimap down the right of the code. A click near its bottom goes far down a long file, a drag of
// its slider back to the top scrolls back up. Then at 100% and 111% the code is scrolled a bit of a line at a time and
// the space the code has made a pixel shorter every tick, and every row and the slider have to stay on whole pixels
// of the screen the whole time. A short file gets no slider. With HEX_SELFTEST_SHOTS set it leaves pictures of it too.
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
        MinimapShot(test, "minimap.png");
        test.Check("minimap: it's on and the code starts at the top", () => code.Minimap && TopLine() == 0);

        // How many rows the minimap shows decides how far down its bottom is. The code is shorter with the panel open
        // under it (the console's test leaves it open), a click there brought line 279 to the middle and the top was 259,
        // which a check for more than 300 lines took for the click going nowhere
        int shown = 0;
        test.Run(() => shown = code.MinimapRowTops.Count);
        test.Click(() => InMinimap(0.95f));
        test.Settle(0.3f);
        test.Check("minimap: a click near its bottom goes a long way down the file", () => shown > 100 && TopLine() > shown * 0.6f);

        // The slider is where the code in sight is, dragged back to the top it takes the code back there
        test.Drag(() => InMinimap(0.95f), () => InMinimap(0.01f));
        test.Settle(0.3f);
        test.Check("minimap: and its slider dragged to the top scrolls back up", () => TopLine() < 5);

        TestMinimapPixels(test, 1.0f);
        TestMinimapPixels(test, 1.11f);

        test.Run(() =>
        {
            if (document.Path is { } open && PlatformPaths.Same(open, file))
                CloseNow();
        });
        test.Wait();

        TestMinimapShortFile(test);
    }

    /// <summary>
    /// The long file scrolled down a bit of a line a tick at a UI scale, the code a pixel shorter each tick and its top
    /// a pixel down every third, and every tick the minimap's top, its rows and its slider on whole pixels and its scroll
    /// a whole number of rows.
    /// </summary>
    private void TestMinimapPixels(HexPointerScript test, float uiScale)
    {
        string at = $"{uiScale * 100.0f:0}%";
        float scaleWas = 1.0f, firstScroll = 0.0f;
        Horizon.UI.UIEdges paddingWas = default;
        bool whole = true, scrolled = false;
        var heights = new HashSet<float>();

        static bool Whole(float value) => float.IsFinite(value) && MathF.Abs(value - MathF.Round(value)) < 0.02f;

        // What's off a pixel this tick, empty for nothing
        string OffPixel()
        {
            int row = code.MinimapRowPixels;
            if (row < 1 || row != (int)MathF.Round(2.0f * compositor.UIScale))
                return $"a row is {row} pixels at {compositor.UIScale:0.###}";
            if (!Whole(code.MinimapTop))
                return $"its top is at {code.MinimapTop:0.###}";
            if (!Whole(code.MinimapSliderTop))
                return $"its slider is at {code.MinimapSliderTop:0.###}";
            if (!Whole(code.MinimapScrollPixels / row))
                return $"it's scrolled {code.MinimapScrollPixels:0.###} pixels, not a whole number of rows";

            var tops = code.MinimapRowTops;
            for (int i = 0; i < tops.Count; i++)
            {
                if (!Whole(tops[i]) || (i > 0 && MathF.Abs(tops[i - 1] - tops[i] - row) > 0.02f))
                    return $"row {i} is at {tops[i]:0.###}";
            }

            return tops.Count > 0 ? string.Empty : "it drew no rows";
        }

        test.Run(() =>
        {
            scaleWas = compositor.Scale;
            compositor.Scale = uiScale * compositor.Scale / compositor.UIScale;
            paddingWas = code.Padding;
            code.GoTo(0, 0);
        });
        test.Settle(0.4f);
        test.Hover(() => chrome.ToWorld(code.Bounds.Center));
        test.Run(() => firstScroll = code.MinimapScrollPixels);

        // A bit of a line down every tick, and the code a pixel shorter every tick and its top a pixel down every third,
        // the way dragging the window's edge goes. The window itself can't be sized from here, the space the code has can
        for (int tick = 0; tick < 30; tick++)
        {
            int step = tick;
            test.Run(() =>
            {
                float pixel = 1.0f / compositor.UIScale;
                compositor.Scroll(-0.11f);
                code.Padding = paddingWas with { Top = paddingWas.Top + step / 3 * pixel, Bottom = paddingWas.Bottom + (step + 1) * pixel };
            });
            test.Wait();
            test.Run(() =>
            {
                if (whole && OffPixel() is { Length: > 0 } off)
                {
                    whole = false;
                    Console.WriteLine($"[Hex self-test]   minimap at {at}, tick {step}: {off}");
                }

                scrolled |= code.MinimapScrollPixels != firstScroll;
                heights.Add(code.Bounds.Height - code.Padding.Total.Y);
            });
        }

        MinimapShot(test, $"minimap_{uiScale * 100.0f:0}.png");
        test.Check($"minimap: at {at} its top, every row and the slider stay on whole pixels as it scrolls and its height goes a pixel at a time", () =>
            Near(compositor.UIScale, uiScale, 0.001f) && heights.Count >= 30 && whole);
        test.Check($"minimap: at {at} its scroll goes whole rows, and it did scroll", () => scrolled && whole);

        test.Run(() =>
        {
            compositor.Scale = scaleWas;
            code.Padding = paddingWas;
        });
        test.Settle(0.3f);
    }

    /// <summary>A file that's all in sight has nothing to scroll, it gets its rows in the minimap and no slider over them.</summary>
    private void TestMinimapShortFile(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "minimap_short.cs");
        test.Run(() =>
        {
            File.WriteAllText(file, string.Join("\n", Enumerable.Range(1, 9).Select(line => $"int line{line} = {line};")));
            Open(file);
        });
        test.Settle(0.3f);
        test.Check("minimap: a 9 line file shows its 9 rows and no slider block", () =>
            code.Minimap && code.MinimapRowTops.Count == 9 && float.IsNaN(code.MinimapSliderTop));

        test.Run(() =>
        {
            if (document.Path is { } open && PlatformPaths.Same(open, file))
                CloseNow();
        });
        test.Wait();
    }

    // A picture of the screen into HEX_SELFTEST_SHOTS, when it's set
    private void MinimapShot(HexPointerScript test, string name)
    {
        test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, name));
        });
        test.Settle(0.3f);
    }
}
