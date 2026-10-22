using System.Numerics;

using Key = Silk.NET.Input.Key;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Keys put where somebody wants them. Help, Keyboard Shortcuts, is every command with its key in a list, typed into to
// narrow it down, the arrows up and down it, enter (or a double click) waits for a new key for the one chosen and delete
// takes its key off. keys.hor next to hex_settings.hor is what was changed, a command by what its menu calls it and the
// key it's on now ({ "Find in Files": "Ctrl+Alt+F" }, "" for none), read as Hex starts and written at every change. The
// old key of the command comes off, and whatever else was on the new one, so a key does one thing, and the menus and
// the palette say what it is now since they read it off the items. The editing keys the code editor handles itself
// (Ctrl+D and its friends) go on working there as well, it doesn't ask the menus.
internal sealed partial class DawnScene
{
    // dawn_keys.hor in the settings folder, it was keys.hor next to the exe, which Dusk took for its own keys file there
    private const string KEYS_FILE = "dawn_keys.hor", KEYS_LAYOUT = "keys.hor";

    // What keys.hor says, the label of a command and its key as the menus write it, in the order they were changed
    private readonly List<(string Label, string Key)> rebound = [];

    // What every changed command was on to begin with, to put back
    private readonly Dictionary<string, string> originalKeys = [];

    // The keys that were taken off of something, so a menu put together again (File, with its recent files) doesn't
    // say one that does nothing or something else now
    private readonly HashSet<string> keysTaken = [];

    private string? keysPath;

    // The command a key is being pressed for, while the dialog waits for it
    private MenuItem? keyFor;
    private UIDialog? keyDialog;

    // The window, its list and its filter while it's up
    private WindowPanel? keysWindow;
    private TreeView? keysTree;
    private TextBox? keysFilter;

    private void LoadKeys()
    {
        bool kept = !options.SelfTest && options.Check.Length == 0;
        keysPath = kept ? HexFiles.Kept(KEYS_FILE, was: "keys.hor") : null;
        if (keysPath is null || !File.Exists(keysPath))
            return;

        foreach (var (label, key) in ReadKeys(keysPath))
        {
            if (!Rebind(label, key))
                Say(Said("keys_window.nothing_called", ("label", label), ("key", key)), error: true);
        }
    }

    /// <summary>What a keys.hor says, nothing for one that doesn't read.</summary>
    private List<(string Label, string Key)> ReadKeys(string path)
    {
        var read = new List<(string, string)>();
        try
        {
            HIDLRuntime runtime = new() { Output = null };
            var (success, message) = runtime.Evaluate(File.ReadAllText(path));
            if (!success || runtime.UserScope.Lookup("keys") is not ObjectValue keys)
                throw new Exception(success ? Said("keys_window.needs_keys_object") : message);

            foreach (var (label, key) in keys.Properties)
            {
                if (key is StringValue written)
                    read.Add((label, written.Value));
            }
        }
        catch (Exception e)
        {
            Say(Said("keys_window.unread", ("why", e.Message)), error: true);
        }

        return read;
    }

    private void WriteKeys(string path)
    {
        var keys = new HIDLObject();
        foreach (var (label, key) in rebound)
            keys.Add(label, key);

        string text = new HIDLDocument()
            .Comment("Keyboard shortcuts changed from the defaults, a command by its menu label and the key it is on, \"\" for none.\n" +
                "Help, Keyboard Shortcuts writes this, and it can be edited by hand. Reset All deletes it.")
            .Let("keys", keys)
            .Write();
        File.WriteAllText(path, text);
    }

    private void SaveKeys()
    {
        if (keysPath is null)
            return;

        try
        {
            if (rebound.Count == 0)
                File.Delete(keysPath);
            else
                WriteKeys(keysPath);
        }
        catch (Exception e)
        {
            Say(Said("keys_window.not_written", ("why", e.Message)), error: true);
        }
    }

    /// <summary>Every item of every menu, with the menus it's in, the ones that do something.</summary>
    private IEnumerable<(MenuItem Item, string Path)> Commands() =>
        menu.Menus.SelectMany(bar => bar.Everything()).Where(pair => pair.Item is { IsSeparator: false, Submenu: null, OnPicked: not null });

    private static string LabelOf(MenuItem item) => item.Label.TrimEnd('.');

