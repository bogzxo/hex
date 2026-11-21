namespace Hex.Core;

/// <summary>
/// A document as it is when the typing has paused, taken on the editor's thread without copying text: its lines (the
/// document's own strings), the caret's line and column, the version it was at, its path inside its folder and its
/// language. <paramref name="Related"/> is asked off the editor's thread for excerpts of other files to send with it.
/// </summary>
internal sealed record PredictionSnapshot(
    object Document,
    int Version,
    string Path,
    string Language,
    string[] Lines,
    int CaretLine,
    int CaretColumn,
    Func<IReadOnlyList<PredictionExcerpt>>? Related = null);
