using System.Text;
using System.Text.Json.Nodes;

using Hex.Core.Notebooks;

namespace Hex.Dawn;

// The tools for Jupyter notebooks, the notebook on screen read as its cells and what they came to, a cell written, put in,
// taken out or made another kind, cells run in its kernel and waited for, and the kernel interrupted, restarted or
// stopped. Cells are counted from 1, the way an agent reads them, the notebook's own are from 0. Pictures come back as
// what they are and how big, the text of every other output as it is with its ANSI escapes taken out
internal sealed partial class DawnScene
{
    // The most of a cell's output text handed back, and how long a run is waited for at the most
    private const int MCP_MOST_OUTPUT = 20_000;
    private const float MCP_RUN_PATIENCE = 600.0f;

    private IEnumerable<HexMcpTool> NotebookTools() =>
    [
        new(new McpTool("hex_notebook", "Notebook cells",
            "Read the Jupyter notebook on screen as its cells, each with its number (1 is the first), kind, run count, source and outputs (text, errors, pictures as their size). Open one first with hex_open.",
            McpSchema.Object([
                ("cells", McpSchema.String("Which cells, \"all\" (the default), \"3\", \"2,5\" or \"2-4\".")),
                ("outputs", McpSchema.Flag("Include the outputs, true by default."))]), ReadOnly: true),
            NotebookCells),

        new(new McpTool("hex_notebook_edit", "Edit notebook",
            "Change the notebook on screen. set writes a cell's source, insert puts a new cell before the numbered one (past the end to add it last), delete takes one out, kind makes one code, markdown or raw.",
            McpSchema.Object([
                ("action", McpSchema.OneOf("What to do.", "set", "insert", "delete", "kind")),
                ("cell", McpSchema.Integer("The cell, 1 is the first.")),
                ("source", McpSchema.String("The source, for set and insert.")),
                ("kind", McpSchema.OneOf("The kind, for insert and kind, code by default.", "code", "markdown", "raw"))], "action", "cell")),
            NotebookEdit),

        new(new McpTool("hex_notebook_run", "Run notebook cells",
            "Run cells of the notebook on screen in its kernel (started if it isn't up) and wait for them, then say what each came to. A cell that errors stops the ones after it, as in Jupyter.",
            McpSchema.Object([
                ("cells", McpSchema.String("Which cells, \"all\", \"3\", \"2,5\" or \"2-4\".")),
                ("timeout", McpSchema.Number("Seconds to wait at the most, 120 by default."))], "cells")),
            NotebookRun, MCP_RUN_PATIENCE + 5.0f),

        new(new McpTool("hex_notebook_kernel", "Notebook kernel",
            "Interrupt what the notebook's kernel is running, restart it (what it knew is gone), or shut it down.",
            McpSchema.Object([("action", McpSchema.OneOf("What to do.", "interrupt", "restart", "shutdown"))], "action")),
            NotebookKernel),
    ];

    // Helper method for the notebook on screen, or why there isn't one
    private HexNotebook? McpNotebook(out string? why)
    {
        why = null;
        if (!IsNotebook(document))
        {
            why = "The file on screen isn't a Jupyter notebook, open one with hex_open.";
            return null;
        }

        if (document.View != HexView.Notebook)
            SwitchView(HexView.Notebook);
        HexNotebook notebook = NotebookOf(document);
        if (notebook.Unreadable is { } broken)
            why = $"The notebook's JSON can't be read, {broken}";
        return why is null ? notebook : null;
    }

    // Helper method for which cells a list names, "all", "3", "2,5", "2-4", from 1, those that are there
    private static List<int> McpCells(string? named, int count)
    {
        var cells = new List<int>();
        if (string.IsNullOrWhiteSpace(named) || named.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
            return [.. Enumerable.Range(0, count)];

        foreach (string part in named.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] ends = part.Split('-', 2, StringSplitOptions.TrimEntries);
            if (!int.TryParse(ends[0], out int from))
                continue;
            int to = ends.Length > 1 && int.TryParse(ends[1], out int last) ? last : from;
            for (int cell = from; cell <= to; cell++)
            {
                if (cell >= 1 && cell <= count && !cells.Contains(cell - 1))
                    cells.Add(cell - 1);
            }
        }

        return cells;
    }

    // Helper method for a cell as an agent reads it
    private static void McpCell(StringBuilder said, NotebookCell cell, int index, bool outputs)
    {
        string kind = cell.Kind.ToString().ToLowerInvariant();
        said.Append($"## Cell {index + 1}, {kind}{(cell.ExecutionCount is { } count ? $", run {count}" : string.Empty)}\n");
        said.Append(cell.Source.Length > 0 ? cell.Source : "(empty)");
        said.Append('\n');
        if (!outputs || cell.Outputs.Count == 0)
            return;

        said.Append("Outputs:\n");
        foreach (JsonObject output in cell.Outputs)
            said.Append(McpOutput(output)).Append('\n');
    }

    // Helper method for an output as text, what it printed or came to, an error's traceback, a picture's size
    private static string McpOutput(JsonObject output)
    {
        string text = (output["output_type"]?.GetValue<string>() ?? string.Empty) switch
        {
            "stream" => NotebookFile.Text(output["text"]),
            "error" => output["traceback"] is JsonArray lines ? string.Join('\n', lines.Select(line => line?.GetValue<string>() ?? string.Empty)) : $"{output["ename"]}: {output["evalue"]}",
            _ when output["data"] is JsonObject data => data["image/png"] is not null || data["image/jpeg"] is not null
                ? $"[a picture{(data["text/plain"] is { } plain ? ", " + NotebookFile.Text(plain) : string.Empty)}]"
                : data["text/markdown"] is { } markdown ? NotebookFile.Text(markdown)
                : data["text/plain"] is { } said ? NotebookFile.Text(said)
                : $"[{string.Join(", ", data.Select(pair => pair.Key))}]",
            _ => string.Empty
        };

        text = AnsiText.Read(text).Text.TrimEnd('\n');
        return text.Length > MCP_MOST_OUTPUT ? text[..MCP_MOST_OUTPUT] + "\n(cut short)" : text;
    }

