using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestTypeHierarchy(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "type hierarchy"), file = Path.Combine(root, "types.cpp");
        UITreeItem? Find(int direction, string name) => resultsTree?.Rows.ElementAtOrDefault(direction)?.Children?.FirstOrDefault(row => row.Text == name);
        void Ask() => AllCommands().First(command => command.Text.EndsWith(Said("code_server.type_hierarchy"), StringComparison.Ordinal)).Picked();
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(file, "struct Ancestor {};\nstruct Base : Ancestor {};\nstruct Middle : Base {};\nstruct Leaf : Middle {};\n");
            explorer.OpenFolder(root);
            Open(file);
        });
        test.WaitFor(() => languages.CanServe(document, "typeHierarchyProvider"), 30);
        test.Check("Type hierarchy: native server capability is available", () => languages.CanServe(document, "typeHierarchyProvider"));
        test.Run(() => { code.Document.Select(new(2, 9), new(2, 9)); Ask(); code.Document.Insert("X"); });
        test.Settle(1);
        test.Check("Type hierarchy: stale preparation does not open a tree", () => resultsTree is null);
        test.Run(() => { code.Document.Undo(); code.Document.Select(new(2, 9), new(2, 9)); Ask(); });
        test.WaitFor(() => Find(0, "Base") is not null && Find(1, "Leaf") is not null, 15);
        test.Check("Type hierarchy: native base and derived rows appear", () => Find(0, "Base") is not null && Find(1, "Leaf") is not null);
        test.Check("Type hierarchy: further ancestors load lazily", () => Find(0, "Base") is { Children: null, Expandable: true });
        test.Run(() => { if (Find(0, "Base") is { } row) (row.Tag as SideTag)?.Expanding?.Invoke(row); });
        test.WaitFor(() => Find(0, "Base")?.Children?.Any(row => row.Text == "Ancestor") == true, 15);
        test.Check("Type hierarchy: expansion resolves the next ancestor", () => Find(0, "Base")?.Children?.Any(row => row.Text == "Ancestor") == true);
        test.Run(() => (Find(1, "Leaf")?.Tag as SideTag)?.Pressed?.Invoke());
        test.Check("Type hierarchy: selecting a type opens its declaration", () => document.Path == file && code.Document.Caret == new UITextPosition(3, 7));
        string otherRoot = Path.Combine(testFolder, "another hierarchy workspace"), otherFile = Path.Combine(otherRoot, "notes.txt");
        test.Run(() =>
        {
            var oldRow = Find(0, "Base");
            Directory.CreateDirectory(otherRoot);
            File.WriteAllText(otherFile, "New workspace");
            explorer.OpenFolder(otherRoot);
            Open(otherFile);
            (oldRow?.Tag as SideTag)?.Pressed?.Invoke();
        });
        test.Check("Type hierarchy: old rows cannot navigate into a replaced workspace", () => document.Path == otherFile);
    }
}
