using System.Numerics;
using System.Text;

using Horizon.UI.Drawing;

namespace Hex.Dawn;

// The self test, how the code itself is drawn. A C# file written the Allman way scrolled into the middle of a method has
// the class, the method and the loop held at the top by what they are and not by their lone {s. Something wrong with no
// length is underlined under the whole of what it's on, the bar is as thick as every other bar, the find bar keeps left
// of the minimap, and a diff has the old version's numbers and the new one's in two columns. With HEX_SELFTEST_SHOTS set
// it leaves pictures of the lot, the squiggles, the gutter's columns, the hints and the sticky lines.
internal sealed partial class DawnScene
{
    // The lines of the file the test writes, where things are on it
    private const int SURFACE_CLASS = 3, SURFACE_METHOD = 7, SURFACE_LOOP = 9, SURFACE_LINES = 200;

    private void TestSurface(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "surface.cs");
        int middle = SURFACE_LOOP + 2 + SURFACE_LINES / 2;
        string written = SurfaceCSharp();

        test.Run(() =>
        {
            File.WriteAllText(file, written);
            settings.Minimap = true;
            Open(file);
            code.GoTo(middle, 0);
        });
        test.Settle(0.6f);
        test.Check("surface: the sticky lines are the class, the method and the loop, not their lone {s", () =>
            code.StickyShown.SequenceEqual([SURFACE_CLASS, SURFACE_METHOD, SURFACE_LOOP])
            && code.StickyShown.All(line => code.Document.LineAt(line).Trim() != "{"));

        // Something wrong given with no length on the quote of a string that's never closed, a warning on a word, a hint,
        // something obsolete, a breakpoint, what changed, a fold and the hints, all in sight
        int quoteLine = middle + 2, warned = middle + 3, hinted = middle + 4, old = middle + 5, called = middle + 6;
        int quote = 0;
        test.Run(() =>
        {
            string text = code.Document.LineAt(quoteLine);
            quote = text.IndexOf('"');
            code.Diagnostics =
            [
                new(quoteLine, quote, 0, "never closed"),
                new(warned, 12, 5, "a warning", Warning: true),
                new(hinted, 12, 5, "a hint", Warning: true, Hint: true),
                new(old, 12, 3, "obsolete", Warning: true, Deprecated: true),
            ];
            code.Breakpoints = new HashSet<int> { middle - 2 };
            code.LineChanges = [new(middle - 4, 1, UILineChangeKind.Modified), new(middle - 3, 2, UILineChangeKind.Added), new(middle + 1, 0, UILineChangeKind.Deleted)];
            code.InlayHints = [new(called, 24, "a:", PadRight: true), new(called, 27, "b:", PadRight: true)];
            code.InlineHints = [new(called, "= 3")];
        });
        test.Settle(0.3f);
        SurfaceShot(test, "surface.png");
        test.Check("surface: a mistake with no length is underlined under the whole of the string it's on", () =>
            code.UnderlinedColumns(new(quoteLine, quote, 0, "never closed")) is var (from, to) && from == quote
            && to == code.Document.LineAt(quoteLine).Length);
        test.Check("surface: and one on a word under the word", () =>
            code.UnderlinedColumns(new(warned, 14, 0, "in a word")) == (12, 17));
        test.Check("surface: the bar's thumb is as thick as every other bar's", () =>
            MathF.Abs(code.ScrollBarThumb.Width - UIScrollBar.WIDTH) < 0.01f && code.ScrollBarThumb.Max.X <= code.Bounds.Max.X);

        // The find bar over the code, left of the minimap and the bar
        test.Run(() =>
        {
            codeEditing.FindInFile();
            TypeInto(codeEditing.findBox, "total");
        });
        test.Settle(0.3f);
        SurfaceShot(test, "surface_find.png");
        test.Check("surface: the find bar keeps left of the minimap and the scroll bar", () =>
            codeEditing.FindBarOpen && codeEditing.findBar.Bounds.Max.X <= code.CodeBounds.Max.X - 4.0f && code.FoundCount > 100);
        test.Run(codeEditing.CloseFindBar);
        test.Wait();

        // A line changed and one taken out, compared with what's saved, the diff numbered in two columns
        test.Run(() =>
        {
            code.Diagnostics = [];
            code.InlayHints = [];
            code.InlineHints = [];
            code.Text = written.Replace("total += 3;", "total += 33;", StringComparison.Ordinal).Replace("            total += 4;\n", "", StringComparison.Ordinal);
            CompareWithSaved();
        });
        test.WaitFor(() => document.DiffOf is not null, 4.0f);
        test.Settle(0.6f);
        SurfaceShot(test, "surface_diff.png");
        test.Settle(0.5f);
        test.Check("surface: a diff has the old numbers and the new ones, a line taken out only in the old column", () =>
            document.DiffOf is not null && code.DiffLineNumbers is var (olds, news) && olds.Length == news.Length
            && olds.Contains(0) && news.Contains(0) && news.Max() == olds.Max() - 1);

        test.Run(() =>
        {
            if (document.DiffOf is not null)
                CloseNow();
            if (document.Path is { } open && PlatformPaths.Same(open, file))
            {
                code.Text = written;
                CloseNow();
            }
        });
        test.Wait();
    }

    // A class with an attribute, a method under its doc comment and a loop in it, every brace on a line of its own
    private static string SurfaceCSharp()
    {
        var text = new StringBuilder("namespace Look;\n\n[Serializable]\npublic sealed class Big\n{\n    /// <summary>Counts.</summary>\n    [Obsolete]\n");
        text.Append("    public int Method(int a, string name)\n    {\n        for (int i = 0; i < a; i++)\n        {\n");
        for (int i = 0; i < SURFACE_LINES; i++)
        {
            text.Append(i switch
            {
                SURFACE_LINES / 2 + 2 => "            string text = \"never closed;\n",
                SURFACE_LINES / 2 + 6 => "            total = Add(1, 2);\n",
                SURFACE_LINES / 2 + 5 => "            Old();\n",
                _ => $"            total += {i};\n"
            });
        }

        text.Append("        }\n\n        return 0;\n    }\n}\n");
        return text.ToString();
    }

    private void SurfaceShot(HexPointerScript test, string name)
    {
        test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, name));
        });
        test.Settle(0.3f);
    }
}
