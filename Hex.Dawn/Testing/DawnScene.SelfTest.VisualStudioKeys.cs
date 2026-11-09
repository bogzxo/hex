using Horizon.UI;
using Horizon.UI.Components;

using Key = Horizon.Input.Key;

namespace Hex.Dawn;

// Visual Studio's keys turned on and off again (DawnScene.Keys.VisualStudio), the commands on its keys, the second keys
// there, nothing of it in keys.hor, and Hex's own keys back as it goes off.
internal sealed partial class DawnScene
{
    private void TestVisualStudioKeys(HexPointerScript test, Func<string, MenuItem?> command)
    {
        string back = Said("places.back").TrimEnd('.'), zoomOut = Said("designer_selection.zoom_out").TrimEnd('.'), settingsOpen = Said("settings_window.open").TrimEnd('.');
        var ctrlMinus = new UIKeyStroke(Key.Minus, true, false, false);
        var ctrlR = new UIKeyStroke(Key.R, true, false, false);
        test.Run(() =>
        {
            settings.VisualStudioKeys = true;
            ApplyKeyLayer();
        });
        test.Wait();
        test.Check("visual studio keys: Ctrl+- goes back, the zoom on Ctrl+Shift+, and the settings off Ctrl+,", () =>
            command(back)!.Shortcut == "Ctrl+-" && command(zoomOut)!.Shortcut == "Ctrl+Shift+," && command(settingsOpen)!.Shortcut.Length == 0
            && commands.Keys.Has(ctrlMinus, null) && commands.Keys.Has(new UIKeyStroke(Key.Comma, true, false, false), null)
            && commands.Keys.Has(ctrlR, ctrlR) && commands.Keys.Has(new UIKeyStroke(Key.M, true, false, false), new UIKeyStroke(Key.O, true, false, false)));
        test.Check("visual studio keys: none of it is a key changed by hand", () => !HandKeys().Any() && layerKeys.Count > 0);

        string duplicate = Said("code.duplicate_line").TrimEnd('.'), next = Said("code.add_next_occurrence").TrimEnd('.'), copyDown = Said("code.copy_line_down").TrimEnd('.');
        string problems = Said("menus.problems").TrimEnd('.'), split = Said("menus.split_editor").TrimEnd('.');
        var ctrlBackslash = new UIKeyStroke(Key.BackSlash, true, false, false);
        test.Check("visual studio keys: Ctrl+D duplicates and the editor's keys go with it, a box on Shift+Alt+arrows", () =>
            command(duplicate)!.Shortcut == "Ctrl+D" && command(next)!.Shortcut == "Shift+Alt+." && command(copyDown)!.Shortcut.Length == 0
            && UIKeyboard.VisualStudioKeys && UIKeyboard.Meaning(Key.Down, false, true, true, false, UIKeyboard.VisualStudioKeys).Code == UIKeyboard.BOX_DOWN);
        test.Check("visual studio keys: Ctrl+\\ Ctrl+E is the Error List, the split off Ctrl+\\, and Ctrl+F4, Ctrl+Alt+L and Ctrl+] are there", () =>
            command(problems)!.Shortcut == "Ctrl+\\ Ctrl+E" && command(split)!.Shortcut.Length == 0
            && commands.Keys.Has(ctrlBackslash, new UIKeyStroke(Key.E, true, false, false)) && !commands.Keys.Has(ctrlBackslash, null)
            && commands.Keys.Has(new UIKeyStroke(Key.F4, true, false, false), null) && commands.Keys.Has(new UIKeyStroke(Key.L, true, false, true), null)
            && commands.Keys.Has(new UIKeyStroke(Key.RightBracket, true, false, false), null));

        test.Run(() =>
        {
            settings.VisualStudioKeys = false;
            ApplyKeyLayer();
        });
        test.Wait();
        test.Check("visual studio keys: off again, Hex's own keys are back", () =>
            command(back)!.Shortcut == "Alt+Left" && command(zoomOut)!.Shortcut == "Ctrl+-" && command(settingsOpen)!.Shortcut == "Ctrl+,"
            && commands.Keys.Has(ctrlMinus, null) && !commands.Keys.Has(ctrlR, ctrlR) && layerKeys.Count == 0 && rebound.Count == 0
            && command(duplicate)!.Shortcut == "Ctrl+Shift+D" && command(next)!.Shortcut == "Ctrl+D" && command(split)!.Shortcut == "Ctrl+\\"
            && !UIKeyboard.VisualStudioKeys && !commands.Keys.Has(new UIKeyStroke(Key.F4, true, false, false), null)
            && commands.Keys.Has(new UIKeyStroke(Key.W, true, false, false), null) && commands.Keys.Has(new UIKeyStroke(Key.E, true, true, false), null)
            && commands.Keys.Has(new UIKeyStroke(Key.Comma, true, false, false), null));
    }
}
