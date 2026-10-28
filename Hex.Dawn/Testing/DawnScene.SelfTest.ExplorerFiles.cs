using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestExplorerFiles(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "file-workflow"), target = Path.Combine(folder, "destination");
        string a = Path.Combine(folder, "file-000.txt"), b = Path.Combine(folder, "file-001.txt");
        UITreeItem Row(string path) => explorer.explorerRows.First(row => PlatformPaths.Same(row.Path, path)).Row;
        test.Run(() =>
        {
            Directory.CreateDirectory(target);
            for (int i = 0; i < 120; i++) File.WriteAllText(Path.Combine(folder, $"file-{i:000}.txt"), i.ToString());
            explorer.AddFolder(folder);
            Open(a);
            ShowSide(HexSide.Explorer);
        });
        test.Wait();
        test.Run(() => { sideTree.Selected = Row(target); sideTree.ScrollOffset = 500; });
        test.Wait();
        test.Run(RefreshFromDisk);
        test.Wait();
        test.Check("explorer files: refresh preserves selected folder and viewport", () => sideTree.Selected?.Tooltip == target && MathF.Abs(sideTree.ScrollOffset - 500) < 2);
        test.Run(() => { sideTree.SelectMany([Row(a), Row(b)]); sideTree.Focus(); });
        PressKeys(test, "Ctrl+C");
        test.Check("explorer files: Ctrl+C stages both selected files", () => explorer.CanPaste && !explorer.ClippedCut);
        test.Run(() => { sideTree.Selected = Row(target); sideTree.Focus(); });
        PressKeys(test, "Ctrl+V");
        test.WaitFor(() => File.Exists(Path.Combine(target, Path.GetFileName(b))) && explorer.CanPaste, 10);
        test.Check("explorer files: paste copies both and keeps their sources", () =>
            File.Exists(Path.Combine(target, Path.GetFileName(a))) && File.Exists(a) && File.Exists(b));
        test.Wait();
        test.Check("explorer files: pasted files become the explorer selection", () => sideTree.SelectedItems.Count == 2 && sideTree.SelectedItems.All(item => Path.GetDirectoryName(item.Tooltip) == target));
        test.Run(() => { sideTree.Selected = Row(a); sideTree.Focus(); });
        PressKeys(test, "Ctrl+X");
        test.Check("explorer files: cut stays reversible until paste", () => File.Exists(a) && explorer.IsCutPath(a));
        PressKeys(test, "Escape");
        test.Check("explorer files: Escape cancels staged cut", () => !explorer.ClippedCut && !explorer.IsCutPath(a) && File.Exists(a));
        test.Run(() => explorer.CutPaths([a]));
        test.Run(() => explorer.PasteInto(Path.Combine(folder, "missing-target")));
        test.Check("explorer files: failed paste retains the cut for retry", () => explorer.CanPaste && explorer.ClippedCut && File.Exists(a));
        test.Run(() => explorer.PasteIntoAsync(target));
        test.WaitFor(() => !File.Exists(a) && !explorer.ClippedCut, 10);
        test.Check("explorer files: successful retry moves file and retargets its tab", () => document.Path is { } path && Path.GetDirectoryName(path) == target && File.Exists(path));
        test.Run(() => Shot("explorer-file-workflow"));
        test.Run(() => { explorer.CancelClip(); explorer.RemoveFolder(folder); CloseAll(); });
    }
}
