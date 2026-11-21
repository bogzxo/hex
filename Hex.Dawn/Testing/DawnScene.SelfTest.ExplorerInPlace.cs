using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The explorer worked in place (DawnScene.ExplorerInPlace and the tree's find, edit and drag): files open in tabs marked,
// the file shown revealed and left alone with explorer_auto_reveal off, a row found by typing, a name typed over on its
// row with what's wrong said under it, a new file and folder named on rows of their own, files dragged onto a folder with
// the folder lit, a line where they land, a plus for a copy and the tree scrolling near its edge. And two looks the tree
// pictures showed: the menus folded into one at 200% rather than under Dawn and Dusk, and Properties' read-only values
// written as text. HEX_SELFTEST_SHOTS names a folder for a picture of each.
internal sealed partial class DawnScene
{
    private void TestExplorerInPlace(HexPointerScript test)
    {
        string repo = Path.Combine(testFolder, "in_place");
        string src = Path.Combine(repo, "src"), docs = Path.Combine(repo, "docs"), dest = Path.Combine(repo, "dest"), list = Path.Combine(repo, "list");
        string alpha = Path.Combine(src, "Alpha.cs"), beta = Path.Combine(src, "Beta.cs"), gamma = Path.Combine(src, "Gamma.cs");
        string guide = Path.Combine(docs, "guide.md");
        float scaleBefore = 1.0f;

        UITreeItem? Row(string path) => explorer.explorerRows.FirstOrDefault(row => PlatformPaths.Same(row.Path, path)).Row;
        Vector2 At(Func<UITreeItem?> row) => row() is { } shown && sideTree.RowBounds(shown) is { } bounds ? chrome.ToWorld(bounds.Center) : test.Pointer.Position;
        void Type(string text) => test.Run(() => Engine.Input.Post(Horizon.Input.InputEvent.Typed(text)));

        void Shot(string name)
        {
            test.Run(() => Engine.WindowManager.RequestFrames());
            test.Settle(0.4f);
            test.Run(() =>
            {
                if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                    Engine.CaptureScreenshot(Path.Combine(shots, name + ".png"));

                // The picture is of the next frame drawn, which an idle window only draws when asked
                Engine.WindowManager.RequestFrames();
            });
            test.Settle(0.3f);
        }

        test.Run(() =>
        {
            scaleBefore = compositor.Scale;
            if (Directory.Exists(repo))
                Directory.Delete(repo, recursive: true);
            foreach (string folder in (string[])[src, docs, dest, list])
                Directory.CreateDirectory(folder);
            File.WriteAllText(alpha, "namespace Sample;\n");
            File.WriteAllText(beta, "namespace Sample;\n");
            File.WriteAllText(gamma, "namespace Sample;\n");
            File.WriteAllText(guide, "# Guide\n");
            for (int i = 0; i < 60; i++)
                File.WriteAllText(Path.Combine(list, $"item-{i:000}.txt"), i.ToString());

            explorer.OpenFolder(repo);
            explorer.workspace.Expanded.Add(src);
            Open(alpha);
            ShowSide(HexSide.Explorer);
            treeDirty = true;
        });
        test.WaitFor(() => Row(alpha) is not null && Row(beta) is not null, 5.0f);

        // Open files have a dot where a folder has its chevron, the others none
        test.Check("in place: the file open in a tab has a dot on its row, the files that aren't have none", () =>
            Row(alpha)?.Pip is not null && Row(beta)?.Pip is null && Row(src)?.Pip is null);
        test.Run(() => Open(beta));
        test.WaitFor(() => Row(beta)?.Pip is not null, 3.0f);
        test.Check("in place: a file opened gets its dot, the one open before keeps it", () => Row(beta)?.Pip is not null && Row(alpha)?.Pip is not null);
        Shot("inplace-open-marks");

        // The file shown has its folders opened and its row picked, unless explorer_auto_reveal is off
        test.Run(() =>
        {
            explorer.workspace.Expanded.Remove(docs);
            treeDirty = true;
        });
        test.Wait();
        test.Run(() => Open(guide));
        test.WaitFor(() => Row(guide) is not null, 3.0f);
        test.Check("in place: a file shown in a closed folder has the folder opened and its row picked", () =>
            explorer.workspace.Expanded.Contains(docs) && sideTree.Selected == Row(guide) && sideTree.RowBounds(Row(guide)!) is not null);
        Shot("inplace-revealed");
        test.Run(() =>
        {
            settings.ExplorerAutoReveal = false;
            explorer.workspace.Expanded.Remove(src);
            sideTree.Selected = Row(guide);
            treeDirty = true;
        });
        test.Wait();
        test.Run(() => Open(alpha));
        test.Wait();
        test.Check("in place: with explorer_auto_reveal off a file shown leaves the folders and the picked row as they were", () =>
            !explorer.workspace.Expanded.Contains(src) && Row(alpha) is null && sideTree.Selected == Row(guide));
        test.Run(() =>
        {
            settings.ExplorerAutoReveal = true;
            explorer.workspace.Expanded.Add(src);
            explorer.workspace.Expanded.Remove(list);
            treeDirty = true;
        });
        test.WaitFor(() => Row(alpha) is not null, 3.0f);

        // Typing finds a row by its name, a box at the top says what's typed, Escape lets it go
        test.Run(() =>
        {
            sideTree.Selected = Row(repo);
            sideTree.Focus();
        });
        test.Wait();
        Type("gu");
        test.Wait();
        test.Check("in place: typing in the explorer picks the next row with what's typed, and the box says it", () =>
            sideTree.FindText == "gu" && sideTree.Selected == Row(guide) && sideTree.FindBounds.Width > 0.0f && compositor.Focus == sideTree);
        Shot("inplace-find");
        Type("zz");
        test.Wait();
        test.Check("in place: what no row has is said in the box, the picked row stays", () =>
            sideTree.FindMissed && sideTree.Selected == Row(guide));
        Shot("inplace-find-missed");
        PressKeys(test, "Escape");
        test.Check("in place: Escape lets go of what was typed and the box goes", () => sideTree.FindText.Length == 0 && sideTree.FindBounds.Width == 0.0f);
        test.Run(() => sideTree.Selected = Row(repo));
        Type("b");
        test.Wait();
        test.Check("in place: a letter goes to the first row starting with it", () => sideTree.Selected == Row(beta));
        PressKeys(test, "Escape");

        // F2 types the name over on its row, its name without the extension picked
        test.Run(() =>
        {
            sideTree.Selected = Row(beta);
            sideTree.Focus();
        });
        PressKeys(test, "F2");
        test.Check("in place: F2 types the name over on its row, the name picked and not its extension", () =>
            sideTree.Edited == Row(beta) && sideTree.EditBox is { Text: "Beta.cs", Selection: "Beta" } box && box.IsFocused);
        Shot("inplace-rename");
        Type("Alpha");
        test.Wait();
        test.Check("in place: a name another file has is said under the row as it's typed", () =>
            sideTree.EditBox?.Text == "Alpha.cs" && sideTree.EditProblem == Said("explorer_files.name_taken", ("name", "Alpha.cs")) && sideTree.EditProblemBounds.Width > 0.0f);
        Shot("inplace-rename-clash");
        PressKeys(test, "Enter");
        test.Check("in place: Enter with a clash keeps the box open and the file as it was", () =>
            sideTree.Edited is not null && File.Exists(beta));
        test.Run(() => TypeInto(sideTree.EditBox!, "Be:ta.cs"));
        test.Wait();
        test.Check("in place: a letter a name can't have is said by name", () =>
            sideTree.EditProblem == Said("explorer_files.name_bad_char", ("char", ":")));
        Shot("inplace-rename-invalid");
        test.Run(() => TypeInto(sideTree.EditBox!, "Delta.cs"));
        PressKeys(test, "Enter");
        test.WaitFor(() => File.Exists(Path.Combine(src, "Delta.cs")), 3.0f);
        test.Check("in place: Enter renames it, the box goes and the open file follows", () =>
            !File.Exists(beta) && sideTree.Edited is null && documents.Any(open => open.Path is { } path && PlatformPaths.Same(path, Path.Combine(src, "Delta.cs"))));
        test.Run(() => explorer.RenamePath(gamma));
        test.Wait();
        test.Check("in place: the menu's Rename is on the row too", () => sideTree.Edited == Row(gamma));
        PressKeys(test, "Escape");
        test.Check("in place: Escape leaves the name as it was", () => sideTree.Edited is null && File.Exists(gamma));

        // New File and New Folder of the explorer's head are named on a row of their own in the folder
        test.Run(() =>
        {
            sideTree.Selected = Row(src);
            newFileButton.OnPressed?.Invoke();
        });
        test.Wait();
        test.Check("in place: New File names the file on a row of its own under the folder, after its folders", () =>
            sideTree.Edited is { Text: "" } made && made.Parent == Row(src) && sideTree.EditBox?.Placeholder == Said("explorer_files.new_file_hint"));
        Shot("inplace-new-file");
        PressKeys(test, "Enter");
        test.Check("in place: Enter with no name says one is needed", () =>
            sideTree.Edited is not null && sideTree.EditProblem == Said("explorer_files.name_empty"));
        Type("Epsilon");
        PressKeys(test, "Enter");
        test.WaitFor(() => File.Exists(Path.Combine(src, "Epsilon.cs")), 3.0f);
        test.Check("in place: Enter makes it with the extension of the files beside it, and opens it", () =>
            sideTree.Edited is null && document.Path is { } path && PlatformPaths.Same(path, Path.Combine(src, "Epsilon.cs")));
        test.WaitFor(() => Row(Path.Combine(src, "Epsilon.cs")) is not null, 3.0f);
        test.Run(() =>
        {
            sideTree.Selected = Row(repo);
            newFolderButton.OnPressed?.Invoke();
        });
        test.Wait();
        test.Check("in place: New Folder names it on a row of its own, first in the folder", () =>
            sideTree.Edited is { } made && made.Parent == Row(repo) && Row(repo)!.Children![0] == made);
        Shot("inplace-new-folder");
        Type("lib");
        PressKeys(test, "Enter");
        test.WaitFor(() => Directory.Exists(Path.Combine(repo, "lib")), 3.0f);
        test.Check("in place: Enter makes the folder", () => sideTree.Edited is null);
        test.Run(() =>
        {
            sideTree.Selected = Row(src);
            newFileButton.OnPressed?.Invoke();
        });
        test.Wait();
        PressKeys(test, "Escape");
        test.Check("in place: Escape takes the new row away and makes nothing", () =>
            sideTree.Edited is null && Row(src)!.Children!.All(child => child.Text.Length > 0));

        // A file dragged onto a folder, the folder lit, a line where it lands, a plus while it's copied
        test.WaitFor(() => Row(guide) is not null && Row(dest) is not null, 3.0f);
        test.Press(() => At(() => Row(guide)));
        test.MoveHeld(() => At(() => Row(dest)));
        test.Wait();
        test.Check("in place: dragging a file over a folder lights the folder as where it goes", () =>
            sideTree.Dragging && sideTree.DropShown is { } drop && drop.Into == Row(dest) && !sideTree.DragCopies);
        Shot("inplace-drag-move");
        test.Run(() => explorerDropCopies = true);
        test.MoveHeld(() => At(() => Row(dest)) + new Vector2(2.0f, 0.0f));
        test.Wait();
        test.Check("in place: with Ctrl the drag says it copies", () => sideTree.DragCopies);
        Shot("inplace-drag-copy");
        test.Run(() => explorerDropCopies = false);
        test.MoveHeld(() => At(() => Row(src)));
        test.Wait();
        test.Check("in place: over a folder whose files all come first by name the line goes after the last of them", () =>
            sideTree.DropShown is { } drop && drop.Into == Row(src) && drop.Before is null);
        Shot("inplace-drag-line");
        test.MoveHeld(() => At(() => Row(dest)));
        test.Release(() => At(() => Row(dest)));
        test.WaitFor(() => File.Exists(Path.Combine(dest, "guide.md")), 5.0f);
        test.Check("in place: let go over the folder it's moved there", () => !File.Exists(guide) && !sideTree.Dragging);
        test.Press(() => At(() => Row(docs)));
        test.MoveHeld(() => At(() => Row(dest)));
        test.Wait();
        test.Check("in place: a folder dragged over a folder can go into it", () => sideTree.DropShown?.Into == Row(dest));
        test.MoveHeld(() => At(() => Row(docs)));
        test.Wait();
        test.Check("in place: a folder dragged over itself has nowhere to go", () => sideTree.Dragging && sideTree.DropShown is null);
        test.Run(sideTree.CancelDrag);
        test.Release(() => At(() => Row(docs)));

        // The tree scrolls by itself while a drag is near its bottom
        test.Run(() =>
        {
            explorer.workspace.Expanded.Add(list);
            treeDirty = true;
        });
        test.WaitFor(() => Row(Path.Combine(list, "item-000.txt")) is not null, 3.0f);
        float scrolledFrom = 0.0f;
        test.Run(() =>
        {
            sideTree.ScrollOffset = 0.0f;
            scrolledFrom = sideTree.ScrollOffset;
        });
        test.Wait();
        test.Press(() => At(() => Row(src)));
        test.MoveHeld(() => chrome.ToWorld(new Vector2(sideTree.Bounds.Center.X, sideTree.Bounds.Min.Y + 6.0f)));
        test.Settle(0.6f);
        test.Check("in place: a drag near the bottom of the tree scrolls it", () => sideTree.Dragging && sideTree.ScrollOffset > scrolledFrom + 20.0f);
        Shot("inplace-drag-scroll");
        test.Run(sideTree.CancelDrag);
        test.Release(() => chrome.ToWorld(new Vector2(sideTree.Bounds.Center.X, sideTree.Bounds.Min.Y + 6.0f)));
        test.Wait();
        test.Check("in place: Escape or a cancel lets go of a drag without moving anything", () => !sideTree.Dragging && Directory.Exists(src));

        // Properties' read-only values are text, the editable ones keep their boxes
        test.Run(() => ShowPropertiesOf(null));
        test.WaitFor(() => properties.Page.Visible && properties.Shown.Count == 1, 10.0f);
        test.Check("in place: Properties' read-only values are written as text, no box round them", () =>
            Within(properties.Page).OfType<TextBox>().Where(box => box.ReadOnly).ToList() is { Count: > 0 } read && read.All(box => box.Bare));
        Shot("inplace-properties");

        // At 200% the menus that don't fit are one menu at the end of the bar, none of them under Dawn and Dusk
        test.Run(() => SetInterfaceSize(2.0f, remember: false));
        test.Settle(0.5f);
        test.Check("in place: at 200% the menus end before Dawn and Dusk, the rest folded into the last menu", () =>
            modeToggle.Parent is { } modes && menu.Bounds.Max.X <= modes.Bounds.Min.X + 0.5f && menu.FoldedCount > 0 && menu.FoldedBounds.Width > 0.0f);
        Shot("inplace-menus-200");
        test.Run(() => menu.Open(menu.Menus.Count));
        test.Wait();
        test.Check("in place: the last menu has the menus that didn't fit, each opening its own", () =>
            menu.OpenMenu == menu.FoldedMenu && menu.FoldedMenu!.Items.Count == menu.FoldedCount && menu.FoldedMenu.Items.All(item => item.Submenu is not null));
        Shot("inplace-menus-folded-open");
        test.Run(() =>
        {
            menu.Close();
            SetInterfaceSize(scaleBefore, remember: false);
        });
        test.Settle(0.5f);
        test.Check("in place: back at its size every menu fits again", () => menu.FoldedCount == 0 && menu.FoldedBounds.Width == 0.0f);
    }

    // Everything inside a component, for a test looking for what's in a page
    private static IEnumerable<UIComponent> Within(UIComponent component)
    {
        foreach (UIComponent child in component.Children)
        {
            yield return child;
            foreach (UIComponent inside in Within(child))
                yield return inside;
        }
    }
}
