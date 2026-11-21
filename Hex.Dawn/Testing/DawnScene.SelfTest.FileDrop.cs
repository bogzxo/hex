using System.Numerics;

using Horizon.Core;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Files and folders dragged onto the window from outside (DawnScene.FileDrop), faked through the engine's event the way
// the platform says it (WindowManager.PostFileDrop): what is held said over the window, a folder dropped with no workspace
// made the workspace, another added to it, files dropped each in a tab of their own with a picture as the picture and a
// notebook as the notebook and the last one shown, a file held over a folder's row in the explorer said and dropped there
// copied into it, a drop of nothing that's there said and nothing opened, and a drag that leaves taking what it said with it.
internal sealed partial class DawnScene
{
    private void TestFileDrop(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "file drop");
        string first = Path.Combine(root, "first"), second = Path.Combine(root, "second"), loose = Path.Combine(root, "loose");
        string dest = Path.Combine(first, "dest");
        string code = Path.Combine(loose, "Dropped.cs"), text = Path.Combine(loose, "notes.txt"), picture = Path.Combine(loose, "picture.png");
        string notebook = Path.Combine(loose, "sheet.ipynb"), copied = Path.Combine(loose, "copied.txt");
        Vector2 middle = Vector2.Zero;

        // Where is asked as the step runs, the places are found by the steps before it
        void Post(FileDropKind kind, string[] paths, Func<Vector2> at) => test.Run(() => Engine.WindowManager.PostFileDrop(new FileDrop(kind, paths, at())));
        bool Opened(string path) => documents.Any(open => open.Path is { } at && PlatformPaths.Same(Path.GetFullPath(at), path));
        string Said() => dropOverlay is { Visible: true } shown ? shown.Text : string.Empty;
        void Shot(string name)
        {
            test.Run(() => Engine.WindowManager.RequestFrames());
            test.Settle(0.4f);
            test.Run(() =>
            {
                if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                    Engine.CaptureScreenshot(Path.Combine(shots, name + ".png"));
                Engine.WindowManager.RequestFrames();
            });
            test.Settle(0.3f);
        }
        UITreeItem? Row(string path) => explorer.explorerRows.FirstOrDefault(row => PlatformPaths.Same(row.Path, path)).Row;

        test.Run(() =>
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            foreach (string folder in (string[])[first, second, loose, dest])
                Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(first, "inside.txt"), "first\n");
            File.WriteAllText(Path.Combine(second, "inside.txt"), "second\n");
            File.WriteAllText(code, "class Dropped { }\n");
            File.WriteAllText(text, "dropped\n");
            File.WriteAllText(copied, "copied\n");
            File.WriteAllText(notebook, "{\"cells\": [{\"cell_type\": \"markdown\", \"metadata\": {}, \"source\": [\"# Dropped\"]}], \"metadata\": {}, \"nbformat\": 4, \"nbformat_minor\": 5}\n");
            File.Copy(Path.Combine(Hex.Core.HexHost.ProgramFolder, "Assets", "hex", "images", "background_icon.png"), picture, overwrite: true);

            // No workspace, so the first folder dropped is the workspace
            explorer.workspace = new HexWorkspace();
            treeDirty = true;
            Side = HomeSide;
            middle = Engine.WindowManager.ViewportSize * 0.5f;
        });
        test.Wait();

        // Held over the window, what letting go would do is said, and goes when the drag leaves
        Post(FileDropKind.Enter, [first], () => middle);
        test.WaitFor(() => Said().Length > 0, 3.0f);
        test.Check("file drop: a folder held over the window with no workspace says it'd be the workspace", () => Said().Contains("first") && Said().Contains("workspace"));
        Post(FileDropKind.Over, [], () => middle + new Vector2(20, 10));
        test.Check("file drop: moving about keeps saying it", () => Said().Contains("first"));
        Post(FileDropKind.Leave, [], () => Vector2.Zero);
        test.WaitFor(() => Said().Length == 0, 3.0f);
        test.Check("file drop: a drag that leaves takes what it said with it", () => Said().Length == 0);

        // Dropped, the folder is the workspace
        Post(FileDropKind.Drop, [first], () => middle);
        test.WaitFor(() => explorer.workspace.Folders.Count == 1, 3.0f);
        test.Check("file drop: a folder dropped with no workspace is the workspace", () =>
            explorer.workspace.Folders.Count == 1 && PlatformPaths.Same(explorer.workspace.Folders[0], first));

        // Files and another folder dropped at once, the folder added and the files each in a tab, the last one shown
        Post(FileDropKind.Enter, [second, code, text], () => middle);
        test.WaitFor(() => Said().Length > 0, 3.0f);
        Shot("file-drop-window");
        test.Check("file drop: files and a folder held say both, the folder added to the workspace", () => Said().Contains("2 files") && Said().Contains("second") && Said().Contains("add"));
        Post(FileDropKind.Drop, [second, code, text, picture, notebook], () => middle);
        test.WaitFor(() => Opened(notebook), 5.0f);
        test.Check("file drop: a second folder dropped is added to the workspace, the first kept", () =>
            explorer.workspace.Folders.Count == 2 && explorer.workspace.Folders.Any(folder => PlatformPaths.Same(folder, second)));
        test.Check("file drop: every file dropped is open in a tab of its own", () => Opened(code) && Opened(text) && Opened(picture) && Opened(notebook));
        test.Check("file drop: the picture is open as the picture it is", () =>
            documents.FirstOrDefault(open => open.Path is { } at && PlatformPaths.Same(Path.GetFullPath(at), picture))?.Picture is not null);
        test.Check("file drop: the last one dropped is the one shown, a notebook as the notebook", () =>
            document.Path is { } shown && PlatformPaths.Same(Path.GetFullPath(shown), notebook) && IsNotebook(document) && document.View == HexView.Notebook);
        test.Check("file drop: what it said while held is gone once dropped", () => Said().Length == 0);

        // Over a folder's row in the explorer, a file held says it'd be copied there, and dropped it is
        Vector2 overDest = Vector2.Zero;
        test.Run(() =>
        {
            explorer.workspace.Expanded.Add(first);
            ShowSide(HexSide.Explorer);
            treeDirty = true;
        });
        test.WaitFor(() => Row(dest) is { } row && sideTree.RowBounds(row) is not null, 5.0f);
        test.Run(() =>
        {
            if (Row(dest) is { } row && sideTree.RowBounds(row) is { } bounds)
                overDest = camera.WorldToScreen(chrome.ToWorld(bounds.Center));
        });
        Post(FileDropKind.Enter, [copied], () => overDest);
        test.WaitFor(() => Said().Length > 0, 3.0f);
        test.Check("file drop: a file held over a folder's row in the explorer says it'd be copied into it", () =>
            Said().Contains("Copy") && Said().Contains("dest") && dropOverlay?.Place is not null);
        Shot("file-drop-explorer-folder");
        Post(FileDropKind.Drop, [copied], () => overDest);
        test.WaitFor(() => File.Exists(Path.Combine(dest, "copied.txt")), 5.0f);
        test.Check("file drop: dropped on the folder's row it's copied into the folder, the file left where it was", () =>
            File.Exists(Path.Combine(dest, "copied.txt")) && File.Exists(copied) && !Opened(copied));

        // Nothing that's there opens nothing
        int before = 0;
        test.Run(() => before = documents.Count);
        Post(FileDropKind.Drop, [Path.Combine(root, "not there.txt")], () => middle);
        test.Wait();
        test.Check("file drop: a drop of nothing that's there opens nothing", () => documents.Count == before);

        test.Run(() => Side = HomeSide);
    }
}
