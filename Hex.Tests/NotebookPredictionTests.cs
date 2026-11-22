using Hex.Core;
using Hex.Core.Notebooks;

namespace Hex.Tests;

public sealed class NotebookPredictionTests
{
    [Fact]
    public void Captured_context_keeps_other_cells_in_order_and_only_takes_their_source()
    {
        var notebook = new Notebook();
        var editing = new NotebookCell { Kind = CellKind.Code, Source = "editing" };
        var first = new NotebookCell { Kind = CellKind.Markdown, Source = "# Inputs\r\nFrom the experiment" };
        first.Metadata["secret"] = "metadata";
        var next = new NotebookCell { Kind = CellKind.Code, Source = "print(total)" };
        next.Outputs.Add(new() { ["text"] = "output" });
        notebook.Cells.AddRange([first, new() { Source = "initial = 1" }, editing, next, new() { Source = "finish()" }]);

        PredictionNotebookContext? context = PredictionNotebookContext.Capture(notebook, editing);
        next.Source = "changed after capture";

        Assert.Equal(new PredictionNotebookContext("# Inputs\nFrom the experiment\n\ninitial = 1", "print(total)\n\nfinish()"), context);
        Assert.Null(PredictionNotebookContext.Capture(notebook, new NotebookCell()));
    }

    [Fact]
    public void Large_nearby_cells_keep_the_text_nearest_the_edited_cell()
    {
        var notebook = new Notebook();
        var editing = new NotebookCell();
        notebook.Cells.AddRange([
            new() { Source = "FAR ABOVE" },
            new() { Source = new string('a', 10_000) + "\nNEAR ABOVE" },
            editing,
            new() { Source = "NEAR BELOW\n" + new string('b', 10_000) },
            new() { Source = "FAR BELOW" }
        ]);

        var context = PredictionNotebookContext.Capture(notebook, editing);
        Assert.NotNull(context);
        Assert.EndsWith("\nNEAR ABOVE", context.Before);
        Assert.StartsWith("NEAR BELOW\n", context.After);
        Assert.DoesNotContain("FAR ABOVE", context.Before);
        Assert.DoesNotContain("FAR BELOW", context.After);
        Assert.InRange(context.Before.Length + context.After.Length, 1, 6_000);
    }

    [Fact]
    public void Fim_puts_other_cells_around_the_active_source_and_returns_cell_local_offsets()
    {
        const string TEXT = "total = ";
        var input = new PredictionInput("results.ipynb#cell-total", "python", TEXT, TEXT.Length, [], [],
            new("initial = 1\n\nscale = 2", "print(total)\n\nfinish()"));

        PredictionPrompt? prompt = FimFormat.Qwen.Build(input, 128);

        Assert.NotNull(prompt);
        Assert.Equal("<|fim_prefix|># results.ipynb#cell-total\ninitial = 1\n\nscale = 2\n\ntotal = <|fim_suffix|>\n\nprint(total)\n\nfinish()<|fim_middle|>",
            prompt.Request.Prompt);
        Assert.DoesNotContain("\n", prompt.Request.Stop);
        PredictionParsed parsed = FimFormat.Qwen.Parse(prompt, "initial * scale", complete: true);
        Assert.Equal(new PredictionEdit(TEXT.Length, TEXT.Length, "initial * scale"), Assert.Single(parsed.Edits));
    }

    [Fact]
    public void Zeta_has_read_only_cell_context_and_only_edits_the_active_cell()
    {
        const string TEXT = "total = 1";
        var input = new PredictionInput("results.ipynb#cell-total", "python", TEXT, TEXT.Length, [], [],
            new("initial = 1", "print(total)"));
        var format = new Zeta2Format();

        PredictionPrompt? prompt = format.Build(input, 128);

        Assert.NotNull(prompt);
        Assert.Equal(TEXT, prompt.Region);
        Assert.Contains("<filename>results.ipynb#cell-total/before\ninitial = 1\n<filename>results.ipynb#cell-total/after\nprint(total)", prompt.Request.Prompt);
        Assert.Contains("<<<<<<< CURRENT\ntotal = 1<|user_cursor|>\n=======", prompt.Request.Prompt);
        PredictionParsed parsed = format.Parse(prompt, "total = 10\n>>>>>>> UPDATED\n", complete: true);
        Assert.Equal(new PredictionEdit(TEXT.Length, TEXT.Length, "0"), Assert.Single(parsed.Edits));
    }
}
