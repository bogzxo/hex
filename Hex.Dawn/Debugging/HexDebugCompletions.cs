namespace Hex.Dawn;

/// <summary>The expression editor's current native completion list, independent of source-file language services.</summary>
internal sealed class HexDebugCompletions(Func<string, int, Task<IReadOnlyList<DebugCompletion>?>> ask, Func<bool> current,
    Action<Action> post, string waiting)
{
    private (int Version, UITextPosition Caret)? askedFor;
    private UICompletionRequest? shown;
    private long request;

    internal UICompletionRequest? Complete(CodeEditor editor)
    {
        if (!current()) return null;
        var document = editor.Document;
        var key = (document.Version, document.Caret);
        if (askedFor == key) return shown;
        askedFor = key;
        long number = ++request;
        var caret = document.Caret;
        int version = document.Version, start = caret.Column - document.WordBeforeCaret().Length;
        string line = document.LineAt(caret.Line);
        shown = new(start, [], waiting);
        var weak = new WeakReference<CodeEditor>(editor);
        _ = Load();
        return shown;

        async Task Load()
        {
            IReadOnlyList<DebugCompletion>? found;
            try { found = await ask(line, caret.Column).ConfigureAwait(false); }
            catch (Exception) { found = null; }
            post(() =>
            {
                if (number != request || !current() || !weak.TryGetTarget(out var target)
                    || target.Document.Version != version || target.Document.Caret != caret) return;
                var items = found?.Where(item => item.Start <= caret.Column && item.Start + item.Length >= caret.Column)
                    .Select(item => new UICompletion(item.Label, item.Detail, item.Text, Kind: item.Kind, ExpectedVersion: version,
                        ReplacementStart: new(caret.Line, item.Start), ReplacementEnd: new(caret.Line, item.Start + item.Length),
                        SelectionStart: item.SelectionStart, SelectionLength: item.SelectionLength)).ToArray() ?? [];
                shown = new(start, items);
                if (target.CompletionOpen) target.Complete(target.CompletionTyped);
            });
        }
    }
}
