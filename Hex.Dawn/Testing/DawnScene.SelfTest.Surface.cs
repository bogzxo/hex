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

        float findScaleWas = 0, panelHeightWas = 0;
        bool panelShownWas = false;
        int panelPageWas = 0;
        var originalFindBox = codeEditing.findBox;
        var originalReplaceBox = codeEditing.replaceBox;
        test.Run(() =>
        {
            findScaleWas = compositor.Scale;
            panelHeightWas = panelWanted;
            panelShownWas = bottom.Shown;
            panelPageWas = bottom.Selected;
            ShowPanel(OUTPUT_PAGE);
            panelWanted = 400;
            SetScale(2.0f, remember: false);
        });
        test.Settle(0.5f);
        PressKeys(test, "Ctrl+H");
        test.Settle(0.3f);
        test.Check("surface: compact find keeps dismissal with the query, arrows paired and replacement actions beside the field", () =>
            Near(codeEditing.findBox.Bounds.Center.Y, codeEditing.findClose.Bounds.Center.Y, 1)
            && Near(codeEditing.findPrevious.Bounds.Center.Y, codeEditing.findNext.Bounds.Center.Y, 1)
            && Near(codeEditing.replaceBox.Bounds.Center.Y, codeEditing.replaceOne.Bounds.Center.Y, 1)
            && Near(codeEditing.replaceOne.Bounds.Center.Y, codeEditing.replaceAll.Bounds.Center.Y, 1));
        test.Check("surface: compact find reserves space above the editing context", () =>
            code.CodeBounds.Max.Y <= codeEditing.findBar.Bounds.Min.Y - 4);
        test.Check("surface: a compact find with room for its options uses three tidy lines", () =>
            codeEditing.findBar.Bounds.Width < 222 || codeEditing.findBar.Bounds.Width >= 616
            || (Near(codeEditing.findCase.Bounds.Center.Y, codeEditing.findNext.Bounds.Center.Y, 1)
                && Near(codeEditing.findCount.Bounds.Center.Y, codeEditing.findNext.Bounds.Center.Y, 1)
                && codeEditing.findBar.Bounds.Height <= 98.5f));
        test.Check("surface: at 200% Ctrl+H keeps every find and replace control inside the editor and clear of the minimap", () =>
        {
            var bar = codeEditing.findBar.Bounds;
            Horizon.UI.Components.UIComponent[] controls = [codeEditing.findExpand, codeEditing.findBox, codeEditing.findCase,
                codeEditing.findWord, codeEditing.findRegex, codeEditing.findCount, codeEditing.findPrevious,
                codeEditing.findNext, codeEditing.findClose, codeEditing.replaceBox, codeEditing.replaceOne, codeEditing.replaceAll];
            return codeEditing.replaceRow.Visible && bar.Min.X >= codeScroll.Bounds.Min.X
                && bar.Max.X <= code.CodeBounds.Max.X - 4 && bar.Min.Y >= codeScroll.Bounds.Min.Y
                && controls.All(control => control.Visible && control.Bounds.Min.X >= bar.Min.X
                    && control.Bounds.Max.X <= bar.Max.X && control.Bounds.Min.Y >= bar.Min.Y && control.Bounds.Max.Y <= bar.Max.Y);
        });
        test.Click(() => chrome.ToWorld(codeEditing.replaceBox.Bounds.Center));
        test.Run(() => TypeInto(codeEditing.replaceBox, "sum"));
        PressKeys(test, "Tab");
        test.Check("surface: wrapped replacement still tabs to the case toggle", () => compositor.Focus == codeEditing.findCase);
        test.Click(() => chrome.ToWorld(codeEditing.findRegex.Bounds.Center));
        test.Check("surface: the wrapped regex toggle responds to a pointer", () => codeEditing.findOptions.Regex);
        test.Click(() => chrome.ToWorld(codeEditing.findNext.Bounds.Center));
        test.Check("surface: wrapped navigation still selects the query", () => code.Document.SelectedText == "total");
        test.Click(() => chrome.ToWorld(codeEditing.replaceOne.Bounds.Center));
        test.Check("surface: the wrapped replace action edits the document", () => code.Text.Contains("sum +=", StringComparison.Ordinal));
        test.Run(() => code.Document.Undo());
        test.Run(() => codeEditing.replaceBox.Focus());
        test.Settle(0.3f);
        test.Check("surface: the active match stays below pinned scopes at 200%", () =>
        {
            var caret = code.Document.Caret;
            float y = code.PlaceOf(caret).Y;
            float row = MathF.Abs(y - code.PlaceOf(new(caret.Line + 1, 0)).Y);
            Console.WriteLine($"[Hex self-test] find visibility: caret={caret}, y={y}, row={row}, bounds={code.CodeBounds}, sticky={code.StickyShown.Count}");
            return y - row * 0.5f >= code.CodeBounds.Min.Y
                && y + row * 0.5f <= code.CodeBounds.Max.Y - (code.StickyShown.Count + 1) * row;
        });
        SurfaceShot(test, "surface_find_200.png");
        test.Check("surface: compact find and the bottom panel leave usable editor space", () =>
            code.CodeBounds.Height >= 7 * MathF.Max(16, code.CaretLineHeight) - 1 &&
            codeScroll.Bounds.Min.Y >= bottom.Bounds.Max.Y - 1);
        test.Run(() =>
        {
            panelWanted = panelHeightWas;
            bottom.Select(panelPageWas);
            bottom.Show(panelShownWas, instantly: true);
            SetScale(findScaleWas, remember: false);
        });
        test.Settle(0.5f);
        test.Check("surface: returning to the wide layout keeps the same focused find fields", () =>
            ReferenceEquals(originalFindBox, codeEditing.findBox) && ReferenceEquals(originalReplaceBox, codeEditing.replaceBox)
            && compositor.Focus == originalReplaceBox && codeEditing.replaceBox.Text == "sum");
        test.Click(() => chrome.ToWorld(codeEditing.findClose.Bounds.Center));
        test.Check("surface: the reflowed close action returns focus to code", () => !codeEditing.FindBarOpen && compositor.Focus == code);
        test.Run(() => codeEditing.findOptions = default);
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
