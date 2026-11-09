namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestWatchPersistence(HexPointerScript test)
    {
        string first = Path.Combine(testFolder, "watch workspace first"), second = Path.Combine(testFolder, "watch workspace second");
        string saved = Path.Combine(testFolder, "watch-test.hex-workspace");
        bool Shown(string expression) => sideTree.Items.Any(section => section.Text == Said("debugging.watch") && section.Children?.Any(row => row.Text == expression) == true);
        test.Run(() =>
        {
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            explorer.OpenFolder(first);
            debugger.WatchExpression("value + 1");
            debugger.WatchExpression("items[0].Name");
            explorer.SaveWorkspaceAs(saved);
        });
        test.Check("Watch persistence: expressions saved with workspace", () => HexWorkspace.Load(saved).Watches.SequenceEqual(new[] { "value + 1", "items[0].Name" }));
        test.Run(() => { explorer.OpenFolder(second); Context.Panels.ShowSide(HexSide.Debug); });
        test.Wait();
        test.Check("Watch persistence: another workspace has its own watches", () => explorer.workspace.Watches.Count == 0 && !Shown("value + 1"));
        test.Run(() => { debugger.WatchExpression("other"); explorer.OpenWorkspaceFile(saved); Context.Panels.ShowSide(HexSide.Debug); });
        test.Wait();
        test.Check("Watch persistence: reopening restores original expressions", () => Shown("value + 1") && Shown("items[0].Name") && !Shown("other"));
        test.Run(() => debugger.WatchExpression("restored + 2"));
        test.Check("Watch persistence: later changes update saved workspace", () => HexWorkspace.Load(saved).Watches.Contains("restored + 2"));
    }
}
