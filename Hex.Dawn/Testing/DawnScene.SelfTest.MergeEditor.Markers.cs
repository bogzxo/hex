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
            code.Document.Select(new(4, 0), new(4, 1));
            code.Document.Insert("");
            CompleteMergeEditor(document);
        });
        test.Check("merge editor: damaging the separator keeps the block unresolved and prevents writing", () =>
            merge is { UnresolvedCount: 1, CanComplete: false } && writes == 0 && code.Text.Contains("======\n"));
        test.Run(() =>
        {
            if (document.Merge != merge) return;
            code.Document.Undo();
            code.Document.Select(new(0, 8), new(0, 12));
            code.Document.Insert("label");
        });
        test.Check("merge editor: editing a marker label keeps both alternatives", () =>
            merge is { UnresolvedCount: 1, CanComplete: false } && code.Text.Contains("<<<<<<< label") && code.Text.Contains("two"));
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
        });
        test.Check("merge editor: undo restores the partially edited block and disables completion", () =>
            merge is { UnresolvedCount: 1, CanComplete: false } && code.Text.Contains("<<<<<<< label"));
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
