namespace Hex.Core;

/// <summary>
/// Everything a prompt is built from: the file's path inside its folder, its language, its text with lines ended by
/// '\n', the caret as an offset into that text, the recent edits (oldest first), excerpts of other files and, for a
/// notebook cell, the source of nearby cells.
/// </summary>
internal sealed record PredictionInput(
    string Path,
    string Language,
    string Text,
    int Caret,
    IReadOnlyList<PredictionEvent> Events,
    IReadOnlyList<PredictionExcerpt> Related,
    PredictionNotebookContext? Notebook = null);
