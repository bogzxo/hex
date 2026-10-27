using System.Text.Json.Nodes;

using Hex.Core.Notebooks;

using Horizon.UI.Components;

namespace Hex.Dawn;

// The kernel a notebook's cells run in. Started the first time a cell is run (the one the notebook says it was made with,
// else one of its language, else any), the cells run meanwhile waiting for it, and what it says taken in a tick at a time
// on the editor's thread. Interrupted, restarted, stopped, or swapped for another from Select Kernel. Completions and the
// hover over a name are asked of it too, the way Jupyter's are
internal sealed partial class DawnScene
{
    // How long a kernel has to come up, a first Python on a cold disk can take a while
    private static readonly TimeSpan KERNEL_PATIENCE = TimeSpan.FromSeconds(60);

    // The kernels on this machine, looked for once and again when Select Kernel is opened
    private List<KernelSpec>? kernelSpecs;

    /// <summary>Runs cells, in order, once the kernel's up. Markdown and raw cells are only written out.</summary>
    private void RunCells(HexNotebook notebook, IEnumerable<NotebookCell> cells)
    {
        foreach (NotebookCell cell in cells)
        {
            if (cell.Kind != CellKind.Code)
                continue;

            if (notebook.Kernel is { State: not KernelState.Dead } kernel && !notebook.Starting)
            {
                notebook.Run.Sent(cell, kernel.Execute(cell.Source));
                NotebookChanged(notebook);
            }
            else if (!notebook.Waiting.Contains(cell))
            {
                notebook.Waiting.Add(cell);
            }
        }

        if (notebook.Waiting.Count > 0)
            EnsureKernel(notebook);
    }

    // Helper method to start the notebook's kernel if it hasn't one up or on its way
    private void EnsureKernel(HexNotebook notebook)
    {
        if (notebook.Starting || notebook.Kernel is { State: not KernelState.Dead })
            return;

        notebook.Starting = true;
        if (notebook.Spec is { } chosen)
        {
            StartKernel(notebook, chosen);
            return;
        }

        InBackground("kernels", KernelSpecs.Find, found =>
        {
            kernelSpecs = found;
            if (KernelSpecs.For(found, notebook.Model.KernelName, notebook.Model.Language) is { } spec)
            {
                StartKernel(notebook, spec);
                return;
            }

            notebook.Starting = false;
            notebook.Waiting.Clear();
            Notify(Said("notebook.no_kernel"), error: true);
        }, quiet: true);
    }

