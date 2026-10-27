using Hex.Core.Notebooks;

namespace Hex.Dawn;

/// <summary>
/// An open notebook, what it is (its cells as last read or changed), the kernel its cells run in once one's been started,
/// what's running, which cell is picked and whether it's being typed into. The document's own text is the notebook's
/// JSON (what's saved, what the Code view shows), written from the cells a moment after they change and read again
/// when it changes from under them.
/// </summary>
internal sealed class HexNotebook
{
    public required HexDocument Document { get; init; }
    public Notebook Model { get; set; } = Notebook.Empty();

    /// <summary>
    /// The version of the document's text the cells were last read from or written as, it's something else once the JSON
    /// changed from under them. Its text compared every tick was the whole JSON made, pictures and all, sixty times a second.
    /// </summary>
    public int JsonVersion { get; set; } = -1;

    /// <summary>The JSON the cells were last read from or written as.</summary>
    public string Json { get; set; } = string.Empty;

    /// <summary>Why the JSON couldn't be read, null when it could.</summary>
    public string? Unreadable { get; set; }

    public NotebookRun Run { get; } = new();
    public Kernel? Kernel { get; set; }
    public KernelSpec? Spec { get; set; }

    /// <summary>A kernel on its way up, cells run meanwhile wait for it.</summary>
    public bool Starting { get; set; }

    /// <summary>Cells run before the kernel was up, sent once it is.</summary>
    public List<NotebookCell> Waiting { get; } = [];

    /// <summary>Which cell is picked, and whether it's being typed into (edit mode) or not (command mode).</summary>
    public int Selected { get; set; }
    public bool Editing { get; set; }

    /// <summary>The cells changed since the JSON was written, written a moment after the last change.</summary>
    public bool Changed { get; set; }
    public float WriteIn { get; set; }

    /// <summary>Whether the cells on screen have to be made again (another notebook shown, cells added or taken out).</summary>
    public bool Remake { get; set; } = true;

    /// <summary>The cell picked, null in a notebook with none.</summary>
    public NotebookCell? Cell => Selected >= 0 && Selected < Model.Cells.Count ? Model.Cells[Selected] : null;
}
