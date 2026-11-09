using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestExplorerFiles(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "file-workflow"), target = Path.Combine(folder, "destination");
        string a = Path.Combine(folder, "file-000.txt"), b = Path.Combine(folder, "file-001.txt"), locked = Path.Combine(folder, "locked.txt");
        FileStream? lockedHandle = null;
        UITreeItem Row(string path) => explorer.explorerRows.First(row => PlatformPaths.Same(row.Path, path)).Row;
        test.Run(() =>
        {
            Directory.CreateDirectory(target);
            for (int i = 0; i < 120; i++) File.WriteAllText(Path.Combine(folder, $"file-{i:000}.txt"), i.ToString());
            File.WriteAllText(locked, "owned by another process");
            explorer.AddFolder(folder);
            Open(a);
            ShowSide(HexSide.Explorer);
        });
        test.Wait();
        test.Check("explorer files: the selected file has a visible row before a pointer drag", () =>
            sideTree.Visible && Row(a) is { } file && sideTree.RowBounds(file) is not null);
        test.Drag(
            () => sideTree.RowBounds(Row(a)) is { } from ? chrome.ToWorld(from.Center) : test.Pointer.Position,
            () => sideTree.RowBounds(Row(b)) is { } to ? chrome.ToWorld(to.Center) : test.Pointer.Position);
        test.Wait();
        test.Check("explorer files: holding and moving from one file row to another leaves the tree usable", () =>
            sideTree.Visible && sideTree.RowBounds(Row(a)) is not null && sideTree.RowBounds(Row(b)) is not null
            && document.Path is { } opened && PlatformPaths.Same(opened, a));

        test.Run(() => sideTree.Reveal(Row(locked)));
        test.Wait();
        test.Check("explorer files: the locked file is in view for the pointer test", () => sideTree.RowBounds(Row(locked)) is not null);
        test.Run(() => lockedHandle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None));
        test.Drag(
            () => sideTree.RowBounds(Row(locked)) is { } from ? chrome.ToWorld(from.Center) : test.Pointer.Position,
            () => sideTree.RowBounds(Row(locked)) is { } to ? chrome.ToWorld(to.Center + new System.Numerics.Vector2(24, 0)) : test.Pointer.Position);
        test.Wait();
        test.Check("explorer files: dragging a locked file reports the read error and keeps the open document", () =>
            document.Path is { } opened && PlatformPaths.Same(opened, a) && messageItem.Text.Contains(Path.GetFileName(locked), StringComparison.Ordinal));
        test.Run(() => { lockedHandle?.Dispose(); lockedHandle = null; });
        string missing = Path.Combine(folder, "missing.txt");
        bool missingOpened = true;
        test.Run(() => missingOpened = Open(missing));
        test.Check("explorer files: a missing path reports the existing error and keeps the open document", () =>
            !missingOpened && document.Path is { } opened && PlatformPaths.Same(opened, a)
            && messageItem.Text.Contains(Said("documents.no_file", ("path", missing)), StringComparison.Ordinal));

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
