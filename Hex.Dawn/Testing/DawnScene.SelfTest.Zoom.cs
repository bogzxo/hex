namespace Hex.Dawn;

// The self test, zooming the code in and out with the caret halfway down a long file. The caret's line stays where it was
// on the screen. It slid away before, the scroll being in pixels of the old size.
internal sealed partial class DawnScene
{
    private void TestZoomKeepsPlace(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "zoom_long.txt");
        float before = 0.0f;

        test.Run(() =>
        {
            File.WriteAllText(file, string.Join('\n', Enumerable.Range(1, 600).Select(i => $"line {i} of a long file")));
            Open(file);
            code.CaretGlide = 0.0f;
            code.GoTo(300, 4);
        });
        test.Settle(0.4f);
        test.Run(() => before = code.CaretPoint.Y);

        test.Run(() =>
        {
            ZoomCode(CODE_ZOOM_STEP);
            ZoomCode(CODE_ZOOM_STEP);
            ZoomCode(CODE_ZOOM_STEP);
        });
        test.Settle(0.4f);
        test.Check("zoom: in, the caret's line stays where it was on the screen", () =>
            settings.CodeZoom > 1.2f && code.Document.Caret.Line == 300 && MathF.Abs(code.CaretPoint.Y - before) < 4.0f);

        test.Run(() => ZoomCode(1.0f / CODE_ZOOM_STEP / CODE_ZOOM_STEP / CODE_ZOOM_STEP / CODE_ZOOM_STEP / CODE_ZOOM_STEP));
        test.Settle(0.4f);
        test.Check("zoom: and out past where it started, still there", () =>
            settings.CodeZoom < 1.0f && MathF.Abs(code.CaretPoint.Y - before) < 4.0f);

        test.Run(() =>
        {
            ZoomCode(0.0f);
            if (document.Path is { } open && PlatformPaths.Same(open, file))
                CloseNow();
        });
        test.Wait();
    }
}
