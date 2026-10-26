using System.Numerics;

namespace Hex.Dawn;

// The self test, inlay hints, the names of the parameters a call passes to written in among the code of a HIDL file.
internal sealed partial class DawnScene
{
    private void TestInlays(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "inlays.hor");
        const string CALL = "let total = add(1, 2);";
        test.Run(() =>
        {
            File.WriteAllText(file, $"func add(first, second) {{ return first + second; }}\n{CALL}\nlet first = 4;\nlet again = add(first, 5);\n");
            Open(file);
        });
        test.WaitFor(() => code.InlayHints.Count > 0, 5.0f);
        test.Settle(0.3f);
        test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, "inlays.png"));
        });

        int one = CALL.IndexOf('1'), two = CALL.IndexOf('2');
        test.Check("inlays: each argument of a call to a function of the file says the parameter it goes to", () =>
            code.InlayHints.Any(inlay => inlay.Line == 1 && inlay.Column == one && inlay.Text == "first:")
            && code.InlayHints.Any(inlay => inlay.Line == 1 && inlay.Column == two && inlay.Text == "second:"));
        test.Check("inlays: but not an argument that's already a name the same as its parameter", () =>
            !code.InlayHints.Any(inlay => inlay.Line == 3 && inlay.Text == "first:") && code.InlayHints.Any(inlay => inlay.Line == 3 && inlay.Text == "second:"));

        // The code after a hint is pushed along to make room, and a click lands on the character drawn there
        test.Check("inlays: the code after a hint is pushed along, and a click on it lands where it's drawn", () =>
        {
            float digit = code.PlaceOf(new UITextPosition(1, 1)).X - code.PlaceOf(new UITextPosition(1, 0)).X;
            Vector2 before = code.PlaceOf(new UITextPosition(1, two)), after = code.PlaceOf(new UITextPosition(1, two + 1));
            return after.X - before.X > digit * 4.0f && code.PositionAt(after - new Vector2(digit * 0.7f, 0.0f)) == new UITextPosition(1, two);
        });

        // Typing on the line holds its hints back until they're worked out again, and they come back
        test.Run(() =>
        {
            code.GoTo(1, CALL.Length);
            code.Document.Insert(" ");
        });
        test.Check("inlays: the line being typed on goes without its hints until they're worked out again", () =>
            code.PlaceOf(new UITextPosition(1, two + 1)).X - code.PlaceOf(new UITextPosition(1, two)).X < code.PlaceOf(new UITextPosition(1, 2)).X - code.PlaceOf(new UITextPosition(1, 0)).X);
        test.Settle(1.0f);
        test.Check("inlays: and they come back once the typing stops", () =>
            code.InlayHints.Any(inlay => inlay.Line == 1 && inlay.Column == two)
            && code.PlaceOf(new UITextPosition(1, two + 1)).X - code.PlaceOf(new UITextPosition(1, two)).X > code.PlaceOf(new UITextPosition(1, 4)).X - code.PlaceOf(new UITextPosition(1, 0)).X);

        test.Run(() =>
        {
            if (document.Path is { } open && PlatformPaths.Same(open, file))
                CloseNow();
        });
        test.Wait();
    }
}
