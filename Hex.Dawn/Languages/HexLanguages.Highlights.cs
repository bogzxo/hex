using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class HexLanguages
{
    private (HexDocument? Document, LanguageServer? Server) highlightOwner;
    private sealed record HighlightRequest(HexDocument Document, LanguageServer Server, int Version, UITextPosition Caret);
    private HighlightRequest? highlightRequested, highlightAnswered;
    private IReadOnlyList<UICodeOccurrence> highlighted = [];

    private void UpdateDocumentHighlights()
    {
        var document = context.Documents.Current;
        var server = linters.GetValueOrDefault(document);
        if (server?.Failure is not null || server?.Offers("documentHighlightProvider") != true) server = null;
        var owner = (document, server);
        if (highlightOwner == owner) return;
        if (highlightOwner.Document is { } old)
        {
            old.Editor.OccurrenceSource = null;
            old.Editor.RefreshOccurrences();
        }
        highlightOwner = owner;
        highlightRequested = highlightAnswered = null;
        highlighted = [];
        if (server is not null) document.Editor.OccurrenceSource = caret => ServerHighlights(document, caret);
        document.Editor.RefreshOccurrences();
    }

    private IReadOnlyList<UICodeOccurrence>? ServerHighlights(HexDocument document, UITextPosition caret)
    {
        if (!Serves(document, "documentHighlightProvider", out var server, out string path)) return null;
        var request = new HighlightRequest(document, server, document.Editor.Document.Version, caret);
        if (highlightAnswered == request) return highlighted;
        if (highlightRequested != request)
        {
            highlightRequested = request;
            When(server.DocumentHighlights(path, caret.Line, caret.Column), found =>
            {
                if (highlightRequested != request || context.Documents.Current != document
                    || document.Editor.Document.Version != request.Version || document.Editor.Document.Caret != caret
                    || linters.GetValueOrDefault(document) != server) return;
                List<UICodeOccurrence> ranges = [];
                foreach (var item in found)
                {
                    for (int line = item.Line; line <= item.EndLine && line < document.Editor.Document.LineCount; line++)
                    {
                        int length = document.Editor.Document.LineAt(line).Length;
                        int start = Math.Min(line == item.Line ? item.Column : 0, length);
                        int end = Math.Min(line == item.EndLine ? item.EndColumn : length, length);
                        if (end > start) ranges.Add(new(line, start, end - start, item.Kind));
                    }
                }
                highlighted = ranges;
                highlightAnswered = request;
                document.Editor.RefreshOccurrences();
            });
        }
        return [];
    }
}
