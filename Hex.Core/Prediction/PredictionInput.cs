namespace Hex.Core;

/// <summary>
/// Everything a prompt is built from: the file's path inside its folder, its language, its text with lines ended by
/// '\n', the caret as an offset into that text, the recent edits (oldest first), excerpts of other files and, for a
/// notebook cell, the source of nearby cells. <paramref name="Rename"/> is the rename the user is making, when one is
/// being followed.
/// </summary>
internal sealed record PredictionInput(
    string Path,
    string Language,
    string Text,
    int Caret,
    IReadOnlyList<PredictionEvent> Events,
    IReadOnlyList<PredictionExcerpt> Related,
    PredictionNotebookContext? Notebook = null,
    PredictionRenameHint? Rename = null);

/// <summary>A rename being made in a file: the old name, the new one and how many whole-word occurrences of the old one remain.</summary>
internal sealed record PredictionRenameHint(string OldName, string NewName, int Remaining)
{
    /// <summary>The one line the prompts say it in: Renamed count → total in Foo.cs; 4 occurrences remain.</summary>
    public string Summary(string path)
    {
        string where = path.Replace('\\', '/');
        string left = Remaining == 1 ? "1 occurrence remains" : Remaining.ToString(System.Globalization.CultureInfo.InvariantCulture) + " occurrences remain";
        return $"Renamed {OldName} → {NewName} in {where}; {left}.";
    }
}
