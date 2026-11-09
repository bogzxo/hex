namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestWatchObject(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "watch object");
        string file = Path.Combine(root, "Program.cs");
        const string source = "class Box { public int Value = 7; }\nclass Program { static void Main() {\n    var box = new Box();\n    System.Console.WriteLine(box.Value);\n} }\n";
        UITreeItem? watch = null;
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "Probe.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, source);
            explorer.OpenFolder(root);
            Open(file);
            debugger.ToggleBreakpoint(file, 3);
            RunOrPlay();
        });
        test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == 3, 60f);
        test.Run(() => debugger.WatchExpression("box"));
        test.WaitFor(() => (watch = sideTree.Items.FirstOrDefault(section => section.Text == Said("debugging.watch"))?.Children?
            .FirstOrDefault(item => item.Text.StartsWith("box = ", StringComparison.Ordinal) && item.Expandable)) is not null, 10f);
        test.Check("Object Watch: native C# object is expandable", () => watch?.Expandable == true && watch.Tag is SideTag { Expanding: not null });
        test.Run(() => { if (watch is not null) sideTree.OnItemContextMenu?.Invoke(watch, System.Numerics.Vector2.Zero); });
        test.Check("Object Watch: right-click opens Edit/Remove and keeps the watch", () =>
            contextMenu?.Showing?.Items.Any(item => item.Label == Said("debugging.edit_watch")) == true
            && contextMenu.Showing.Items.Any(item => item.Label == Said("debugging.remove_watch"))
            && explorer.workspace.Watches.SequenceEqual(new[] { "box" }));
        test.Run(() =>
        {
            contextMenu?.Close();
            if (watch?.Tag is SideTag { Expanding: { } expand }) { watch.Expanded = true; expand(watch); }
        });
        test.WaitFor(() => watch?.Children?.Any(item => item.Text == "Value = 7") == true, 5f);
        test.Check("Object Watch: context menu preserves lazy native expansion", () => watch?.Children?.Any(item => item.Text == "Value = 7") == true);
        test.Run(() =>
        {
            if (watch is null) return;
            sideTree.OnItemContextMenu?.Invoke(watch, System.Numerics.Vector2.Zero);
            var edit = contextMenu?.Showing?.Items.FirstOrDefault(item => item.Label == Said("debugging.edit_watch"))?.OnPicked;
            contextMenu?.Close();
            edit?.Invoke();
        });
        test.WaitFor(() => textDialog is { IsOpen: true }, 3f);
        test.Run(() =>
        {
            if (textDialog is not { IsOpen: true } dialog) return;
            var input = Descendants(dialog.Body).OfType<TextBox>().First();
            input.Text = "box.Value";
            input.OnSubmitted?.Invoke(input.Text);
        });
        test.WaitFor(() => sideTree.Items.Any(section => section.Text == Said("debugging.watch") && section.Children?.Any(item => item.Text == "box.Value = 7") == true), 5f);
        test.Check("Object Watch: edited expression evaluates and persists", () => explorer.workspace.Watches.SequenceEqual(new[] { "box.Value" })
            && sideTree.Items.Any(section => section.Text == Said("debugging.watch") && section.Children?.Any(item => item.Text == "box.Value = 7") == true));
        test.Run(() =>
        {
            var row = sideTree.Items.FirstOrDefault(section => section.Text == Said("debugging.watch"))?.Children?.FirstOrDefault(item => item.Text == "box.Value = 7");
            if (row is null) return;
            sideTree.OnItemContextMenu?.Invoke(row, System.Numerics.Vector2.Zero);
            var remove = contextMenu?.Showing?.Items.FirstOrDefault(item => item.Label == Said("debugging.remove_watch"))?.OnPicked;
            contextMenu?.Close();
            remove?.Invoke();
        });
        test.Check("Object Watch: explicit removal persists", () => explorer.workspace.Watches.Count == 0);
        test.Run(() => debugger.EvaluateIntoImmediate("box.Value"));
        test.WaitFor(() => sideTree.Items.Any(section => section.Text == Said("debugging.immediate")
            && section.Children?.Any(item => item.Text == "box.Value = 7") == true), 5f);
        test.Run(() =>
        {
            var row = sideTree.Items.FirstOrDefault(section => section.Text == Said("debugging.immediate"))?.Children?.FirstOrDefault(item => item.Text == "box.Value = 7");
            if (row is null) return;
            sideTree.OnItemContextMenu?.Invoke(row, System.Numerics.Vector2.Zero);
            var add = contextMenu?.Showing?.Items.FirstOrDefault(item => item.Label == Said("debugging.add_to_watch"))?.OnPicked;
            contextMenu?.Close();
            add?.Invoke();
        });
        test.WaitFor(() => sideTree.Items.Any(section => section.Text == Said("debugging.watch")
            && section.Children?.Any(item => item.Text == "box.Value = 7") == true), 5f);
        test.Check("Immediate Watch: context-menu addition evaluates and persists", () => explorer.workspace.Watches.SequenceEqual(new[] { "box.Value" })
            && sideTree.Items.Any(section => section.Text == Said("debugging.watch") && section.Children?.Any(item => item.Text == "box.Value = 7") == true));
        test.Run(() => { debugger.StopDebugging(); debugger.breakpoints.Clear(); });
        test.WaitFor(() => !debugger.Debugging, 10f);
    }
}
