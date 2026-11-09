namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestDefinitionResults(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "definition results"), file = Path.Combine(root, "Program.cs");
        string first = Path.Combine(root, "Model.A.cs"), second = Path.Combine(root, "Model.B.cs");
        string local = Path.Combine(root, "LocalTarget.cs"), notes = Path.Combine(root, "notes.txt");
        const string source = "using System;\nclass Program {\n    static void Main() {\n        var item = new Model();\n        Version version = new Version(1, 2);\n        _ = item;\n    }\n}\n";
        UITreeItem? Row(string name) => ResultsTreeShown ? resultsTree?.Rows.FirstOrDefault(row => row.Text == name)?.Children?.FirstOrDefault() : null;
        void Press(UITreeItem? row) => (row?.Tag as SideTag)?.Pressed?.Invoke();
        List<UITreeItem>? previous = null;
        UITreeItem? metadata = null, disk = null;
        WeakReference? origin = null;
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "Probe.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, source);
            File.WriteAllText(first, "partial class Model { public int A; }\n");
            File.WriteAllText(second, "partial class Model { public int B; }\n");
            File.WriteAllText(local, "class LocalTarget {}\n");
            File.WriteAllText(notes, "Notes without a language server\n");
            explorer.OpenFolder(root);
            Open(file);
        });
        test.WaitFor(() => languages.CanServe(document, "definitionProvider"), 60);
        test.Run(() => { code.GoTo(3, code.Document.LineAt(3).IndexOf("Model", StringComparison.Ordinal) + 1); languages.GoToDefinition(document); });
        test.WaitFor(() => Row("Model.A.cs") is not null && Row("Model.B.cs") is not null, 20);
        test.Check("Definition results: native C# partial declarations produce two readable destinations", () => document.Path == file
            && Row("Model.A.cs")?.Text.Contains("partial class Model", StringComparison.Ordinal) == true
            && Row("Model.B.cs")?.Text.Contains("partial class Model", StringComparison.Ordinal) == true);
        test.Run(() => Press(Row("Model.B.cs")));
        test.Check("Definition results: choosing a declaration opens its native location", () => document.Path == second
            && code.Document.Caret == new UITextPosition(0, 14));
        test.Run(() =>
        {
            Open(file);
            code.GoTo(5, code.Document.LineAt(5).IndexOf("item", StringComparison.Ordinal) + 1);
            previous = resultsTree?.Rows;
            AllCommands().First(command => command.Text.EndsWith(Said("code_server.go_to_type_definition"), StringComparison.Ordinal)).Picked();
        });
        test.WaitFor(() => resultsTree?.Rows != previous && Row("Model.A.cs") is not null && Row("Model.B.cs") is not null, 20);
        test.Check("Definition results: native type definitions use the same destination tree", () => resultsTree?.Rows != previous
            && Row("Model.A.cs") is not null && Row("Model.B.cs") is not null);
        test.Run(() => Press(Row("Model.A.cs")));
        test.Check("Definition results: choosing a type preserves its native selection range", () => document.Path == first
            && code.Document.Caret == new UITextPosition(0, 14));
        test.Run(() =>
        {
            Open(file);
            code.GoTo(4, code.Document.LineAt(4).IndexOf("Version", StringComparison.Ordinal) + 1);
            languages.ShowMixedDefinitionsForTest(document, local);
        });
        test.WaitFor(() => Row("System.Version.cs") is not null && Row("LocalTarget.cs") is not null, 20);
        test.Check("Definition results: mixed project and native metadata destinations remain selectable", () => Row("System.Version.cs") is not null
            && Row("LocalTarget.cs") is not null && document.Path == file);
        test.Run(() => { metadata = Row("System.Version.cs"); disk = Row("LocalTarget.cs"); Press(disk); });
        test.Check("Definition results: the project destination in a mixed result opens normally", () => document.Path == local
            && code.Document.Caret == new UITextPosition(0, 6));
        test.Run(() => { Open(notes); Press(metadata); });
        test.WaitFor(() => document.Path is {} path && PlatformPaths.IsUnder(path, HexLanguages.DecompiledFolder), 15);
        test.Check("Definition results: a metadata choice from another document opens native read-only source", () => code.ReadOnly
            && code.Text.Contains("class Version", StringComparison.Ordinal) && languages.CanServe(document, "definitionProvider"));
        test.Run(() => { Open(file); origin = new WeakReference(document); CloseNow(); Open(notes); });
        test.Settle(1);
        test.Run(() => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); });
        test.Check("Definition results: retained result rows do not retain their closed source document", () => ResultsTreeShown && origin?.IsAlive == false);
        test.Run(() => { Open(notes); Press(metadata); code.GoTo(0, 4); });
        test.Settle(0.5f);
        test.Check("Definition results: moving the caret rejects a pending metadata choice", () => document.Path == notes
            && code.Document.Caret == new UITextPosition(0, 4));
        test.Run(() =>
        {
            string other = Path.Combine(testFolder, "replacement definition workspace");
            Directory.CreateDirectory(other);
            string otherFile = Path.Combine(other, "other.txt");
            File.WriteAllText(otherFile, "Other workspace\n");
            explorer.OpenFolder(other);
            Open(otherFile);
            Press(metadata);
            Press(disk);
        });
        test.Settle(0.5f);
        test.Check("Definition results: old project and metadata rows cannot enter a replaced workspace", () => document.Path?.EndsWith("other.txt", StringComparison.Ordinal) == true);
    }

    private void TestUnionDefinitionResults(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "union definition results"), file = Path.Combine(root, "main.py");
        const string source = "class First: pass\nclass Second: pass\ndef read(item: First | Second):\n    return item\n";
        HexDocument? original = null;
        UITreeItem? Group() => ResultsTreeShown ? resultsTree?.Rows.FirstOrDefault(row => row.Text == "main.py") : null;
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(file, source);
            explorer.OpenFolder(root);
            Open(file);
            original = document;
            code.Document.MoveToEnd(false);
            code.Document.Insert("# unsaved union navigation\n");
        });
        test.WaitFor(() => languages.CanServe(document, "typeDefinitionProvider"), 45);
        test.Check("Definition results: native Python union type navigation is available", () => languages.CanServe(document, "typeDefinitionProvider"));
        test.Run(() =>
        {
            code.GoTo(3, 12);
            AllCommands().First(command => command.Text.EndsWith(Said("code_server.go_to_type_definition"), StringComparison.Ordinal)).Picked();
        });
        test.WaitFor(() => Group()?.Children?.Count == 2, 15);
        test.Check("Definition results: Python union destinations are grouped within one file", () => Group() is { Detail: "2", Children: { Count: 2 } children }
            && children[0].Text == "class First: pass" && children[1].Text == "class Second: pass");
        test.Run(() => { if (Group()?.Children?.ElementAtOrDefault(1) is {} row) sideTree.OnActivated?.Invoke(row); });
        test.Check("Definition results: activating the second union type navigates to its exact declaration", () => document == original
            && code.Document.Caret == new UITextPosition(1, 6));
        test.Check("Definition results: server path casing reuses the tab and preserves unsaved text", () => document == original
            && documents.Count(open => open.Path is {} path && PlatformPaths.Same(path, file)) == 1
            && code.Text == source + "# unsaved union navigation\n" && File.ReadAllText(file) == source);
        test.Run(() => Open(file));
        test.Check("Definition results: opening the project path also reuses the unsaved source tab", () => document == original
            && code.Text == source + "# unsaved union navigation\n");
    }
}
