using System.Collections.Concurrent;
using System.Text;

using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

// A notebook's code read by its language's server the way a file of it is, which a notebook's cells never were: each is
// an editor of its own and the .ipynb is JSON, so pyright never heard of a cell and nothing was underlined, an import
// that isn't there or a name never made. The code cells are put together into one file the server is told of and that's
// never written, beside the notebook and named after it (analysis.ipynb's is analysis.ipynb.py), the way the cells run
// one after the other in the kernel. Jupyter's own lines (%pip, !ls) aren't the language's and go as blank lines, the
// lines of every cell keeping their place. What the server says comes back to the cell it's in, a line of the file being
// a line of a cell; what it completes at a cell's caret is asked at the same place in the file.
internal sealed partial class HexLanguages
{
    private sealed class NotebookShadow
    {
        public required string Path;
        public required LanguageServer Server;
        public required string Extension;

        // Where each cell starts in the file, by its index among the notebook's cells, -1 for one that isn't code
        public int[] Starts = [];
        public int[] Heights = [];
        public string Text = string.Empty;
        public bool Opened;
        public float SendIn = -1.0f;

        // What the server said, by the file's lines, and how many times it has
        public IReadOnlyList<UICodeDiagnostic> Found = [];
        public int Said;

        // What was last asked to complete, and what came of it
        public int Generation;
        public (CodeEditor Editor, int Cell, int Line, int Start, int Version)? Asked;
        public UICompletionRequest? Offered;
        public bool Answered;
    }

    private readonly Dictionary<object, NotebookShadow> notebookShadows = [];
    private readonly ConcurrentQueue<(NotebookShadow Shadow, int Generation, LanguageServerCompletions? Found)> shadowAnswers = new();

    /// <summary>A notebook's cells were said again by the server, for whoever shows them to put it under the code.</summary>
    internal Action<object>? NotebookSaid;

    /// <summary>
    /// A notebook's cells, as they are now, for its language's server: opened in it the first time, sent again a moment
    /// after they're typed into. <paramref name="extension"/> is the language's (".py"), nothing happens for a language
    /// with no server, or one whose server isn't on this machine.
    /// </summary>
    internal void NotebookChanged(object key, string notebookPath, string extension, IReadOnlyList<(string Source, bool Code)> cells, bool now = false)
    {
        if (!serversRead)
            ReadLanguageServers();

        string path = notebookPath + extension;
        if (!notebookShadows.TryGetValue(key, out NotebookShadow? shadow) || !PlatformPaths.Same(shadow.Path, path))
        {
            if (shadow is not null)
                NotebookClosed(key);
            // The same server a file of the language beside it gets, with the Python environment picked for the folder
            string root = ServerRootFor(notebookPath);
            if (LanguageServers.RegistrationFor(path) is not { } registration
                || LanguageServers.For(path, root, null, null, PythonServerSettings(registration, path, root)) is not { } server)
            {
                SayIfMissing(path);
                return;
            }

            server.Woken ??= WakeForLinting;
            notebookShadows[key] = shadow = new NotebookShadow { Path = path, Server = server, Extension = extension };
            context.Workspace.Rewatch();
            Console.WriteLine($"[Hex] {Path.GetFileName(notebookPath)}'s cells are linted by {server.Name}.");
        }

        var text = new StringBuilder();
        int line = 0;
        shadow.Starts = new int[cells.Count];
        shadow.Heights = new int[cells.Count];
        for (int i = 0; i < cells.Count; i++)
        {
            if (!cells[i].Code)
            {
                shadow.Starts[i] = -1;
                continue;
            }

            string[] lines = cells[i].Source.ReplaceLineEndings("\n").Split('\n');
            shadow.Starts[i] = line;
            shadow.Heights[i] = lines.Length;
            foreach (string each in lines)
                text.Append(Unmagic(each)).Append('\n');
            line += lines.Length;
        }

        string made = text.ToString();
        if (made == shadow.Text && shadow.Opened)
            return;
        shadow.Text = made;
        if (!shadow.Opened)
        {
            shadow.Opened = true;
            shadow.Server.Open(shadow.Path, made);
        }
        else
        {
            shadow.SendIn = now ? 0.0f : LINT_DELAY;
        }
    }

