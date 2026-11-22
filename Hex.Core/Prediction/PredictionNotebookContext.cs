using Hex.Core.Notebooks;

namespace Hex.Core;

/// <summary>The source of the cells round the one being edited, in notebook order and bounded for the prompt.</summary>
internal sealed record PredictionNotebookContext(string Before, string After)
{
    private const int BEFORE_CHARACTERS = 4500;
    private const int AFTER_CHARACTERS = 1500;
    private const string BETWEEN_CELLS = "\n\n";

    public static PredictionNotebookContext? Capture(Notebook notebook, NotebookCell cell)
    {
        int index = notebook.Cells.IndexOf(cell);
        if (index < 0)
            return null;

        return new PredictionNotebookContext(
            Take(notebook.Cells, index - 1, -1, BEFORE_CHARACTERS),
            Take(notebook.Cells, index + 1, 1, AFTER_CHARACTERS));
    }

    // Start at the active cell so the closest source is kept first, then put it back in notebook order.
    private static string Take(List<NotebookCell> cells, int index, int step, int remaining)
    {
        var sources = new List<string>();
        for (; index >= 0 && index < cells.Count && remaining > 0; index += step)
        {
            string source = cells[index].Source;
            int length = Math.Min(remaining, source.Length);
            if (length == 0)
                continue;

            int start = step < 0 ? source.Length - length : 0;
            if (char.IsLowSurrogate(source[start]))
            {
                start++;
                length--;
            }
            if (length > 0 && char.IsHighSurrogate(source[start + length - 1]))
                length--;
            if (length > 0)
                sources.Add(source.Substring(start, length).ReplaceLineEndings("\n"));
            remaining -= length + BETWEEN_CELLS.Length;
        }

        if (step < 0)
            sources.Reverse();
        return string.Join(BETWEEN_CELLS, sources);
    }
}
