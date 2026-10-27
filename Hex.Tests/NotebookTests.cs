using System.Text.Json.Nodes;

using Hex.Core.Notebooks;

namespace Hex.Tests;

/// <summary>
/// Notebooks read and written the way Jupyter writes them, text carried on the way a terminal has it, and a real kernel
/// talked to natively, started, run, interrupted and asked for input. The kernel ones pass without doing anything on a
/// machine with no Python that has ipykernel (the build machines), there's nothing to talk to there.
/// Run after the rest on their own, a kernel started while every other class was busy failed now and then.
/// </summary>
[Collection(Timed.NAME)]
public class NotebookTests
{
    private const string JUPYTER_WRITTEN = """
        {
         "cells": [
          {
           "cell_type": "markdown",
           "id": "a1b2c3d4",
           "metadata": {},
           "source": [
            "# Title\n",
            "Some *words*."
           ]
          },
          {
           "cell_type": "code",
           "execution_count": 3,
           "id": "e5f6a7b8",
           "metadata": {
            "tags": [
             "keep"
            ]
           },
           "outputs": [
            {
             "name": "stdout",
             "output_type": "stream",
             "text": [
              "hello\n",
              "world\n"
             ]
            },
            {
             "data": {
              "image/png": "iVBORw0KGgo=",
              "text/plain": [
               "<Figure size 640x480 with 1 Axes>"
              ]
             },
             "execution_count": 3,
             "metadata": {},
             "output_type": "execute_result"
            }
           ],
           "source": [
            "print('hello')\n",
            "print('world')"
           ]
          }
         ],
         "metadata": {
          "kernelspec": {
           "display_name": "Python 3 (ipykernel)",
           "language": "python",
           "name": "python3"
          },
          "language_info": {
           "name": "python",
           "version": "3.13.15"
          }
         },
         "nbformat": 4,
         "nbformat_minor": 5
        }

        """;

    [Fact]
    public void A_notebook_jupyter_wrote_is_read_and_written_back_byte_for_byte()
    {
        string file = JUPYTER_WRITTEN.ReplaceLineEndings("\n");
        Notebook read = NotebookFile.Read(file);

        Assert.Equal(2, read.Cells.Count);
        Assert.Equal(CellKind.Markdown, read.Cells[0].Kind);
        Assert.Equal("# Title\nSome *words*.", read.Cells[0].Source);
        Assert.Equal("print('hello')\nprint('world')", read.Cells[1].Source);
        Assert.Equal(3, read.Cells[1].ExecutionCount);
        Assert.Equal("hello\nworld\n", read.Cells[1].Outputs[0]["text"]!.GetValue<string>());
        Assert.Equal("python3", read.KernelName);
        Assert.Equal(file, NotebookFile.Write(read));
    }

    [Fact]
    public void A_new_notebook_is_one_empty_code_cell_and_an_empty_file_reads_as_one()
    {
        Notebook empty = NotebookFile.Read("");
        Assert.Single(empty.Cells);
        Assert.Equal(CellKind.Code, empty.Cells[0].Kind);
        Assert.Contains("\"nbformat\": 4", NotebookFile.Write(empty));
    }

