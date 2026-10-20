using Key = Silk.NET.Input.Key;

using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test, keyboard shortcuts changed (Help, Keyboard Shortcuts, and keys.hor), and put back after.
internal sealed partial class HexScene
{
    private void TestKeys(HexPointerScript test)
    {
        MenuItem? Item(string label) => Commands().FirstOrDefault(pair => LabelOf(pair.Item) == label).Item;

        test.Check("keys: the menus' keys read and write back as they're written, the odd ones among them", () =>
            new[] { "Ctrl+Shift+F", "Ctrl+\\", "Ctrl+,", "Ctrl+Shift+[", "Del", "Esc", "Shift+F12", "Ctrl+0", "Alt+Up", "Ctrl+=" }
                .All(written => UIKeys.Tidy(written) == written)
            && UIKeys.Read("Ctrl+Nope") is null && UIKeys.Read("Ctrl+F+G") is null && UIKeys.Tidy("alt+ctrl+f") == "Ctrl+Alt+F");

        // The window, every command in a list, narrowed by the filter, the arrows go about it
        test.Run(OpenKeysWindow);
        test.Settle(0.3f);
        test.Check("keys: the shortcuts window lists every command with its key, and the keys that aren't a command's", () =>
            keysWindow is { Visible: true } && keysTree is { } tree && tree.Items.Count > Commands().Count()
            && tree.Items.Any(row => row.Text == "Find in Files" && row.Detail == "Ctrl+Shift+F") && tree.Items.Any(row => row.Tag is null && row.Detail.Contains("Ctrl+Space")));
        test.Run(() =>
        {
            keysFilter!.Text = "find in";
            keysFilter.OnChanged!(keysFilter.Text);
        });
        test.Wait();
        test.Check("keys: typed into, the filter leaves the commands that say it", () =>
            keysTree!.Items.Count >= 1 && keysTree.Items.All(row => row.Text.Contains("Find in", StringComparison.OrdinalIgnoreCase) || row.Tooltip.Contains("find in", StringComparison.OrdinalIgnoreCase)));

        // Enter on a row waits for a key, the way the dialog takes it
        test.Run(() => keysTree!.OnActivated!(keysTree.Items.First(row => row.Text == "Find in Files")));
        test.Wait();
        test.Check("keys: enter on a command waits for the key it goes on", () => keyFor is not null && keyDialog is { IsOpen: true });
        test.Run(() => KeyPressedFor(Item("Find in Files")!, Key.F, control: true, shift: false, alt: true));
        test.Wait();
        test.Check("keys: a command put on another key says so in its menu, the palette and the list, and its old key does nothing", () =>
            Item("Find in Files")!.Shortcut == "Ctrl+Alt+F" && AllCommands().Any(command => command.Text.EndsWith("Find in Files...   (Ctrl+Alt+F)", StringComparison.Ordinal))
            && keysTree!.Items.Any(row => row.Text == "Find in Files" && row.Detail == "Ctrl+Alt+F")
            && commands.Keys.Has(Key.F, true, false, true) && !commands.Keys.Has(Key.F, true, true, false) && keyFor is null && keyDialog is not { IsOpen: true });

        // Something else onto a key that's somebody's, which then isn't on one, and delete takes a key off
        test.Run(() => KeyPressedFor(Item("Go to File")!, Key.F, control: true, shift: false, alt: true));
        test.Wait();
        test.Check("keys: a key that something else had is taken off of it, one thing a key", () =>
            Item("Go to File")!.Shortcut == "Ctrl+Alt+F" && Item("Find in Files")!.Shortcut.Length == 0 && rebound.Count == 2);
        test.Run(() => RemoveKeyOf(Item("Go to File")!));
        test.Wait();
        test.Check("keys: delete on a row takes its key off", () => Item("Go to File")!.Shortcut.Length == 0 && !commands.Keys.Has(Key.F, true, false, true));

        // Written to a keys.hor and read back
        string file = Path.Combine(testFolder, "selftest_keys.hor");
        List<(string Label, string Key)> read = [];
        test.Run(() =>
        {
            rebound.Add(("Split Editor", "Ctrl+\\"));
            WriteKeys(file);
            read = ReadKeys(file);
            rebound.RemoveAt(rebound.Count - 1);
        });
        test.Check("keys: keys.hor keeps them and reads back the same, a backslash and a key taken off among them", () =>
            read is [("Find in Files", ""), ("Go to File", ""), ("Split Editor", "Ctrl+\\")]);

        // Reset All puts every key back where it was
        test.Run(PutKeysBack);
        test.Wait();
        test.Check("keys: reset puts them all back where they were", () =>
            Item("Go to File")!.Shortcut == "Ctrl+P" && Item("Find in Files")!.Shortcut == "Ctrl+Shift+F" && commands.Keys.Has(Key.F, true, true, false)
            && commands.Keys.Has(Key.P, true, false, false) && !commands.Keys.Has(Key.F, true, false, true) && rebound.Count == 0);
        test.Run(() => keysWindow?.OnClosed?.Invoke());
        test.Wait();
    }
}