    private IEnumerable<McpToolResult?> NotebookCells(JsonObject arguments)
    {
        if (McpNotebook(out string? why) is not { } notebook)
        {
            yield return McpToolResult.Error(why!);
            yield break;
        }

        bool outputs = arguments.Flag("outputs", fallback: true);
        var said = new StringBuilder($"{McpWhere(document)}{(document.Modified ? " (modified)" : string.Empty)}, {notebook.Model.Cells.Count} cells, kernel {KernelStatus(notebook)}.\n\n");
        foreach (int index in McpCells(arguments.String("cells"), notebook.Model.Cells.Count))
            McpCell(said, notebook.Model.Cells[index], index, outputs);
        yield return McpToolResult.Text(said.ToString());
    }

    private IEnumerable<McpToolResult?> NotebookEdit(JsonObject arguments)
    {
        if (McpNotebook(out string? why) is not { } notebook)
        {
            yield return McpToolResult.Error(why!);
            yield break;
        }

        List<NotebookCell> cells = notebook.Model.Cells;
        int index = (arguments.Integer("cell") ?? 0) - 1;
        CellKind kind = arguments.String("kind") switch { "markdown" => CellKind.Markdown, "raw" => CellKind.Raw, _ => CellKind.Code };
        string action = arguments.String("action") ?? string.Empty;
        bool there = index >= 0 && index < cells.Count;

        switch (action)
        {
            case "set" when there:
                cells[index].Source = arguments.String("source") ?? string.Empty;
                break;
            case "insert" when index >= 0:
                index = Math.Min(index, cells.Count);
                cells.Insert(index, new NotebookCell { Kind = kind, Source = arguments.String("source") ?? string.Empty });
                break;
            case "delete" when there:
                cells.RemoveAt(index);
                if (cells.Count == 0)
                    cells.Add(new NotebookCell { Kind = CellKind.Code });
                index = Math.Min(index, cells.Count - 1);
                break;
            case "kind" when there:
                cells[index].Kind = kind;
                if (kind != CellKind.Code)
                {
                    cells[index].Outputs.Clear();
                    cells[index].ExecutionCount = null;
                }
                break;
            default:
                yield return McpToolResult.Error($"There's no cell {index + 1} to {action}, the notebook has {cells.Count}.");
                yield break;
        }

        notebook.Selected = Math.Clamp(index, 0, cells.Count - 1);
        notebook.Editing = false;
        notebook.Remake = true;
        NotebookChanged(notebook, now: true);
        yield return null;

        var said = new StringBuilder($"Done, the notebook has {cells.Count} cells.\n\n");
        if (action != "delete")
            McpCell(said, cells[index], index, outputs: false);
        yield return McpToolResult.Text(said.ToString());
    }

    private IEnumerable<McpToolResult?> NotebookRun(JsonObject arguments)
    {
        if (McpNotebook(out string? why) is not { } notebook)
        {
            yield return McpToolResult.Error(why!);
            yield break;
        }

        List<int> picked = McpCells(arguments.String("cells"), notebook.Model.Cells.Count);
        var cells = picked.Select(index => notebook.Model.Cells[index]).Where(cell => cell.Kind == CellKind.Code).ToList();
        if (cells.Count == 0)
        {
            yield return McpToolResult.Error("None of those are code cells.");
            yield break;
        }

        RunCells(notebook, cells);
        float patience = Math.Clamp((float)(arguments.Number("timeout") ?? 120.0), 1.0f, MCP_RUN_PATIENCE);
        long until = System.Environment.TickCount64 + (long)(patience * 1000.0f);
        while ((notebook.Starting || notebook.Waiting.Count > 0 || cells.Any(notebook.Run.IsQueued)) && System.Environment.TickCount64 < until)
            yield return null;

        bool finished = !notebook.Starting && notebook.Waiting.Count == 0 && !cells.Any(notebook.Run.IsQueued);
        if (notebook.Kernel is null && !finished)
        {
            yield return McpToolResult.Error("No kernel came up to run them in, see hex_problems or the status bar.");
            yield break;
        }

        var said = new StringBuilder(finished ? "Ran them.\n\n" : $"Still running after {patience:0} seconds, what they came to so far.\n\n");
        foreach (int index in picked)
            McpCell(said, notebook.Model.Cells[index], index, outputs: true);
        yield return McpToolResult.Text(said.ToString());
    }

    private IEnumerable<McpToolResult?> NotebookKernel(JsonObject arguments)
    {
        if (McpNotebook(out string? why) is not { } notebook)
        {
            yield return McpToolResult.Error(why!);
            yield break;
        }

        switch (arguments.String("action"))
        {
            case "interrupt":
                InterruptKernel(notebook);
                break;
            case "restart":
                RestartKernel(notebook);
                while (notebook.Starting)
                    yield return null;
                break;
            case "shutdown":
                StopKernel(notebook);
                break;
            default:
                yield return McpToolResult.Error("The action is interrupt, restart or shutdown.");
                yield break;
        }

        yield return null;
        yield return McpToolResult.Text($"The kernel is {KernelStatus(notebook)}.");
    }
}