    [Fact]
    public void A_notebook_older_than_four_is_turned_away()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => NotebookFile.Read("{\"nbformat\": 3, \"worksheets\": []}"));
    }

    [Theory]
    [InlineData("", "abc", "abc")]
    [InlineData("10%\r", "50%\r", "50%\r")]
    [InlineData("", "10%\r50%\r100%\n", "100%\n")]
    [InlineData("line\n", "ab\bc", "line\nac")]
    [InlineData("long text", "\rok", "ok")]
    [InlineData("a\r\nb", "", "a\nb")]
    public void Text_carried_on_writes_over_itself_at_a_carriage_return(string was, string more, string now) =>
        Assert.Equal(now, NotebookRun.Carried(was, more));

    [Fact]
    public void Streams_run_on_errors_and_results_come_in_and_the_reply_finishes_the_cell()
    {
        var cell = new NotebookCell { Kind = CellKind.Code };
        var run = new NotebookRun();
        run.Sent(cell, "ask");
        KernelEvent Said(string type, JsonObject content) =>
            new(Kernel.IOPUB, new JupyterMessage { Header = new JsonObject { ["msg_type"] = type, ["msg_id"] = Guid.NewGuid().ToString() }, ParentHeader = new JsonObject { ["msg_id"] = "ask" }, Content = content });

        run.Take(Said("execute_input", new JsonObject { ["execution_count"] = 7 }));
        run.Take(Said("stream", new JsonObject { ["name"] = "stdout", ["text"] = "a" }));
        run.Take(Said("stream", new JsonObject { ["name"] = "stdout", ["text"] = "b\n" }));
        run.Take(Said("execute_result", new JsonObject { ["execution_count"] = 7, ["data"] = new JsonObject { ["text/plain"] = "12" } }));
        Assert.True(run.IsQueued(cell));
        run.Take(Said("execute_reply", new JsonObject { ["status"] = "ok", ["execution_count"] = 7 }));

        Assert.False(run.IsQueued(cell));
        Assert.Equal(7, cell.ExecutionCount);
        Assert.Equal(2, cell.Outputs.Count);
        Assert.Equal("ab\n", cell.Outputs[0]["text"]!.GetValue<string>());
        Assert.Equal("12", cell.Outputs[1]["data"]!["text/plain"]!.GetValue<string>());
    }

    [Fact]
    public void A_traceback_s_escapes_come_off_and_its_colours_are_where_they_were()
    {
        var (text, colours) = AnsiText.Read("\x1b[31mError\x1b[39m: no\n\x1b[1;32mline 2\x1b[0m done");

        Assert.Equal("Error: no\nline 2 done", text);
        Assert.Equal(2, colours.Count);
        Assert.Equal(new Horizon.UI.Scripting.SemanticSpan(0, 0, 5, Horizon.UI.Scripting.SyntaxKind.Ansi1), colours[0]);
        Assert.Equal(new Horizon.UI.Scripting.SemanticSpan(1, 0, 6, Horizon.UI.Scripting.SyntaxKind.Ansi10), colours[1]);
    }

    // A kernel to talk to, null when this machine has no Python with ipykernel
    private static KernelSpec? Python() => KernelSpecs.Find().FirstOrDefault(spec => spec.Language == "python");

    // Helper method to take what the kernel says until a cell's reply comes, the cell's outputs made of it
    private static async Task<NotebookCell> Run(Kernel kernel, NotebookRun run, string code, Action<NotebookRun>? meanwhile = null)
    {
        var cell = new NotebookCell { Kind = CellKind.Code, Source = code };
        run.Sent(cell, kernel.Execute(code));
        var until = DateTime.UtcNow.AddSeconds(30);
        while (run.IsQueued(cell) && DateTime.UtcNow < until)
        {
            while (kernel.Events.TryDequeue(out KernelEvent? said))
                run.Take(said);
            meanwhile?.Invoke(run);
            await Task.Delay(20);
        }

        Assert.False(run.IsQueued(cell), "the cell never finished");
        return cell;
    }

    [Fact(Timeout = 120_000)]
    public async Task A_python_kernel_runs_cells_and_keeps_what_they_defined()
    {
        if (Python() is not { } spec)
            return;

        await using var kernel = new Kernel(spec, Path.GetTempPath());
        await kernel.StartAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("python", kernel.Info["name"]?.GetValue<string>());

        var run = new NotebookRun();
        NotebookCell first = await Run(kernel, run, "x = 6\nprint('half', x)\nx * 2");
        Assert.Equal("half 6\n", first.Outputs[0]["text"]!.GetValue<string>());
        Assert.Equal("12", first.Outputs[1]["data"]!["text/plain"]!.GetValue<string>());
        Assert.Equal(1, first.ExecutionCount);

        NotebookCell second = await Run(kernel, run, "x + 1");
        Assert.Equal("7", second.Outputs.Single()["data"]!["text/plain"]!.GetValue<string>());

        NotebookCell broken = await Run(kernel, run, "1 / 0");
        Assert.Equal("ZeroDivisionError", broken.Outputs.Single()["ename"]!.GetValue<string>());

        NotebookCell asking = await Run(kernel, run, "name = input('who? ')\nprint('hi', name)", running =>
        {
            if (running.Input is { } asked)
            {
                Assert.Equal("who? ", asked.Prompt);
                kernel.Answer("bogz", asked.Request);
                running.Answered();
            }
        });
        Assert.Contains("hi bogz", asking.Outputs.Last()["text"]!.GetValue<string>());

        await kernel.ShutdownAsync();
    }

    [Fact(Timeout = 120_000)]
    public async Task A_running_cell_is_interrupted_and_the_kernel_carries_on()
    {
        if (Python() is not { } spec)
            return;

        await using var kernel = new Kernel(spec, Path.GetTempPath());
        await kernel.StartAsync(TimeSpan.FromSeconds(60));

        var run = new NotebookRun();
        var started = DateTime.UtcNow;
        bool sent = false;
        NotebookCell sleeping = await Run(kernel, run, "while True:\n    pass", _ =>
        {
            if (!sent && kernel.State == KernelState.Busy && DateTime.UtcNow - started > TimeSpan.FromSeconds(1))
            {
                sent = true;
                kernel.Interrupt();
            }
        });

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30));
        Assert.Equal("KeyboardInterrupt", sleeping.Outputs.Single()["ename"]!.GetValue<string>());

        NotebookCell after = await Run(kernel, run, "'still here'");
        Assert.Equal("'still here'", after.Outputs.Single()["data"]!["text/plain"]!.GetValue<string>());
        await kernel.ShutdownAsync();
    }
}
