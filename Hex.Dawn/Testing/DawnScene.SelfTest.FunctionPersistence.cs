namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestFunctionBreakpointPersistence(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "function persistence");
        string first = Path.Combine(root, "first"), second = Path.Combine(root, "second"), store = Path.Combine(root, "store");
        string firstFile = Path.Combine(first, "First.cs"), secondFile = Path.Combine(second, "Second.cs");
        IEnumerable<UITreeItem> Rows() => sideTree.Items.FirstOrDefault(section => section.Text == Said("debugging.breakpoints"))?.Children ?? [];
        void OpenWorkspace(string folder, string file) { explorer.OpenFolder(folder); Open(file); Context.Panels.ShowSide(HexSide.Debug); }
        test.Run(() =>
        {
            Directory.CreateDirectory(first); Directory.CreateDirectory(second);
            File.WriteAllText(firstFile, "class First {}\n"); File.WriteAllText(secondFile, "class Second {}\n");
            OpenWorkspace(first, firstFile);
            debugger.UseTestBreakpointStore(store);
            debugger.SetFunctionBreakpoint("First.Target", true);
            debugger.SetFunctionBreakpoint("First.Disabled", false);
            debugger.ToggleBreakpoint(firstFile, 0);
        });
        test.Check("Function persistence: mixed source and function settings are saved", () => Directory.GetFiles(store, "*.json").Length == 1);
        test.Run(() => OpenWorkspace(second, secondFile));
        test.Check("Function persistence: another workspace starts without the previous functions", () => !Rows().Any(row => row.Text.StartsWith("First.", StringComparison.Ordinal)) && code.Breakpoints is { Count: 0 });
        test.Run(() => { debugger.SetFunctionBreakpoint("Second.Target", true); debugger.ToggleBreakpoint(secondFile, 0); });
        test.Run(() => OpenWorkspace(first, firstFile));
        test.Check("Function persistence: returning restores the enabled function", () => Rows().Any(row => row.Text == "First.Target") && !Rows().Any(row => row.Text == "Second.Target"));
        test.Check("Function persistence: disabled state and source breakpoint survive reload", () => Rows().Any(row => row.Text == "First.Disabled" && row.Icon == "circle") && code.Breakpoints?.Contains(0) == true);
        test.Run(() => OpenWorkspace(second, secondFile));
        test.Check("Function persistence: loading the first workspace did not overwrite the second", () => Rows().Any(row => row.Text == "Second.Target") && !Rows().Any(row => row.Text.StartsWith("First.", StringComparison.Ordinal)) && code.Breakpoints?.Contains(0) == true);
        test.Run(() =>
        {
            var section = sideTree.Items.First(item => item.Text == Said("debugging.breakpoints"));
            sideTree.OnItemContextMenu?.Invoke(section, System.Numerics.Vector2.Zero);
            var remove = contextMenu?.Showing?.Items.FirstOrDefault(item => item.Label == Said("debugging.delete_all"))?.OnPicked;
            contextMenu?.Close(); remove?.Invoke();
        });
        test.Run(() => { OpenWorkspace(first, firstFile); OpenWorkspace(second, secondFile); });
        test.Check("Function persistence: Delete All removes saved source and function breakpoints", () => !Rows().Any(row => row.Text == "Second.Target") && code.Breakpoints is { Count: 0 });
        test.Run(() => debugger.UseTestBreakpointStore(null));
    }
}