    /// <summary>
    /// Puts the command a menu calls so on a key ("" for none), its old key and anything else on the new one taken
    /// off. False when there's no such command or no such key.
    /// </summary>
    private bool Rebind(string label, string written)
    {
        string wanted = label.TrimEnd('.');
        if (Commands().FirstOrDefault(pair => LabelOf(pair.Item) == wanted).Item is not { } item)
            return false;

        var to = written.Length > 0 ? UIKeys.Read(written) : null;
        if (written.Length > 0 && to is null)
            return false;

        originalKeys.TryAdd(wanted, UIKeys.Tidy(item.Shortcut) ?? string.Empty);

        // It goes off while typing if its old key did, and never on a key that types something (a letter on its own)
        bool? typing = null;
        if (UIKeys.Read(item.Shortcut) is { } old)
        {
            typing = commands.Keys.Remove(old.Key, old.Control, old.Shift, old.Alt);
            keysTaken.Add(UIKeys.Write(old.Key, old.Control, old.Shift, old.Alt));
        }

        string tidy = string.Empty;
        if (to is { } key)
        {
            tidy = UIKeys.Write(key.Key, key.Control, key.Shift, key.Alt);
            commands.Keys.Remove(key.Key, key.Control, key.Shift, key.Alt);
            bool typesNothing = key.Control || key.Alt || key.Key is >= Key.F1 and <= Key.F25;
            keysTaken.Add(tidy);

            // Whatever else was on the key is written down as off it, so keys.hor says the whole of what changed
            foreach (var (other, _) in Commands())
            {
                if (other == item || UIKeys.Tidy(other.Shortcut) != tidy)
                    continue;

                originalKeys.TryAdd(LabelOf(other), tidy);
                rebound.RemoveAll(pair => pair.Label == LabelOf(other));
                rebound.Add((LabelOf(other), string.Empty));
            }

            MenuItem picked = item;
            commands.Bind(key.Key, () => PickRebound(picked), control: key.Control, whileTyping: (typing ?? true) && typesNothing, shift: key.Shift, alt: key.Alt);
        }

        rebound.RemoveAll(pair => pair.Label == wanted);
        rebound.Add((wanted, tidy));
        MarkReboundKeys();
        return true;
    }

    // A command off its new key, if its menu would let it be picked right now
    private static void PickRebound(MenuItem item)
    {
        if ((item.IsVisible?.Invoke() ?? true) && (item.IsEnabled?.Invoke() ?? true))
            item.OnPicked?.Invoke();
    }

    /// <summary>The keys the menus say, the rebound ones on their commands and nothing on what lost its key.</summary>
    private void MarkReboundKeys()
    {
        if (keysTaken.Count == 0)
            return;

        foreach (var (item, _) in Commands())
        {
            string label = LabelOf(item);
            int at = rebound.FindIndex(pair => pair.Label == label);
            if (at >= 0)
                item.Shortcut = rebound[at].Key;
            else if (item.Shortcut.Length > 0 && UIKeys.Tidy(item.Shortcut) is { } shown && keysTaken.Contains(shown))
                item.Shortcut = string.Empty;
        }
    }

    /* Help, Keyboard Shortcuts */

    private void OpenKeysWindow()
    {
        if (keysWindow is { Visible: true } open)
        {
            open.BringToFront();
            FillKeysTree();
            return;
        }

        UILayout made = layout.Instantiate(KEYS_LAYOUT, chrome.Root);
        var window = keysWindow = made.Get<WindowPanel>("window");
        window.Title = Said("keys.title");
        DressWindow(window);
        window.OnClosed = () =>
        {
            window.Parent?.Remove(window);
            keysWindow = null;
            keysTree = null;
            keysFilter = null;
        };

        keysTree = made.Get<TreeView>("keys_tree");
        keysFilter = made.Get<TextBox>("keys_filter");
        keysFilter.OnChanged = _ => FillKeysTree();
        keysTree.OnActivated = row =>
        {
            if (row.Tag is MenuItem item)
                AskForKey(item);
        };
        made.Get<Button>("keys_reset").OnPressed = PutKeysBack;

        FillKeysTree();
        keysFilter.Focus();
    }

    /// <summary>The commands with their keys, the ones the filter leaves, and the keys that aren't a command's after.</summary>
    private void FillKeysTree()
    {
        if (keysTree is not { } tree)
            return;

        string filter = keysFilter?.Text.Trim() ?? string.Empty;
        bool Wanted(string text) => filter.Length == 0 || text.Contains(filter, StringComparison.OrdinalIgnoreCase);

        var rows = new List<UITreeItem>();
        foreach (var (item, path) in Commands())
        {
            string label = LabelOf(item);
            if (!Wanted($"{path} {label} {item.Shortcut}"))
                continue;

            rows.Add(new UITreeItem(label, item.Icon.Length > 0 ? item.Icon : "symbol-event", item)
            {
                Detail = item.Shortcut.Length > 0 ? item.Shortcut : Said("keys.no_key"),
                Tooltip = path
            });
        }

        // The keys that aren't a command of a menu, said in the texts as lines of `key`  what it does
        foreach (string line in Said("keys.fixed").Split('\n'))
        {
            int gap = line.IndexOf("  ", StringComparison.Ordinal);
            if (!line.StartsWith("- ", StringComparison.Ordinal) || gap < 0)
                continue;

            string keys = line[2..gap].Replace("`", string.Empty), what = line[gap..].Trim();
            if (Wanted($"{keys} {what}"))
                rows.Add(new UITreeItem(what, "lock") { Detail = keys, Color = new Vector4(0.6f, 0.62f, 0.68f, 1.0f), Tooltip = Said("keys.fixed_tip") });
        }

        tree.Items = rows;
        tree.Refresh();
        if (rows.Count > 0 && tree.Selected is null)
            tree.Selected = rows[0];
    }

