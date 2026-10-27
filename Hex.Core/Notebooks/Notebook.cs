using System.Text.Json.Nodes;

namespace Hex.Core.Notebooks;

/// <summary>What a cell of a notebook is, code run by the kernel, markdown shown written out, or raw text left as it is.</summary>
internal enum CellKind
{
    Code,
    Markdown,
    Raw
}

/// <summary>
/// One cell of a notebook, its source, and for code what running it came to (its outputs, as nbformat has them, and the
/// count of the run). Whatever else the file had on it (metadata, attachments, anything newer) is kept as it was, so a
/// notebook opened and saved by Hex only changes where it was changed.
/// </summary>
internal sealed class NotebookCell
{
    public CellKind Kind { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Id { get; set; } = NewId();

    /// <summary>What running it came to, each an nbformat output (output_type and the rest), multi line text as one string.</summary>
    public List<JsonObject> Outputs { get; } = [];

    /// <summary>Which run of the kernel it was, null for never run.</summary>
    public int? ExecutionCount { get; set; }

    public JsonObject Metadata { get; set; } = [];

    /// <summary>Everything else the cell had in the file, written back as it came.</summary>
    public JsonObject Rest { get; set; } = [];

    /// <summary>A new id, eight letters and numbers the way Jupyter makes them.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..8];
}

/// <summary>
/// A Jupyter notebook (nbformat 4), its cells and its metadata (the kernel it was made with, its language). Read from and
/// written to its file by <see cref="NotebookFile"/>.
/// </summary>
internal sealed class Notebook
{
    public List<NotebookCell> Cells { get; } = [];
    public JsonObject Metadata { get; set; } = [];
    public int Format { get; set; } = 4;
    public int FormatMinor { get; set; } = 5;

    /// <summary>Everything else the file had at the top, written back as it came.</summary>
    public JsonObject Rest { get; set; } = [];

    /// <summary>The kernel the notebook says it was made with, by name, null when it says none.</summary>
    public string? KernelName => Metadata["kernelspec"]?["name"]?.GetValue<string>();

    /// <summary>The language its code is in, out of what it says about its kernel or language, python when it says nothing.</summary>
    public string Language =>
        Metadata["language_info"]?["name"]?.GetValue<string>() ?? Metadata["kernelspec"]?["language"]?.GetValue<string>() ?? "python";

    /// <summary>The kernel it was run with written into it, the way Jupyter does once a kernel's picked.</summary>
    public void UseKernel(KernelSpec spec, JsonObject languageInfo)
    {
        Metadata["kernelspec"] = new JsonObject { ["display_name"] = spec.DisplayName, ["language"] = spec.Language, ["name"] = spec.Name };
        if (languageInfo.Count > 0)
            Metadata["language_info"] = languageInfo.DeepClone();
    }

    /// <summary>An empty notebook with one empty code cell, what a new .ipynb starts as.</summary>
    public static Notebook Empty()
    {
        var made = new Notebook();
        made.Cells.Add(new NotebookCell { Kind = CellKind.Code });
        return made;
    }
}
