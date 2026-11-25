using System.Numerics;
using Horizon.Input;

namespace Hex.Dawn;

// The self test, a trackpad's gestures reaching the code: its pixel scrolling (the wheel event a Mac's trackpad makes, flagged
// precise), the pinch (zoom by the change of size) and the smart zoom (the usual size and a bigger one). They are posted as the
// platform's input events, which is what the Mac's view makes of the real ones, and go through the compositor to the editor
// under the pointer, so the engine's routing is what's tested; what the hardware says is only tried by a person.
internal sealed partial class DawnScene
{
    private void TestGestures(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "gestures_long.txt");
        float before = 0.0f;

        test.Run(() =>
        {
            File.WriteAllText(file, string.Join('\n', Enumerable.Range(1, 600).Select(i => $"line {i} of a long file")));
            Open(file);
            ZoomCode(0.0f);
        });
        test.Settle(0.4f);
        test.Hover(() => code.Module is { } module ? module.ToWorld(code.Bounds.Center) : Vector2.Zero);
        test.Settle(0.3f);

        // Precise scrolling: 10 points of finger are 10 points of code, whatever the interface size and screen scale
        test.Run(() =>
        {
            before = code.ScrollPixels;
            Engine.Input.Post(new InputEvent { Kind = InputEventKind.Scroll, ScrollY = -1.0f, ScrollPrecise = true, Position = Vector2.Zero });
        });
        test.Settle(0.3f);
        test.Check("gestures: a precise scroll moves the code by the finger's distance", () =>
        {
            float unitsPerPoint = compositor.UIScale > 0.0f ? compositor.DisplayScale / compositor.UIScale : 1.0f;
            float expected = PreciseScroll.ToNotchesAt(1.0f, unitsPerPoint) * 48.0f;
            return code.ScrollPixels > before && MathF.Abs(code.ScrollPixels - before - expected) < 0.5f;
        });

        // The pinch: five per cent bigger, three times in one frame (the changes of a frame add up), smoothly (a size between the
        // steps of the keys), then a little back
        test.Run(() =>
        {
            for (int i = 0; i < 3; i++)
                Engine.Input.Post(new InputEvent { Kind = InputEventKind.Magnify, Magnification = 0.05f });
        });
        test.Settle(0.3f);
        test.Check("gestures: a pinch out zooms the code smoothly", () => MathF.Abs(settings.CodeZoom - 1.15f) < 0.005f);
        test.Run(() => Engine.Input.Post(new InputEvent { Kind = InputEventKind.Magnify, Magnification = -0.01f }));
        test.Settle(0.3f);
        test.Check("gestures: a small pinch in is not rounded away", () => settings.CodeZoom < 1.15f && settings.CodeZoom > 1.13f);

        test.Run(() => ZoomCode(0.0f));
        test.Run(() => Engine.Input.Post(new InputEvent { Kind = InputEventKind.Magnify, Magnification = 0.004f }));
        test.Settle(0.3f);
        test.Check("gestures: a pinch of a fraction of a per cent leaves the usual size (no snap back)", () => settings.CodeZoom > 1.0f);

        // Smart zoom goes to a bigger size and back (from a size within two per cent of the usual one it is already "usual")
        test.Run(() => ZoomCode(1.3f, smooth: true));
        test.Run(() => Engine.Input.Post(new InputEvent { Kind = InputEventKind.SmartMagnify }));
        test.Settle(0.3f);
        test.Check("gestures: smart zoom from a pinched size goes to the usual size", () => settings.CodeZoom == 1.0f);
        test.Run(() => Engine.Input.Post(new InputEvent { Kind = InputEventKind.SmartMagnify }));
        test.Settle(0.3f);
        test.Check("gestures: smart zoom from the usual size goes to a bigger one", () => settings.CodeZoom > 1.5f);
        test.Run(() => Engine.Input.Post(new InputEvent { Kind = InputEventKind.SmartMagnify }));
        test.Settle(0.3f);
        test.Check("gestures: and back again", () => settings.CodeZoom == 1.0f);

        // Ctrl and the wheel: a notch is a step, a trackpad's fraction of one is that fraction of a step
        test.Run(() =>
        {
            code.OnZoom!(1.0f);
            code.OnZoom!(0.1f);
        });
        test.Settle(0.3f);
        test.Check("gestures: the zoom of a wheel notch is a step and of a tenth of a notch a tenth of one", () =>
            MathF.Abs(settings.CodeZoom - CODE_ZOOM_STEP * MathF.Pow(CODE_ZOOM_STEP, 0.1f)) < 0.002f);

        test.Run(() =>
        {
            ZoomCode(0.0f);
            if (document.Path is { } open && PlatformPaths.Same(open, file))
                CloseNow();
        });
        test.Wait();
    }
}
