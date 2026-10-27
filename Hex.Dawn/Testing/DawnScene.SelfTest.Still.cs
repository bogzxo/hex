using System.Numerics;

namespace Hex.Dawn;

// The self test of the code staying still while it's typed into. The localisation file, letters typed at the end of a line
// until the line goes red, selected and taken out again, and every tick of the way the lines in sight, the code's own box
// and the toolbar over it have to be where they were. The Code and Table buttons going away with the table and coming back
// a tick before the tabs made room for them, the panel opening for the mistake, a row of counts going, the hints of a line
// going and coming back, each had the code jump about under the caret.
internal sealed partial class DawnScene
{
    private void TestStill(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "still", "Assets", "hex", "localisation");
        string file = Path.Combine(folder, "en-gb.hor");
        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "hex", "localisation", "en-gb.hor"), file, overwrite: true);
            Open(file);
            code.Focus();
            code.GoTo(14, code.Document.LineAt(14).Length);
        });
        test.Settle(2.5f);

        // Where the lines in sight are, the code's box and the toolbar, and the most any of them went from there
        UITextPosition[] watched = [new(8, 0), new(14, 0), new(15, 0), new(30, 0)];
        var was = new Vector2[watched.Length];
        UIRect box = default, bar = default;
        float moved = 0.0f;
        string worst = string.Empty;
        test.Run(() =>
        {
            for (int i = 0; i < watched.Length; i++)
                was[i] = code.PlaceOf(watched[i]);
            (box, bar) = (codeScroll.Bounds, toolbar.Bounds);
        });

        void Went(float by, string what)
        {
            if (by > moved)
                (moved, worst) = (by, $"{what} went {by:0.0}");
        }

        bool Watch(bool done)
        {
            for (int i = 0; i < watched.Length; i++)
                Went(Vector2.Distance(code.PlaceOf(watched[i]), was[i]), $"line {watched[i].Line + 1}");
            Went(Vector2.Distance(codeScroll.Bounds.Min, box.Min) + Vector2.Distance(codeScroll.Bounds.Max, box.Max), "the code's box");
            Went(Vector2.Distance(toolbar.Bounds.Min, bar.Min) + Vector2.Distance(toolbar.Bounds.Max, bar.Max), "the toolbar");
            return done;
        }

        bool Red() => code.SyntaxError is not null || code.Diagnostics.Any(found => !found.Warning && !found.Hint);
        test.Run(() =>
        {
            foreach (char typed in "SDFSDF")
                code.Document.Type(typed);
        });
        test.WaitFor(() => Watch(Red()), 6.0f);
        test.Check("still: letters typed at the end of a line of the localisation file are a mistake, it goes red", Red);
        test.WaitFor(() => Watch(false), 1.5f);
        PressKeys(test, "Escape");
        PressKeys(test, "Shift+Left Shift+Left Shift+Left Shift+Left Shift+Left Shift+Left");
        test.WaitFor(() => Watch(false), 1.0f);
        PressKeys(test, "Backspace");
        test.WaitFor(() => Watch(false), 3.0f);
        test.Run(() => Console.WriteLine($"[Hex self-test] still: the most anything moved was {moved:0.0} {worst}"));
        test.Check("still: typed, gone red, selected and taken out, nothing on screen moved", () => moved < 1.0f && code.Document.LineAt(14).EndsWith("\","));

        test.Run(() =>
        {
            if (document.Path is { } open && PlatformPaths.Same(open, file))
                CloseNow();
        });
        test.Wait();
    }
}