    private void StartKernel(HexNotebook notebook, KernelSpec spec)
    {
        notebook.Spec = spec;
        notebook.Starting = true;
        string folder = notebook.Document.Path is { } path ? Path.GetDirectoryName(Path.GetFullPath(path))! : Environment.CurrentDirectory;
        var kernel = new Kernel(spec, folder);
        notebook.Kernel = kernel;

        InBackground("kernel", () =>
        {
            try
            {
                kernel.StartAsync(KERNEL_PATIENCE).GetAwaiter().GetResult();
                return (string?)null;
            }
            catch (Exception e) when (e is IOException or TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
            {
                return e.Message;
            }
        }, failed =>
        {
            if (notebook.Kernel != kernel)
                return;

            notebook.Starting = false;
            if (failed is not null)
            {
                notebook.Kernel = null;
                notebook.Waiting.Clear();
                _ = kernel.DisposeAsync().AsTask();
                Notify(Said("notebook.kernel_failed", ("name", spec.DisplayName), ("why", failed)), error: true);
                return;
            }

            // The kernel it ran with written into it, as Jupyter does, only when it's another
            if (notebook.Model.KernelName != spec.Name)
            {
                notebook.Model.UseKernel(spec, kernel.Info);
                NotebookChanged(notebook);
            }

            var waiting = notebook.Waiting.ToList();
            notebook.Waiting.Clear();
            RunCells(notebook, waiting);
        }, quiet: true);
    }

    /// <summary>The kernel stopped and let go of, what was running and waiting forgotten.</summary>
    private void StopKernel(HexNotebook notebook)
    {
        Kernel? kernel = notebook.Kernel;
        notebook.Kernel = null;
        notebook.Starting = false;
        notebook.Run.Forget();
        notebook.Waiting.Clear();
        if (kernel is not null)
            _ = Task.Run(kernel.ShutdownAsync);
    }

    /// <summary>The kernel stopped and started again, what it knew gone, the counts from 1 again.</summary>
    private void RestartKernel(HexNotebook notebook, bool runAll = false)
    {
        KernelSpec? spec = notebook.Spec;
        StopKernel(notebook);
        if (runAll)
        {
            RunCells(notebook, notebook.Model.Cells);
            return;
        }

        if (spec is not null)
            StartKernel(notebook, spec);
        else
            EnsureKernel(notebook);
    }

    /// <summary>What's running stopped, the kernel and what it knows stay.</summary>
    private static void InterruptKernel(HexNotebook notebook)
    {
        notebook.Waiting.Clear();
        notebook.Kernel?.Interrupt();
    }

    /// <summary>A list of the kernels on this machine, the one picked used from now on (started again if one was up).</summary>
    private void PickKernel(HexNotebook notebook)
    {
        InBackground("kernels", KernelSpecs.Find, found =>
        {
            kernelSpecs = found;
            if (found.Count == 0)
            {
                Notify(Said("notebook.no_kernel"), error: true);
                return;
            }

            var choices = found.Select(spec => ($"{spec.DisplayName}   {spec.Name}", (Action)(() =>
            {
                bool wasUp = notebook.Kernel is not null;
                StopKernel(notebook);
                notebook.Spec = spec;
                notebook.Model.UseKernel(spec, []);
                NotebookChanged(notebook, now: true);
                if (wasUp)
                    StartKernel(notebook, spec);
            }))).ToList();
            ChooseFrom(Said("notebook.pick_kernel_title"), Said("notebook.pick_kernel_text"), choices);
        }, quiet: true);
    }

    // Every tick, what a notebook's kernel said taken into its cells
    private void TakeKernelEvents(HexNotebook notebook)
    {
        if (notebook.Kernel is not { } kernel)
            return;

        while (kernel.Events.TryDequeue(out KernelEvent? said))
        {
            if (said.Message.Type == "status" && said.Message.Content["execution_state"]?.GetValue<string>() == "dead")
            {
                if (notebook.Kernel == kernel)
                {
                    notebook.Kernel = null;
                    notebook.Run.Forget();
                    Say(Said("notebook.kernel_died", ("name", kernel.Spec.DisplayName)), error: true);
                }
                return;
            }

            if (notebook.Run.Take(said) is not null)
                NotebookChanged(notebook);
        }
    }

    /// <summary>What was typed into the box the code asked for, sent to the kernel and left in the cell's output.</summary>
    private void AnswerInput(HexNotebook notebook, string typed)
    {
        if (notebook.Run.Input is not { } asked || notebook.Kernel is not { } kernel)
            return;

        kernel.Answer(typed, asked.Request);
        notebook.Run.Answered();
        string shown = asked.Password ? new string('·', typed.Length) : typed;
        asked.Cell.Outputs.Add(new JsonObject { ["output_type"] = "stream", ["name"] = "stdout", ["text"] = asked.Prompt + shown + "\n" });
        NotebookChanged(notebook);
    }

    /* Completions and hovers, asked of the kernel */

    // What the kernel last offered, for which cell's text at which place, and what's being asked
    private (CodeEditor? Editor, int Version, UITextPosition At, UICompletionRequest? Answer) kernelCompletion;
    private (CodeEditor? Editor, int Version, UITextPosition At, string? Answer) kernelHover;

    // Where a place in a cell's text is as the kernel counts it, in Unicode characters from the start, not UTF-16 ones
    private static int CodePoints(string text, int index)
    {
        int count = 0;
        for (int i = 0; i < index && i < text.Length; i++)
        {
            if (!char.IsLowSurrogate(text[i]))
                count++;
        }

        return count;
    }

    // And back, the index in the text of a count of Unicode characters
    private static int IndexOfCodePoints(string text, int points)
    {
        int count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (count == points)
                return i;
            if (!char.IsLowSurrogate(text[i]))
                count++;
        }

        return text.Length;
    }

    private static int OffsetOf(CodeEditor editor, UITextPosition at)
    {
        int offset = 0;
        for (int line = 0; line < at.Line; line++)
            offset += editor.Document.LineAt(line).Length + 1;
        return offset + at.Column;
    }

