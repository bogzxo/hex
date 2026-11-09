namespace Hex.Dawn;

internal sealed partial class HexLanguages
{
    internal void TestEditRaces(HexPointerScript script, string folder)
    {
        string first = Path.Combine(folder, "First.cs"), second = Path.Combine(folder, "Second.cs");
        HexDocument a = null!, b = null!;
        EditSnapshot snapshot = null!;
        var edits = new Dictionary<string, IReadOnlyList<LanguageServerEdit>>
        {
            [first] = [new(0, 0, 0, 3, "new")],
            [second] = [new(0, 0, 0, 3, "new")]
        };
        var action = new LanguageServerAction("race fixture", false, edits, null);
        script.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(first, "old first"); File.WriteAllText(second, "old second");
            a = context.Documents.OpenBehind(first)!; b = context.Documents.OpenBehind(second)!;
            snapshot = CaptureEdits();
            context.Documents.Rewrite(b, "typed second");
            var response = new TaskCompletionSource<LanguageServerAction>();
            When(response.Task, result => TakeAction(null, result, snapshot, first));
            response.SetResult(action);
        });
        script.Settle(0.3f);
        script.Check("edit races: late multi-file action preserves newer typing and all other files", () =>
            a.Editor.Text == "old first" && b.Editor.Text == "typed second");
        script.Run(() =>
        {
            snapshot = CaptureEdits();
            EditWorkspaceChanged();
            TakeAction(null, action, snapshot, first);
        });
        script.Check("edit races: workspace replacement rejects an old response", () => a.Editor.Text == "old first");
        script.Run(() =>
        {
            snapshot = CaptureEdits();
            TakeAction(null, action, snapshot, first);
        });
        script.Check("edit races: unchanged snapshot permits both files", () =>
            a.Editor.Text == "new first" && b.Editor.Text == "newed second");
        script.Run(() =>
        {
            var missing = new Dictionary<string, IReadOnlyList<LanguageServerEdit>>(edits)
            { [first] = [new(0, 0, 0, 3, "bad")], [Path.Combine(folder, "missing.cs")] = [new(0, 0, 0, 0, "unexpected")] };
            ApplyEdits(missing);
        });
        script.Check("edit races: unavailable target prevents partial changes", () =>
            a.Editor.Text == "new first" && b.Editor.Text == "newed second");
        script.Check("edit races: edits never save files implicitly", () =>
            File.ReadAllText(first) == "old first" && File.ReadAllText(second) == "old second");
        TaskCompletionSource<LanguageServerAction?> resolving = null!;
        var resolvedAction = new LanguageServerAction("deferred fix", true,
            new Dictionary<string, IReadOnlyList<LanguageServerEdit>> { [first] = [new(0, 0, 0, 6, "fixed")] }, null);
        void BeginResolve()
        {
            resolving = new();
            var deferred = new LanguageServerAction("deferred fix", true, new Dictionary<string, IReadOnlyList<LanguageServerEdit>>(), null)
                { Resolve = () => resolving.Task };
            TakeAction(null, deferred, CaptureEdits(), first);
        }
        script.Run(BeginResolve);
        script.Check("deferred fix: selection waits for resolution before editing", () => a.Editor.Text == "new first");
        script.Run(() => { context.Documents.Rewrite(a, "typing first"); resolving.SetResult(resolvedAction); });
        script.Settle(0.3f);
        script.Check("deferred fix: typing during resolution rejects stale edits", () => a.Editor.Text == "typing first");
        script.Run(() => { BeginResolve(); resolving.SetResult(resolvedAction); });
        script.Settle(0.3f);
        script.Check("deferred fix: fresh resolution applies without saving", () => a.Editor.Text == "fixed first" && File.ReadAllText(first) == "old first");
        script.Run(() => a.Editor.Document.Undo());
        script.Check("deferred fix: resolved edits undo as one action", () => a.Editor.Text == "typing first");
        script.Run(() => { BeginResolve(); resolving.SetException(new IOException("resolution failed")); });
        script.Settle(0.3f);
        script.Check("deferred fix: a failed resolution leaves the document untouched", () => a.Editor.Text == "typing first");
    }
}