    // A line Jupyter runs that isn't the language's (%pip install, %%time, !ls) as a line of nothing, its indent kept so a
    // block it's in stays one
    private static string Unmagic(string line)
    {
        string trimmed = line.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '%' && trimmed[0] != '!'))
            return line;
        return line[..(line.Length - trimmed.Length)] + "pass";
    }

    /// <summary>A notebook closed, its server forgets the file of its cells.</summary>
    internal void NotebookClosed(object key)
    {
        if (!notebookShadows.Remove(key, out NotebookShadow? shadow))
            return;
        if (shadow.Opened)
        {
            shadow.Server.Close(shadow.Path);
            shadow.Server.ForgetSource(shadow.Path);
        }
    }

    /// <summary>What the server says is wrong in one cell, its lines the cell's own, nothing for a notebook it hasn't heard of.</summary>
    internal IReadOnlyList<UICodeDiagnostic> NotebookDiagnostics(object key, int cell)
    {
        if (!notebookShadows.TryGetValue(key, out NotebookShadow? shadow) || cell < 0 || cell >= shadow.Starts.Length || shadow.Starts[cell] < 0)
            return [];

        int start = shadow.Starts[cell], end = start + shadow.Heights[cell];
        int last = end - 1;
        var mine = new List<UICodeDiagnostic>();
        foreach (UICodeDiagnostic found in shadow.Found)
        {
            if (found.Line < start || found.Line >= end)
                continue;
            // A cell's last line on its own is what the cell shows when it runs, not a value thrown away
            // (pyright's reportUnusedExpression, "Expression value is unused")
            if (found.Line == last && found.Message.Contains("Expression value is unused", StringComparison.OrdinalIgnoreCase))
                continue;
            mine.Add(found with { Line = found.Line - start, RelatedLocations = null });
        }

        return mine;
    }

    /// <summary>Whether a notebook's cells are read by a server, for its completions to be asked of it rather than the kernel.</summary>
    internal bool NotebookServed(object key) => notebookShadows.TryGetValue(key, out NotebookShadow? shadow) && shadow.Server.Failure is null;

    /// <summary>
    /// What the server offers at a cell's caret, asked at the same place in the file of the cells, the list opening by
    /// itself once it answers. Null for a notebook without a server.
    /// </summary>
    internal UICompletionRequest? NotebookCompletions(object key, int cell, CodeEditor editor)
    {
        if (!notebookShadows.TryGetValue(key, out NotebookShadow? shadow) || shadow.Server.Failure is not null
            || cell < 0 || cell >= shadow.Starts.Length || shadow.Starts[cell] < 0)
            return null;

        UITextDocument doc = editor.Document;
        UITextPosition caret = doc.Caret;
        int start = caret.Column - doc.WordBeforeCaret().Length;
        string line = doc.LineAt(caret.Line);
        char? trigger = editor.CompletionTyped && caret.Column > 0 && shadow.Server.TriggersCompletion(line[caret.Column - 1]) ? line[caret.Column - 1] : null;
        if (editor.CompletionTyped && caret.Column == start && trigger is null)
            return null;

        var asked = (editor, cell, caret.Line, start, doc.Version);
        if (shadow.Asked is { } was && was.Editor == editor && was.Cell == cell && was.Line == caret.Line && was.Start == start)
        {
            if (!shadow.Answered)
                return new UICompletionRequest(start, [], context.Strings.Said("languages.completion_waiting", ("server", shadow.Server.Name)));
            if (shadow.Offered is { } offered && (was.Version == doc.Version || editor.CompletionTyped))
                return offered;
        }

        // What's typed has to be in the file first, which would otherwise go a moment after the typing stops
        if (shadow.SendIn >= 0.0f)
        {
            shadow.SendIn = -1.0f;
            shadow.Server.Change(shadow.Path, shadow.Text);
        }

        shadow.Asked = asked;
        shadow.Answered = false;
        shadow.Offered = null;
        int generation = ++shadow.Generation;
        int offset = shadow.Starts[cell];
        shadow.Server.Complete(shadow.Path, offset + caret.Line, caret.Column, start, trigger).ContinueWith(answered =>
        {
            shadowAnswers.Enqueue((shadow, generation, answered.IsCompletedSuccessfully ? answered.Result : null));
            context.Wake();
        }, TaskScheduler.Default);

        return new UICompletionRequest(start, [], context.Strings.Said("languages.completion_waiting", ("server", shadow.Server.Name)));
    }

    // Every tick, a notebook's cells that are due go to its server, and what the servers said of them comes back
    private void UpdateNotebooks(float dt)
    {
        foreach (NotebookShadow shadow in notebookShadows.Values)
        {
            if (shadow.SendIn < 0.0f || (shadow.SendIn -= dt) > 0.0f)
                continue;
            shadow.SendIn = -1.0f;
            shadow.Server.Change(shadow.Path, shadow.Text);
        }

        while (shadowAnswers.TryDequeue(out var answer))
        {
            NotebookShadow shadow = answer.Shadow;
            if (shadow.Generation != answer.Generation || shadow.Asked is not { } asked)
                continue;

            int offset = shadow.Starts.ElementAtOrDefault(asked.Cell);
            shadow.Answered = true;
            // Auto-imports and the like are edits of the file of the cells, not of this cell, they're left out. A place to
            // replace from is the cell's own line
            shadow.Offered = answer.Found is { } found ? new UICompletionRequest(asked.Start, [.. found.Items.Select(item => item with
            {
                AdditionalEdits = null,
                ReplacementStart = item.ReplacementStart is { } first ? first with { Line = first.Line - offset } : null,
                ReplacementEnd = item.ReplacementEnd is { } last ? last with { Line = last.Line - offset } : null
            })]) : null;

            CodeEditor editor = asked.Editor;
            UITextPosition caret = editor.Document.Caret;
            if (editor.IsFocused && caret.Line == asked.Line && caret.Column - editor.Document.WordBeforeCaret().Length == asked.Start)
                editor.Complete(typed: true);
        }
    }

    // What a server said of a file that's a notebook's cells, kept for its cells. False for a file that's none
    private bool NotebookFound(LanguageServer server, string path, IReadOnlyList<UICodeDiagnostic> found)
    {
        foreach (var (key, shadow) in notebookShadows)
        {
            if (!ReferenceEquals(shadow.Server, server) || !PlatformPaths.Same(shadow.Path, path))
                continue;
            shadow.Found = found;
            shadow.Said++;
            NotebookSaid?.Invoke(key);
            return true;
        }

        return false;
    }
}