    // Helper method for what a cell's editor offers at its caret, the kernel's answer once it's come
    private UICompletionRequest? KernelCompletions(HexNotebook notebook, CodeEditor editor)
    {
        if (notebook.Kernel is not { State: KernelState.Idle or KernelState.Busy } kernel)
            return null;

        UITextPosition caret = editor.Document.Caret;
        int version = editor.Document.Version;
        if (kernelCompletion.Editor == editor && kernelCompletion.Version == version && kernelCompletion.At == caret)
            return kernelCompletion.Answer ?? new UICompletionRequest(caret.Column, [], Said("notebook.asking"));

        kernelCompletion = (editor, version, caret, null);
        string code = editor.Text;
        int cursor = CodePoints(code, OffsetOf(editor, caret));
        InBackground("completions", () =>
        {
            try
            {
                using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                return kernel.RequestAsync(Kernel.SHELL, "complete_request", new JsonObject { ["code"] = code, ["cursor_pos"] = cursor }, patience.Token).GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is OperationCanceledException or IOException)
            {
                return null;
            }
        }, reply =>
        {
            if (kernelCompletion.Editor != editor || kernelCompletion.Version != version || kernelCompletion.At != caret)
                return;

            var items = new List<UICompletion>();
            int start = caret.Column;
            if (reply?.Content is { } content && content["status"]?.GetValue<string>() == "ok")
            {
                int from = IndexOfCodePoints(code, content["cursor_start"]?.GetValue<int>() ?? cursor);
                int lineStart = code.LastIndexOf('\n', Math.Max(0, from - 1)) + 1;
                start = Math.Clamp(from - (from > 0 && code[from - 1] == '\n' ? from : lineStart), 0, caret.Column);

                var kinds = new Dictionary<string, string>();
                if (content["metadata"]?["_jupyter_types_experimental"] is JsonArray typed)
                {
                    foreach (JsonNode? one in typed)
                    {
                        if (one?["text"]?.GetValue<string>() is { } text)
                            kinds[text] = one["type"]?.GetValue<string>() ?? string.Empty;
                    }
                }

                if (content["matches"] is JsonArray matches)
                {
                    foreach (JsonNode? match in matches)
                    {
                        if (match?.GetValue<string>() is { Length: > 0 } text)
                            items.Add(new UICompletion(text, kinds.GetValueOrDefault(text, string.Empty), Kind: kinds.GetValueOrDefault(text, string.Empty)));
                    }
                }
            }

            kernelCompletion = (editor, version, caret, new UICompletionRequest(start, items));
            if (editor.IsFocused)
                editor.Complete(typed: true);
        }, quiet: true);

        return new UICompletionRequest(caret.Column, [], Said("notebook.asking"));
    }

    // Helper method for what the kernel says about the name at a place, its signature and docstring, asked once a place
    private string? KernelHover(HexNotebook notebook, CodeEditor editor, UITextPosition at)
    {
        if (notebook.Kernel is not { State: KernelState.Idle } kernel)
            return null;

        int version = editor.Document.Version;
        if (kernelHover.Editor == editor && kernelHover.Version == version && kernelHover.At == at)
            return kernelHover.Answer;

        kernelHover = (editor, version, at, null);
        string code = editor.Text;
        int cursor = CodePoints(code, OffsetOf(editor, at) + 1);
        InBackground("hover", () =>
        {
            try
            {
                using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                return kernel.RequestAsync(Kernel.SHELL, "inspect_request", new JsonObject { ["code"] = code, ["cursor_pos"] = cursor, ["detail_level"] = 0 }, patience.Token).GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is OperationCanceledException or IOException)
            {
                return null;
            }
        }, reply =>
        {
            if (kernelHover.Editor != editor || kernelHover.Version != version || kernelHover.At != at)
                return;

            string said = reply?.Content["found"]?.GetValue<bool>() == true && reply.Content["data"]?["text/plain"] is { } text
                ? AnsiText.Read(NotebookFile.Text(text)).Text
                : string.Empty;
            if (said.Length > 2000)
                said = said[..2000] + "...";
            kernelHover = (editor, version, at, said.Length > 0 ? "```\n" + said.Trim() + "\n```" : string.Empty);
            editor.AskAgain();
        }, quiet: true);

        return null;
    }

    // The notebook's kernel as the bar says it, its name and where it is
    private string KernelStatus(HexNotebook notebook)
    {
        if (notebook.Spec is null && notebook.Kernel is null)
        {
            string? named = notebook.Model.Metadata["kernelspec"]?["display_name"]?.GetValue<string>();
            return named is null ? Said("notebook.kernel_none") : Said("notebook.kernel_off", ("name", named));
        }

        string name = (notebook.Kernel?.Spec ?? notebook.Spec)!.DisplayName;
        if (notebook.Starting)
            return Said("notebook.kernel_starting", ("name", name));
        return notebook.Kernel?.State switch
        {
            KernelState.Busy => Said("notebook.kernel_busy", ("name", name)),
            KernelState.Idle => Said("notebook.kernel_idle", ("name", name)),
            KernelState.Starting => Said("notebook.kernel_starting", ("name", name)),
            _ => Said("notebook.kernel_dead", ("name", name))
        };
    }
}
