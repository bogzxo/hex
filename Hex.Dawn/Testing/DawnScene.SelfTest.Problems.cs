using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestProblems(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "problem-search.txt");
        string[] Captions() => Descendants(problems).OfType<Label>().Select(label => label.Text).ToArray();
        test.Run(() =>
        {
            File.WriteAllText(file, "one\ntwo\nthree\n");
            Open(file);
            code.Diagnostics =
            [
                new(0, 0, 1, "Missing value (E100)"),
                new(1, 0, 1, "Unused value (W200)", Warning: true),
                new(2, 0, 1, "Simplify expression", Warning: true, Hint: true)
            ];
            problemsDirty = true;
            panelOpenedByHand = true;
            settings.PanelCollapsed = false;
            ShowPanel(PROBLEMS_PAGE);
        });
        test.Settle(0.4f);
        test.Check("problems: severity groups have distinct colours and counts", () =>
            Captions().Any(text => text.Contains("[color:error]$(error)  Errors  (1)"))
            && Captions().Any(text => text.Contains("[color:warning]$(warning)  Warnings  (1)"))
            && Captions().Any(text => text.Contains("[color:accent]$(info)  Hints  (1)")));
        test.Check("problems: search stays above a bounded scrolling list", () =>
            problemsFilter.Bounds.Min.Y >= problemsScroll.Bounds.Max.Y
            && problemsScroll.Bounds.Min.Y >= bottom.Bounds.Min.Y - 1
            && problemsFilter.Bounds.Width > 100);
        test.Run(() => TypeInto(problemsFilter, "w200"));
        test.Settle(0.2f);
        test.Check("problems: typing searches codes and keeps total counts", () =>
            Captions().Any(text => text.Contains("Unused value"))
            && !Captions().Any(text => text.Contains("Missing value") || text.Contains("Simplify expression"))
            && errorCount == 1 && warningCount == 1);
        test.Run(() => TypeInto(problemsFilter, "no such problem"));
        test.Settle(0.2f);
        test.Check("problems: unmatched search explains the empty list and stays open", () =>
            Captions().Contains(Said("panel_tabs.no_matches")) && !bottom.Folded);
        test.Run(() => layout.Get<Button>("problems_clear").OnPressed!());
        test.Settle(0.2f);
        test.Check("problems: clear restores every severity and focuses search", () =>
            problemsFilter.Text.Length == 0 && Captions().Any(text => text.Contains("Simplify expression"))
            && compositor.Focus == problemsFilter);
        test.Run(() =>
        {
            var row = Descendants(problems).OfType<Button>().First(button =>
                button.Tooltip.StartsWith("Unused value", StringComparison.Ordinal));
            row.OnPressed!();
        });
        test.Wait();
        test.Check("problems: filtered rows still navigate to their source", () => document.Path == file && code.Document.Caret.Line == 1);
        test.Run(() => Shot("problems-search"));
        test.Settle(0.2f);
        test.Run(() => { code.Diagnostics = []; problemsDirty = true; CloseNow(); });
    }
}
