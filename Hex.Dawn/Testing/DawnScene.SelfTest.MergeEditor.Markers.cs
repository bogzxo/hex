using System.Text;
using Hex.Core.Merge;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestManualMergeDelimiters(HexPointerScript test)
    {
        const string draft = "<<<<<<< mine\none\n||||||| base\nold\n=======\ntwo\n>>>>>>> theirs\n";
        MergeEditor? merge = null;
        int writes = 0;
        test.Run(() =>
        {
            merge = OpenMergeEditor(new("sample.txt", "Mine", "Theirs", "Base", MergeModel.FromMarkers(draft),
                _ => { writes++; return Task.CompletedTask; }, static () => { }, Path.Combine(testFolder, "sample.txt")));
            CompleteMergeEditor(document);
        });
        test.Check("merge editor: an unresolved conflict shows its base text with no marker lines and cannot be written", () =>
            merge is { UnresolvedCount: 1, CanComplete: false } && writes == 0 && code.Text == "old\n"
            && !code.Text.Contains("<<<<<<<") && !code.Text.Contains("=======") && !code.Text.Contains(">>>>>>>"));
        test.Check("merge editor: the header bar over the conflict names it and offers every choice", () =>
        {
            var lenses = code.LensesAbove.Where(lens => lens.Line == 0).Select(lens => lens.Text).ToArray();
            return lenses.Any(text => text.StartsWith("Conflict 1 of 1", StringComparison.Ordinal))
                && lenses.Contains("Use yours") && lenses.Contains("Use theirs")
                // A narrow pane puts the rest in the More menu
                && (lenses.Contains("More…") || lenses.Contains("Use both (yours first)")
                    && lenses.Contains("Use both (theirs first)") && lenses.Contains("Edit"));
        });
        test.Run(() =>
        {
            if (document.Merge != merge) return;
            code.Document.Select(new(0, 0), new(0, 3));
            code.Document.Insert("new");
        });
        test.Check("merge editor: typing inside the region resolves it by hand", () =>
            merge is { UnresolvedCount: 0, CanComplete: true } && code.Text == "new\n"
            && code.LensesAbove.Any(lens => lens.Text == "Undo choice"));
        test.Run(() =>
        {
            if (document.Merge != merge) return;
            code.Document.Select(new(0, 0), new(code.Document.LineCount - 1, 0));
            code.Document.Insert("heading\n=======\n");
        });
        test.Check("merge editor: a whole-block manual replacement can contain a literal underline", () =>
            merge is { UnresolvedCount: 0, CanComplete: true } && Encoding.UTF8.GetString(merge.ResultBytes()) == "heading\n=======\n");
        test.Run(() =>
        {
            if (document.Merge != merge) return;
            code.Document.Undo();
            code.Document.Undo();
        });
        test.Check("merge editor: undo restores the unresolved region and disables completion", () =>
            merge is { UnresolvedCount: 1, CanComplete: false } && code.Text == "old\n");
        test.Run(() => { if (merge is not null && document.Merge == merge) merge.Accept(0, MergeChoice.BothTheirsFirst); });
        test.Check("merge editor: Use both (theirs first) writes the two sides and Undo choice returns to unresolved", () =>
        {
            if (merge is not { UnresolvedCount: 0 } || code.Text != "two\none\n")
                return false;
            merge.Accept(0, MergeChoice.Unresolved);
            return merge.UnresolvedCount == 1 && code.Text == "old\n";
        });
        test.Run(() =>
        {
            if (document.Merge == merge) CloseMergeTab(document);
            merge = OpenMergeEditor(new("sample.txt", "Mine", "Theirs", "Base", MergeModel.FromMarkers("clean\n" + draft),
                _ => Task.CompletedTask, static () => { }, Path.Combine(testFolder, "sample.txt")));
            merge.AcceptCurrent(MergeChoice.Theirs);
        });
        test.Check("merge editor: Accept Current initially selects the first conflict after clean lines", () =>
            merge is { CanComplete: true } && code.Text == "clean\ntwo\n");
        test.Run(() => { if (document.Merge == merge) CloseMergeTab(document); });
    }
}
