using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test of where the keyboard goes, every key pressed for real (the engine's keyboard and what it types, the way
// hex_key presses them). A window shut with Escape gives the keyboard back to the code (the settings, the keys, Set Up
// F5), a menu of the bar doesn't take it, the explorer goes along with the arrows and keeps it, Space shows a file and
// Enter goes to it, a right click on a row gives it the tree, the outline's icon gives it the outline, the rings show
// only after a key, a key takes a tooltip down.
internal sealed partial class DawnScene
{
    /// <summary>Presses keys the way a person does, written the way a menu writes them, a space between strokes.</summary>
    private void PressKeys(HexPointerScript test, string keys)
    {
        foreach (string written in keys.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            HexKeyStroke stroke = HexKeyStroke.Parse(written) ?? throw new ArgumentException($"no such key as {written}");
            // Both at once, the way a key comes off the keyboard, so what it types is gone with the keyboard if a shortcut
            // or a menu moves it on (Enter picking the palette out of View typed into the palette)
            test.Run(() =>
            {
                Engine.Input.Keyboard.Tap(stroke.Keys);
                UIKeyboard.Press(stroke.Key, stroke.Control, stroke.Shift, stroke.Alt, stroke.Super);
            });
            test.Wait();
        }
    }

    // The menu a right click opened, whoever opened it, null while none is up
    private ContextMenu? OpenContextMenu => chrome.Root.Children.OfType<ContextMenu>().FirstOrDefault(shown => shown.Showing is not null);

    // Whether the code has the keyboard
    private bool CodeHasKeys => compositor.Focus == code;

    private void TestFocus(HexPointerScript test)
    {
        OnCode(test, dataFile);
        TestWindowsGiveKeysBack(test);
        TestExplorerKeys(test);
        TestRingsAndTips(test);
    }

    private void TestWindowsGiveKeysBack(HexPointerScript test)
    {
        // The settings, Escape out of the search with nothing in it shuts them
        test.Run(() =>
        {
            code.Focus();
            OpenSettings(null);
        });
        test.Settle(0.3f);
        test.Check("focus: the settings open with the keyboard in their search", () => SettingsWindow is not null && compositor.Focus == SettingsPage?.Search);
        PressKeys(test, "Escape");
        test.Check("focus: Escape shuts the settings and the code has the keyboard again", () => SettingsWindow is null && CodeHasKeys);

        // A click on the title bar takes the keyboard off the search, and Escape still shuts the window
        test.Run(() => OpenSettings(null));
        test.Settle(0.3f);
        test.Click(() => chrome.ToWorld(SettingsWindow is { } window ? window.TitleBounds.Center with { X = window.TitleBounds.Center.X - 40.0f } : default));
        test.Check("focus: a click on the settings' title bar leaves the keyboard with the window, not on nothing", () => compositor.Focus is WindowPanel);
        PressKeys(test, "Escape");
        test.Check("focus: and Escape shuts them from there, the code given the keyboard back", () => SettingsWindow is null && CodeHasKeys);

        // The keys window and Set Up F5
        test.Run(OpenKeysWindow);
        test.Settle(0.3f);
        PressKeys(test, "Escape");
        test.Check("focus: Escape shuts Keyboard Shortcuts and gives the code the keyboard back", () => keysWindow is null && CodeHasKeys);
        test.Run(() => tasks.SetUpRun(dataFile));
        test.Settle(0.3f);
        test.Check("focus: Set Up F5 opens with the keyboard in it", () => tasks.SetupWindow is { } window && compositor.Focus is { } focus && window.Holds(focus));
        PressKeys(test, "Escape");
        test.Check("focus: Escape shuts Set Up F5 and gives the code the keyboard back", () => tasks.SetupWindow is null && CodeHasKeys);

        // A menu of the bar opened with a click leaves the keyboard where it was, Escape shuts it
        test.Click(() => chrome.ToWorld(menu.TitleBounds("File").Center));
        test.Check("focus: a click on File opens it with the code still having the keyboard", () => menu.OpenMenu?.Title == "File" && CodeHasKeys);
        PressKeys(test, "Down Down");
        test.Check("focus: Down goes down the menu opened with a click, the code not moved", () => menu.KeyedItem is not null && CodeHasKeys);
        PressKeys(test, "Escape");
        test.Check("focus: Escape shuts it and the code has the keyboard", () => menu.OpenMenu is null && CodeHasKeys);

        // Peek's code lets Tab out again, and Escape from anywhere in it gives the keyboard back to the code
        test.Run(() =>
        {
            File.WriteAllText(Path.Combine(testFolder, "focus_peek.hor"), "let speed = 3;\n\nlet doubled = speed * 2;\n");
            Open(Path.Combine(testFolder, "focus_peek.hor"));
            code.GoTo(2, 15);
            PeekDefinition();
        });
        test.WaitFor(() => textDialog is { IsOpen: true } && peeking is null, 4.0f);
        test.Run(() =>
        {
            if (textDialog is { IsOpen: true } && Descendants(textDialog.Body).OfType<CodeEditor>().FirstOrDefault() is { } box)
                box.Focus();
        });
        PressKeys(test, "Tab");
        test.Check("focus: Tab goes out of peek's code to its buttons", () =>
            textDialog is { IsOpen: true } dialog && compositor.Focus is Button button && dialog.Buttons.Contains(button));
        PressKeys(test, "Escape");
        test.Check("focus: Escape shuts peek and the code has the keyboard", () => textDialog is not { IsOpen: true } && CodeHasKeys);
        test.Run(CloseNow);
        test.Wait();
    }

    private void TestExplorerKeys(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "focus_keys");
        string a = Path.Combine(folder, "a.txt"), b = Path.Combine(folder, "b.txt");
        test.Run(() =>
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
            Directory.CreateDirectory(Path.Combine(folder, "inner"));
            File.WriteAllText(a, "a\n");
            File.WriteAllText(b, "b\n");
            explorer.AddFolder(folder);
            explorer.workspace.Expanded.Add(folder);
            treeDirty = true;
        });
        test.Wait();

        UITreeItem? Row(string path) => explorer.explorerRows.FirstOrDefault(row => PlatformPaths.Same(row.Path, path)).Row;
        HexDocument? before = null;

        PressKeys(test, "Ctrl+Shift+E");
        test.Run(() =>
        {
            before = document;
            sideTree.Selected = Row(folder);
        });
        test.Check("focus: Ctrl+Shift+E gives the explorer the keyboard", () => compositor.Focus == sideTree);
        PressKeys(test, "Down Down Down");
        test.Check("focus: the arrows go down the explorer and leave the keyboard in it, nothing opened", () =>
            compositor.Focus == sideTree && sideTree.Selected is { } row && row != Row(folder) && document == before && Directory.Exists(Path.Combine(folder, "inner")));
        test.Run(() => sideTree.Selected = Row(a));
        PressKeys(test, "Space");
        test.Check("focus: Space shows the file and the keyboard stays in the explorer", () =>
            document.Path is { } shown && PlatformPaths.Same(shown, a) && compositor.Focus == sideTree);
        PressKeys(test, "Down");
        test.Check("focus: and Down goes on to the next file", () => sideTree.Selected == Row(b) && compositor.Focus == sideTree);
        PressKeys(test, "Enter");
        test.Check("focus: Enter opens it and gives it the keyboard", () => document.Path is { } open && PlatformPaths.Same(open, b) && CodeHasKeys);

        // The menu of a row, the tree has the keyboard with it and after it
        test.RightClick(() => sideTree.RowBounds(Row(a)!) is { } bounds ? chrome.ToWorld(bounds.Center) : default);
        test.Check("focus: a right click on a row of the explorer gives the tree the keyboard", () => OpenContextMenu?.Showing is not null && compositor.Focus == sideTree);
        PressKeys(test, "Down");
        test.Check("focus: Down goes down the row's menu", () => OpenContextMenu?.KeyedItem is not null && sideTree.Selected == Row(a));
        PressKeys(test, "Escape");
        test.Check("focus: Escape shuts it with the tree still having the keyboard", () => OpenContextMenu?.Showing is null && compositor.Focus == sideTree);

        // Shift+Tab from the side is the activity bar's button, Tab from there the side again
        PressKeys(test, "Shift+Tab");
        test.Check("focus: Shift+Tab from the explorer goes to its button on the activity bar", () => compositor.Focus == layout.Get<Button>("act_explorer"));
        PressKeys(test, "Tab");
        test.Check("focus: Tab from there is the explorer again", () => compositor.Focus == sideTree);

        // The outline's icon clicked, the outline has the keyboard and not nothing
        test.Run(() => Open(dataFile));
        test.Wait();
        test.Click(At(() => layout.Get<Button>("act_outline")));
        test.Check("focus: a click on the outline's icon gives the outline the keyboard", () => Side == HexSide.Outline && compositor.Focus == sideTree);

        test.Run(() =>
        {
            foreach (string open in (string[])[a, b])
            {
                if (documents.FirstOrDefault(doc => doc.Path is { } path && PlatformPaths.Same(path, open)) is { } doc)
                {
                    Show(doc);
                    CloseNow();
                }
            }

            explorer.RemoveFolder(folder);
            Open(dataFile);
        });
        test.Wait();
    }

    private void TestRingsAndTips(HexPointerScript test)
    {
        // A click rings nothing, a key after it does
        test.Click(At(() => layout.Get<Button>("act_explorer")));
        test.Check("focus: a click on the explorer's icon gives it the keyboard with no ring", () => compositor.Focus == sideTree && !compositor.FocusVisible);
        PressKeys(test, "Down");
        test.Check("focus: and a key after it brings the ring", () => compositor.FocusVisible);

        // A key takes the tooltip down
        test.Hover(At(() => layout.Get<Button>("act_extensions")));
        test.WaitFor(() => compositor.TooltipOf is not null, 3.0f);
        test.Check("focus: resting on the extensions' icon brings its tooltip up", () => compositor.TooltipOf is not null);
        PressKeys(test, "ShiftLeft");
        test.Check("focus: a key pressed takes the tooltip down", () => compositor.TooltipOf is null);
        test.Settle(1.0f);
        test.Check("focus: and it stays down while the pointer doesn't move", () => compositor.TooltipOf is null);
        test.Run(() => code.Focus());
        test.Wait();
    }
}
