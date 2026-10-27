using System.Numerics;

namespace Hex.Dawn;

// The self test, reference counts over the declarations of a HIDL file (Visual Studio's code lens). Each on a row of its
// own over its line, two over a line that declares two, the caret going past their rows and the line numbers counting
// only lines, a press on one listing where it's used and a pick going there, and typing leaving them be until it stops.
internal sealed partial class DawnScene
{
    private void TestCodeLens(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "lens.hor");
        const string CODE = "const [low, high] = [4, 12];\nlet speed = low + high;\n\nfunc go(step) {\n    return step * speed + high;\n}\nlet moved = go(speed);\nlet again = go(moved) + speed;\n";
        UICodeLensAbove[] On(int line) => [.. code.LensesAbove.Where(lens => lens.Line == line)];
        Vector2 Count(int line, int index) => code.Module is { } module ? module.ToWorld(code.LensAboveBounds(line, index).Center) : Vector2.Zero;
        float lineHeight = 0.0f;

        test.Run(() =>
        {
            File.WriteAllText(file, CODE);
            Open(file);
            code.GoTo(2, 0);
        });
        test.WaitFor(() => code.LensesAbove.Count == 6 && code.LensAboveBounds(1, 0).Width > 0.0f, 5.0f);
        test.Settle(0.3f);
        test.Run(() => lineHeight = code.PlaceOf(new UITextPosition(3, 0)).Y - code.PlaceOf(new UITextPosition(2, 0)).Y);

        test.Check("lens: every declaration at the top of the file says how many places use it", () =>
            On(1) is [{ } speed] && speed.Text == "3 references" && On(3) is [{ } go] && go.Text == "2 references"
            && On(6) is [{ } moved] && moved.Text == "1 reference" && On(7) is [{ } again] && again.Text == "0 references");
        test.Check("lens: a line declaring two things has a count for each, side by side with a bar between", () =>
            On(0) is [{ } low, { } high] && low.Text == "1 reference" && high.Text == "2 references"
            && code.LensAboveBounds(0, 1).Min.X > code.LensAboveBounds(0, 0).Max.X + 4.0f);
        test.Check("lens: a count is over its line, small, and starts where the line's text does", () =>
        {
            UIRect over = code.LensAboveBounds(3, 0), above = code.LensAboveBounds(1, 0);
            Vector2 line = code.PlaceOf(new UITextPosition(3, 0)), before = code.PlaceOf(new UITextPosition(2, 0));
            return over.Center.Y > line.Y && over.Center.Y < before.Y && MathF.Abs(over.Min.X - line.X) < 2.0f
                && over.Height < MathF.Abs(lineHeight) && above.Min.X - code.PlaceOf(new UITextPosition(1, 0)).X < 2.0f;
        });
        test.Check("lens: a line with a count over it is two rows down from the line before, and a line without one only one", () =>
            MathF.Abs(code.PlaceOf(new UITextPosition(3, 0)).Y - code.PlaceOf(new UITextPosition(2, 0)).Y) > MathF.Abs(code.PlaceOf(new UITextPosition(5, 0)).Y - code.PlaceOf(new UITextPosition(4, 0)).Y) * 1.8f);
        test.Check("lens: the rows are the lines and a row over each line with counts, the line numbers count lines", () =>
            code.RowCount == code.Document.LineCount + 5 && code.LensRowCount == 5 && code.FirstLineNumber == 1);

        test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, "code_lens.png"));
        });

        // Down from the blank line lands on the declaration under its count, and up goes back over the count's row
        test.Run(() =>
        {
            code.GoTo(2, 0);
            code.Document.MoveVertically(1, false);
        });
        test.Check("lens: the caret goes past a count's row, down onto the line under it", () => code.Document.Caret.Line == 3);
        test.Run(() => code.Document.MoveVertically(-1, false));
        test.Check("lens: and up back to the line over it", () => code.Document.Caret.Line == 2);
        test.Check("lens: a click on a count's row away from the count is on its line, never on the row", () =>
        {
            UIRect over = code.LensAboveBounds(3, 0);
            return code.Module is not null && code.PositionAt(new Vector2(over.Max.X + 200.0f, over.Center.Y)).Line == 3;
        });

        // The pointer on a count underlines it, a press lists the places, a pick goes there
        test.Hover(() => Count(1, 0));
        test.Settle(0.3f);
        test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, "code_lens_hover.png"));
        });
        test.Click(() => Count(1, 0));
        test.WaitFor(() => contextMenu?.Showing is not null, 2.0f);
        test.Settle(0.3f);
        test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, "code_lens_menu.png"));
        });
        const string PICKED = "7   let moved = go(speed);";
        test.Check("lens: a press on a count lists every place it counts under the file's name, with its line number and line", () =>
            contextMenu?.Showing is { } menu && menu.Items.Count == 4 && menu.Items[0].Label == Said("code_lens.file", ("file", "lens.hor"), ("count", "3"))
            && menu.Items[0].IsEnabled?.Invoke() == false && menu.Items[1].Label == "5   return step * speed + high;" && menu.Items[2].Label == PICKED
            && menu.Items[3].Label == "8   let again = go(moved) + speed;");
        test.Click(() => contextMenu is { } shown ? chrome.ToWorld(shown.ItemBounds(PICKED).Center) : Vector2.Zero);
        test.Wait();
        test.Check("lens: picking a place goes there, the caret on the name", () =>
            contextMenu?.Showing is null && code.Document.Caret == new UITextPosition(6, 15));

        // Typing a line in above keeps the counts over their lines while it's typed, and they're counted again after
        test.Run(() =>
        {
            code.GoTo(2, 0);
            code.Document.Insert("let extra = speed;\n");
        });
        test.Check("lens: a line typed in above moves the counts down with their lines straight away", () =>
            On(4) is [{ } go] && go.Text == "2 references" && On(3).Length == 0);
        test.Settle(1.2f);
        test.Check("lens: and once the typing stops they're counted again, the new line's too", () =>
            On(2) is [{ Text: "0 references" }] && On(1) is [{ Text: "4 references" }]);

        // Turned off they go, and their rows with them
        test.Run(() =>
        {
            settings.CodeLensReferences = false;
            ForgetReferenceLenses();
        });
        test.Settle(0.3f);
        test.Check("lens: turned off, the counts and their rows are gone", () => code.LensesAbove.Count == 0 && code.RowCount == code.Document.LineCount);
        test.Run(() =>
        {
            settings.CodeLensReferences = true;
            if (document.Path is { } open && PlatformPaths.Same(open, file))
                CloseNow();
        });
        test.Wait();
    }
}
