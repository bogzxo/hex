using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestCallHierarchy(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "call hierarchy"), file = Path.Combine(root, "calls.cpp");
        UITreeItem? Find(int direction, string name) => resultsTree?.Rows.ElementAtOrDefault(direction)?.Children?.FirstOrDefault(row => row.Text == name);
        void Ask() => AllCommands().First(command => command.Text.Contains(Said("code_server.call_hierarchy") + "   (", StringComparison.Ordinal)).Picked();
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(file, "void leaf() {}\nvoid middle() { leaf(); }\nvoid caller() { middle(); }\n");
            explorer.OpenFolder(root);
            Open(file);
        });
        test.WaitFor(() => languages.CanServe(document, "callHierarchyProvider"), 30);
        test.Check("Call hierarchy: native server capability is available", () => languages.CanServe(document, "callHierarchyProvider"));
        test.Run(() => { code.Document.Select(new(1, 7), new(1, 7)); Ask(); code.Document.Insert("X"); });
        test.Settle(1);
        test.Check("Call hierarchy: stale preparation cannot open results", () => resultsTree is null);
        test.Run(() => { code.Document.Undo(); code.Document.Select(new(1, 7), new(1, 7)); Ask(); });
        test.WaitFor(() => Find(0, "caller") is not null, 15);
        test.Check("Call hierarchy: native callers appear", () => Find(0, "caller") is not null);
        test.Run(() =>
        {
            if (resultsTree?.Rows.ElementAtOrDefault(1) is { } row) (row.Tag as SideTag)?.Expanding?.Invoke(row);
        });
        test.WaitFor(() => Find(1, "leaf") is not null, 15);
        test.Check("Call hierarchy: expanding outgoing calls resolves the callee", () => Find(1, "leaf") is not null);
        test.Run(() => (Find(0, "caller")?.Tag as SideTag)?.Pressed?.Invoke());
        test.Check("Call hierarchy: caller navigates to its call site", () => document.Path == file && code.Document.Caret.Line == 2);
        test.Run(() => (Find(1, "leaf")?.Tag as SideTag)?.Pressed?.Invoke());
        test.Check("Call hierarchy: callee navigates to the outgoing call site", () => document.Path == file && code.Document.Caret.Line == 1);
    }
}
