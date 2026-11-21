namespace Hex.Core;

/// <summary>
/// A finished prediction for a document as it was at a version: the edits in offsets of its text with lines ended by
/// '\n', where the caret goes after them (an offset into the text once they're made), whether it's ghost text (one
/// insertion at the caret) rather than a diff, whether the cache answered it, and how long it took.
/// </summary>
internal sealed record PredictionResult(
    object Document,
    int Version,
    IReadOnlyList<PredictionEdit> Edits,
    int? CaretAfter,
    bool Ghost,
    bool FromCache,
    PredictionTiming Timing);