    /// <summary>A dialog that waits for the key the command goes on, escape or its button to leave it as it was.</summary>
    private void AskForKey(MenuItem item)
    {
        keyFor = item;
        keyDialog = UIDialog.Show(chrome, Said("keys.press_title", ("command", LabelOf(item))),
            Said("keys.press_text", ("key", item.Shortcut.Length > 0 ? item.Shortcut : Said("keys.no_key"))),
            new DialogChoice(Said("keys.leave_it")));
        keyDialog.Closed = () => keyFor = null;
    }

    /// <summary>
    /// Every tick, the window's list sized to it and delete in the list taking a key off, and while a key is being
    /// waited for, the first one pressed that isn't shift, control or alt. True while it waits, the key is for that
    /// and nothing else.
    /// </summary>
    private bool UpdateKeyCapture()
    {
        var keyboard = Engine.Input.Keyboard;
        if (keysWindow is { Visible: true } window && keysTree is { } tree)
        {
            // The list takes what the window has left under the filter and over the buttons
            UIRect room = window.Bounds.Shrink(window.Padding);
            float taken = (keysFilter?.Size.Y ?? 28.0f) + 28.0f + 16.0f + 30.0f;
            if (room.Width > 0 && room.Height > taken)
                tree.Size = new Vector2(room.Width, room.Height - taken);

            if (keyFor is null && compositor.Focus == tree && keyboard.WasPressed(Key.Delete) && tree.Selected is { Tag: MenuItem chosen })
                RemoveKeyOf(chosen);
        }

        if (keyFor is null)
            return false;

        if (keyDialog is not { IsOpen: true })
        {
            keyFor = null;
            return false;
        }

        foreach (Key key in PressableKeys)
        {
            if (!keyboard.WasPressed(key))
                continue;

            if (key == Key.Escape && !keyboard.Control && !keyboard.Shift && !keyboard.Alt)
            {
                keyDialog.Close();
                keyFor = null;
                return true;
            }

            KeyPressedFor(keyFor, key, keyboard.Control, keyboard.Shift, keyboard.Alt);
            break;
        }

        return true;
    }

    private static readonly Key[] PressableKeys = [.. Enum.GetValues<Key>().Distinct().Where(key => !UIKeys.IsHeld(key))];

    /// <summary>The key pressed for a command, put on it and written down.</summary>
    private void KeyPressedFor(MenuItem item, Key key, bool control, bool shift, bool alt)
    {
        string written = UIKeys.Write(key, control, shift, alt);
        string? had = Commands().FirstOrDefault(pair => pair.Item != item && UIKeys.Tidy(pair.Item.Shortcut) == written).Item?.Label.TrimEnd('.');

        keyFor = null;
        keyDialog?.Close();
        if (!Rebind(item.Label, written))
            return;

        SaveKeys();
        FillKeysTree();
        Say(had is null
            ? Said("keys.now", ("command", LabelOf(item)), ("key", written))
            : Said("keys.now_instead", ("command", LabelOf(item)), ("key", written), ("other", had)));
    }

    /// <summary>A command's key taken off, delete on its row.</summary>
    private void RemoveKeyOf(MenuItem item)
    {
        if (item.Shortcut.Length == 0 || !Rebind(item.Label, string.Empty))
            return;

        SaveKeys();
        FillKeysTree();
        Say(Said("keys.removed", ("command", LabelOf(item))));
    }

    /// <summary>Reset All. Every key back where it was, and keys.hor gone.</summary>
    private void PutKeysBack()
    {
        bool changed = rebound.Count > 0;
        foreach (var (label, original) in originalKeys.ToList())
            Rebind(label, original);

        rebound.Clear();
        keysTaken.Clear();
        originalKeys.Clear();
        if (keysPath is not null && File.Exists(keysPath))
            File.Delete(keysPath);

        FillKeysTree();
        Say(Said(changed ? "keys.back" : "keys.back_already"));
    }

    /// <summary>Every tick, whatever keys were pressed set off (see <see cref="UICommands"/>).</summary>
    private void UpdateKeys()
    {
        // A text box of the editor, or one of the layout that is being tried out in the canvas
        // A tree that has the keys for its arrows isn't typing, the editor's shortcuts still go
        bool typing = compositor.Focus is not (null or TreeView) || stage.Focus is not null;

        // While Change a key waits for one, the key is for that and nothing else. While a terminal has the keyboard its
        // keys are the shell's, Ctrl+W is a word rubbed out and not a file closed, the keys of Hex's that have to work
        // from a terminal are terminal commands (DawnScene.Terminal) and go through it
        if (!UpdateKeyCapture() && compositor.Focus is not TerminalView)
            commands.Keys.Update(Engine.Input.Keyboard, typing);
    }
}
