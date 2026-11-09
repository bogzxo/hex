namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestVariablePages(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "variable pages"), file = Path.Combine(root, "main.py");
        UITreeItem? collection = null;
        bool Matches(UITreeItem row, string value)
        {
            int split = row.Text.IndexOf(" = ", StringComparison.Ordinal), wanted = value.IndexOf(" = ", StringComparison.Ordinal);
            return split > 0 && int.TryParse(row.Text[..split], out int index) && int.TryParse(value[..wanted], out int expected)
                && index == expected && row.Text[(split + 3)..] == value[(wanted + 3)..];
        }
        IEnumerable<UITreeItem> Rows(UITreeItem? item) => item?.Children?.SelectMany(row => new[] { row }.Concat(Rows(row))) ?? [];
        bool Has(string value) => Rows(collection).Any(row => Matches(row, value));
        UITreeItem? More() => Rows(collection).FirstOrDefault(row => row.Text == Said("debugging.load_more_variables") || row.Text.StartsWith("more = ", StringComparison.Ordinal) && row.Children is null);
        void LoadMore()
        {
            var row = More();
            if (row?.Tag is not SideTag tag) return;
            if (tag.Pressed is {} pressed) pressed();
            else { row.Expanded = true; tag.Expanding?.Invoke(row); }
        }
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(file, "values = list(range(250))\nprint(len(values))\n");
            explorer.OpenFolder(root);
            Open(file);
            debugger.ToggleBreakpoint(file, 1);
            RunOrPlay();
        });
        test.WaitFor(() => debugger.DebugStopped && sideTree.Items.Any(section => section.Text == "Variables" && section.Children?.Any(row => row.Text.StartsWith("values = ", StringComparison.Ordinal)) == true), 30f);
        test.Run(() =>
        {
            collection = sideTree.Items.First(section => section.Text == "Variables").Children!.First(row => row.Text.StartsWith("values = ", StringComparison.Ordinal));
            collection.Expanded = true;
            (collection.Tag as SideTag)?.Expanding?.Invoke(collection);
        });
        test.WaitFor(() => Has("99 = 99") && More() is not null, 10f);
        test.Check("Variable pages: first hundred elements are loaded", () => Has("0 = 0") && Has("99 = 99") && !Has("100 = 100") && More() is not null);
        test.Run(LoadMore);
        test.WaitFor(() => Has("199 = 199"), 10f);
        test.Check("Variable pages: next page appends without duplicates", () => Has("100 = 100") && Has("199 = 199") && Rows(collection).Count(row => Matches(row, "0 = 0")) == 1);
        test.Run(LoadMore);
        test.WaitFor(() => Has("249 = 249"), 10f);
        test.Check("Variable pages: final partial page removes Load More", () => Has("200 = 200") && Has("249 = 249") && More() is null);
        test.Run(debugger.StopDebugging);
        test.WaitFor(() => !debugger.Debugging, 10f);
        test.Run(() => debugger.breakpoints.Clear());
    }
}
