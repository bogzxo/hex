namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestCompletionDocumentationRace(HexPointerScript test)
    {
        var first = new TaskCompletionSource<UICompletionDocumentation?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<UICompletionDocumentation?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CodeEditor, UICompletionRequest?>? prior = null;
        int firstRequests = 0, secondRequests = 0;
        test.Run(() =>
        {
            string path = Path.Combine(testFolder, "completion documentation race.txt");
            File.WriteAllText(path, "");
            Open(path);
            prior = code.CompletionSource;
            code.CompletionSource = _ => new(0,
            [
                new("First", Insert: "first", ResolveDocumentation: () => { firstRequests++; return first.Task; }),
                new("Second", Insert: "second", ResolveDocumentation: () => { secondRequests++; return second.Task; })
            ]);
            code.Focus();
            code.Complete();
        });
        test.WaitFor(() => firstRequests == 1, 5);
        test.Check("Completion docs: only the highlighted item is requested", () => firstRequests == 1 && secondRequests == 0);
        test.Run(() => code.MoveCompletion(1));
        test.WaitFor(() => secondRequests == 1, 5);
        test.Run(() => second.SetResult(new("second detail", "second documentation")));
        test.WaitFor(() => code.Completions.Count == 2 && code.Completions[1].Documentation == "second documentation", 5);
        test.Check("Completion docs: changing selection resolves the new item", () => code.Completions.Count == 2 && code.Completions[1].Documentation == "second documentation");
        test.Run(() => first.SetResult(new("first detail", "late first documentation")));
        test.Settle(0.1f);
        test.Check("Completion docs: late response cannot overwrite the current selection", () => code.Completions.Count == 2
            && code.Completions[1].Documentation == "second documentation" && code.Completions[0].Documentation == "");
        test.Run(() => code.AcceptCompletion());
        test.Check("Completion docs: acceptance uses the selected item's original insertion", () => code.Text == "second");
        test.Run(() =>
        {
            code.Document.Undo();
            code.CompletionSource = _ => new(0, [new("Failure", Insert: "still usable", ResolveDocumentation: () => throw new IOException("fixture resolution failure"))]);
            code.Complete();
        });
        test.Settle(0.1f);
        test.Run(() => code.AcceptCompletion());
        test.Check("Completion docs: resolution failure does not prevent accepting an item", () => code.Text == "still usable");
        test.Run(() => { code.CloseCompletion(); code.CompletionSource = prior; });
    }

    private void TestCompletionDocumentation(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "completion documentation");
        string file = Path.Combine(root, "Program.cs");
        const string original = "using Tools; class Program { static void Main() { HexDocu value = new(); } }\n";
        const string docs = "Creates the fixture answer.";
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "Probe.csproj"), CSHARP_PROJECT);
            File.WriteAllText(Path.Combine(root, "Library.cs"), "namespace Tools {\n/// <summary>" + docs + "</summary>\npublic class HexDocumentedItem {} }\n");
            File.WriteAllText(file, original);
            explorer.OpenFolder(root);
            Open(file);
        });
        test.WaitFor(() => code.Diagnostics.Any(found => found.Message.Contains("CS0246")), 90);
        test.Run(() =>
        {
            code.Focus();
            code.Document.MoveTo(new(0, original.IndexOf("HexDocu", StringComparison.Ordinal) + 7));
            code.Complete();
        });
        test.WaitFor(() => code.CompletionOpen && code.Completions.Any(item => item.Documentation == docs), 20);
        test.Check("C# completion: highlighted symbol resolves XML documentation", () => code.CompletionOpen
            && code.Completions.Any(item => item.Text == "HexDocumentedItem" && item.Documentation == docs));
        test.Check("C# completion: resolved detail includes its namespace", () =>
            code.Completions.Any(item => item.Text == "HexDocumentedItem" && item.Detail.Contains("Tools")));
        test.Check("C# completion: reading documentation does not edit or save the buffer", () => code.Text == original && File.ReadAllText(file) == original);
        test.Run(() => code.AcceptCompletion());
        test.Check("C# completion: accepting the resolved item preserves its insertion", () => code.Text == original.Replace("HexDocu value", "HexDocumentedItem value", StringComparison.Ordinal));
        test.WaitFor(() => languages.ServerDiagnosticsOf(document) is { Count: 0 }, 20);
        test.Check("C# completion: resolved symbol validates with the native server", () => languages.ServerDiagnosticsOf(document) is { Count: 0 });
        test.Run(() => code.Document.Undo());
        test.Check("C# completion: undo restores the original buffer", () => code.Text == original);
    }
}
