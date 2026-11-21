using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test of a new file or folder named on its row, VS Code's way, from each place it's started: the explorer's
// head clicked, a folder's menu (a folder shut, which opens for it) and the palette from the keys. The row is where the
// file will go and has the keyboard; Escape takes it away, a click away with nothing typed takes it away, a click away
// with a name makes it. Nothing is on the disk until the name is kept, and a row taken away leaves nothing behind.
internal sealed partial class DawnScene
{
    private void TestExplorerNew(HexPointerScript test)
    {
        string repo = Path.Combine(testFolder, "new_rows");
        string src = Path.Combine(repo, "src"), docs = Path.Combine(repo, "docs");
        string[] disk = [];

        UITreeItem? Row(string path) => explorer.explorerRows.FirstOrDefault(row => PlatformPaths.Same(row.Path, path)).Row;
        Vector2 At(Func<UITreeItem?> row) => row() is { } shown && sideTree.RowBounds(shown) is { } bounds ? chrome.ToWorld(bounds.Center) : test.Pointer.Position;
        void Type(string text) => test.Run(() => Engine.Input.Post(Horizon.Input.InputEvent.Typed(text)));
        string[] OnDisk() => [.. Directory.EnumerateFileSystemEntries(repo, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];
        void Remember() => test.Run(() => disk = OnDisk());
        bool NothingMade() => OnDisk().SequenceEqual(disk);
        bool NoEmptyRow() => explorer.explorerRows.All(row => row.Row.Text.Length > 0) && AllRows(sideTree.Items).All(row => row.Text.Length > 0);
        static IEnumerable<UITreeItem> AllRows(IEnumerable<UITreeItem> rows) => rows.SelectMany(row => (UITreeItem[])[row, .. AllRows(row.Children ?? [])]);
        bool NewRowIn(string folder) => sideTree.Edited is { Text: "" } made && made.Parent == Row(folder) && sideTree.EditBox is { IsFocused: true };

        // Away from the row: the code, which takes the keyboard
        Func<Vector2> Away = () => chrome.ToWorld(code.Bounds.Center);

        test.Run(() =>
        {
            if (Directory.Exists(repo))
                Directory.Delete(repo, recursive: true);
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(docs);
            File.WriteAllText(Path.Combine(src, "Alpha.cs"), "namespace Sample;\n");
            File.WriteAllText(Path.Combine(src, "Gamma.cs"), "namespace Sample;\n");
            File.WriteAllText(Path.Combine(docs, "guide.md"), "# Guide\n");
            explorer.OpenFolder(repo);
            explorer.workspace.Expanded.Add(src);
            explorer.workspace.Expanded.Remove(docs);
            Open(Path.Combine(src, "Alpha.cs"));
            ShowSide(HexSide.Explorer);
        });
        test.WaitFor(() => Row(Path.Combine(src, "Alpha.cs")) is not null && Row(docs) is not null, 5.0f);
        Remember();

        /* The explorer's head */

        test.Click(() => At(() => Row(src)));
        test.Wait();
        test.Click(() => chrome.ToWorld(newFileButton.Bounds.Center));
        test.Wait();
        test.Check("explorer new: New File in the head puts a row with the keyboard in the folder picked, after its folders", () => NewRowIn(src) && NothingMade());
        PressKeys(test, "Escape");
        test.Wait();
        test.Check("explorer new: Escape takes the row away and nothing is made, the folder picked again", () => sideTree.Edited is null && NoEmptyRow() && NothingMade() && sideTree.Selected == Row(src));

        test.Click(() => chrome.ToWorld(newFileButton.Bounds.Center));
        test.Wait();
        test.Check("explorer new: New File again has its row", () => NewRowIn(src));
        test.Click(Away);
        test.Wait();
        test.Check("explorer new: a click away with nothing typed takes the row away and nothing is made", () => sideTree.Edited is null && NoEmptyRow() && NothingMade());

        test.Click(() => At(() => Row(src)));
        test.Wait();
        test.Click(() => chrome.ToWorld(newFileButton.Bounds.Center));
        test.Wait();
        Type("Beta");
        test.Wait();
        test.Check("explorer new: typing a name makes nothing yet", () => sideTree.Edited is not null && NothingMade());
        test.Click(Away);
        test.Wait();
        test.WaitFor(() => File.Exists(Path.Combine(src, "Beta.cs")), 3.0f);
        test.Check("explorer new: a click away with a name makes it, with the extension beside it, and its row is there", () =>
            File.Exists(Path.Combine(src, "Beta.cs")) && sideTree.Edited is null && NoEmptyRow());
        test.WaitFor(() => Row(Path.Combine(src, "Beta.cs")) is not null, 3.0f);
        test.Check("explorer new: the file made has its row", () => Row(Path.Combine(src, "Beta.cs")) is not null);
        Remember();

        test.Click(() => At(() => Row(src)));
        test.Wait();
        test.Click(() => chrome.ToWorld(newFolderButton.Bounds.Center));
        test.Wait();
        test.Check("explorer new: New Folder in the head puts its row first in the folder", () => NewRowIn(src) && Row(src)!.Children![0] == sideTree.Edited);
        test.Click(Away);
        test.Wait();
        test.Check("explorer new: a New Folder left empty and clicked away from makes nothing", () => sideTree.Edited is null && NoEmptyRow() && NothingMade());

        /* A folder's menu, a folder that's shut */

        test.RightClick(() => At(() => Row(docs)));
        test.Wait();
        test.Click(() => contextMenu is { } shown ? chrome.ToWorld(shown.ItemBounds(Said("explorer_files.new_file")).Center) : Vector2.Zero);
        test.Wait();
        test.Check("explorer new: the menu's New File on a shut folder opens it and puts the row in it with the keyboard", () =>
            NewRowIn(docs) && Row(docs)!.Expanded && NothingMade());
        test.Settle(0.3f);
        test.Check("explorer new: the row stays while nothing's done, the folder opening doesn't take it away", () => NewRowIn(docs) && NothingMade());
        PressKeys(test, "Escape");
        test.Wait();
        test.Check("explorer new: Escape on the menu's row makes nothing", () => sideTree.Edited is null && NoEmptyRow() && NothingMade());

        test.RightClick(() => At(() => Row(docs)));
        test.Wait();
        test.Click(() => contextMenu is { } shown ? chrome.ToWorld(shown.ItemBounds(Said("explorer_files.new_folder")).Center) : Vector2.Zero);
        test.Wait();
        test.Check("explorer new: the menu's New Folder puts its row in the folder", () => NewRowIn(docs));
        Type("images");
        test.Click(Away);
        test.WaitFor(() => Directory.Exists(Path.Combine(docs, "images")), 3.0f);
        test.Check("explorer new: a click away from a folder's name makes the folder", () =>
            Directory.Exists(Path.Combine(docs, "images")) && sideTree.Edited is null && NoEmptyRow());
        Remember();

        /* The keys, the palette's New File */

        test.Run(() => code.Focus());
        PressKeys(test, "Ctrl+Shift+P");
        test.Wait();
        Type(Said("documents.new_file"));
        test.Wait();
        PressKeys(test, "Enter");
        test.Wait();
        test.Check("explorer new: New File from the palette puts the row in the workspace's folder with the keyboard", () => NewRowIn(repo) && NothingMade());
        test.Click(Away);
        test.Wait();
        test.Check("explorer new: and clicked away from untouched it goes, nothing made", () => sideTree.Edited is null && NoEmptyRow() && NothingMade());

        test.Run(() => code.Focus());
        PressKeys(test, "Ctrl+Shift+P");
        test.Wait();
        Type(Said("documents.new_file"));
        test.Wait();
        PressKeys(test, "Enter");
        test.Wait();
        Type("readme.md");
        PressKeys(test, "Enter");
        test.WaitFor(() => File.Exists(Path.Combine(repo, "readme.md")), 3.0f);
        test.Check("explorer new: Enter on a name typed makes it and opens it", () =>
            sideTree.Edited is null && document.Path is { } path && PlatformPaths.Same(path, Path.Combine(repo, "readme.md")));

        test.Run(() => explorer.CloseWorkspace());
    }
}
