using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class HexLanguages
{
    private sealed record SelectionHistory(HexDocument Document, LanguageServer Server, int Version, int Workspace, List<UITextSelection> Steps)
    {
        public int Index;
    }
    private SelectionHistory? selectionHistory;

    private bool SelectionHistoryCurrent() => selectionHistory is { } history && context.Documents.Current == history.Document
        && history.Workspace == editWorkspaceGeneration && history.Version == history.Document.Editor.Document.Version
        && !history.Document.Editor.Document.HasExtraCarets && linters.GetValueOrDefault(history.Document) == history.Server
        && history.Steps[history.Index] == new UITextSelection(history.Document.Editor.Document.Caret, history.Document.Editor.Document.Anchor);

    private void ApplySelectionStep(int step)
    {
        var history = selectionHistory!;
        history.Index = step;
        var selection = history.Steps[step];
        history.Document.Editor.Document.Select(selection.Anchor, selection.Caret);
    }

    private void ExpandSelection()
    {
        var of = context.Documents.Current;
        var text = of.Editor.Document;
        if (text.HasExtraCarets || !Serves(of, "selectionRangeProvider", out var server, out string path)) return;
        if (SelectionHistoryCurrent() && selectionHistory is { } history)
        {
            if (history.Index + 1 < history.Steps.Count) ApplySelectionStep(history.Index + 1);
            return;
        }
        var original = new UITextSelection(text.Caret, text.Anchor);
        var request = CaptureNavigation(of, server);
        var at = original.Start;
        WhenNavigating(request, server.SelectionRanges(path, at.Line, at.Column), ranges =>
        {
            if (text.Anchor != original.Anchor || text.HasExtraCarets) return;
            List<UITextSelection> steps = [original];
            foreach (var range in ranges)
            {
                if (range.Start > original.Start || range.End < original.End || range.Start == original.Start && range.End == original.End) continue;
                if (range.End.Line >= text.LineCount || range.Start.Column > text.LineAt(range.Start.Line).Length
                    || range.End.Column > text.LineAt(range.End.Line).Length) continue;
                steps.Add(new(range.End, range.Start));
            }
            selectionHistory = new(of, server, text.Version, editWorkspaceGeneration, steps);
            if (steps.Count > 1) ApplySelectionStep(1);
        });
    }

    private void ShrinkSelection()
    {
        if (SelectionHistoryCurrent() && selectionHistory!.Index > 0) ApplySelectionStep(selectionHistory.Index - 1);
    }
}
