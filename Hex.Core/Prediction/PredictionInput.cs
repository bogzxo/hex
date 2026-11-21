namespace Hex.Core;

/// <summary>
/// Everything a prompt is built from: the file's path inside its folder, its language, its text with lines ended by
/// '\n', the caret as an offset into that text, the recent edits (oldest first) and excerpts of other files.
/// </summary>
internal sealed record PredictionInput(
    string Path,
    string Language,
    string Text,
    int Caret,
    IReadOnlyList<PredictionEvent> Events,
    IReadOnlyList<PredictionExcerpt> Related);
