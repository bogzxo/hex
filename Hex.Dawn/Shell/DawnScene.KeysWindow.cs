using System.Numerics;

using Key = Silk.NET.Input.Key;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// File, Keyboard Shortcuts, the window. Every command with its key in a list, typed into to narrow it down, the arrows
// up and down it, enter (or a double click) waits for a new key for the one chosen, delete takes its key off and
// backspace puts back the one it had to begin with. A changed key is in the accent with the accent's bar down its edge,
// the way a changed setting has it, and says what it was. "@modified" in the filter leaves only the changed ones, the
// same word the settings page takes, and the record button (or Alt+K in the filter, VS Code's) has the filter take the
// keys pressed instead of letters and leave the commands on exactly that key, Escape stops that and leaves the window up.
// The funnel beside the filter puts the @ words in, the gear goes to Settings. How the filter reads and how a row looks
// is DawnScene.KeysWindow.Filter, two commands on a key and where a key came from DawnScene.KeysWindow.Conflicts.
// The list is VS Code's table, Command, Keybinding, When and Source, a title pressed sorts by its column and again the
// other way. A key can be a chord, two pressed one after the other ("Ctrl+K Z"), the dialog and the recorder take both.
internal sealed partial class DawnScene
{
    // The word in the filter that leaves only the changed keys
    private const string MODIFIED_TOKEN = "@modified";

    // The window, its list and its filter while it's up
    private WindowPanel? keysWindow;
    private TreeView? keysTree;
    private TextBox? keysFilter;
    private Button? keysRecord;
    private Label? keysHint;
    private Button? keysReset;
    private Button? keysOpenFile;

    // Whether the filter is taking keys pressed rather than letters typed, and what it took last
    private bool keysRecording;

    /// <summary>Keyboard Shortcuts in the File menu, under Settings, VS Code has it there too.</summary>
    private void AddKeysCommands()
    {
        commands.Add("File/settings/prefs:main", Said("commands.shortcuts"), OpenKeysWindow, "Ctrl+K Ctrl+S", "keyboard", UIKeyUse.BoundWhileTyping);
        commands.Add("File/settings/prefs:files", Said("commands.shortcuts_file"), OpenKeysFile, icon: "key");
    }

    /// <summary>
    /// The keys file in a tab like any other, VS Code's Open Keyboard Shortcuts (JSON). Written out first with what
    /// it's for at the top when there's nothing there yet.
    /// </summary>
    private void OpenKeysFile()
    {
        if (keysPath is null)
        {
            Say(Said("keys.no_file"), error: true);
            return;
        }

        if (!File.Exists(keysPath))
        {
            try
            {
                if (Path.GetDirectoryName(keysPath) is { Length: > 0 } folder)
                    Directory.CreateDirectory(folder);
                WriteKeys(keysPath);
            }
            catch (Exception e)
            {
                Say(Said("keys_window.not_written", ("why", e.Message)), error: true);
                return;
            }
        }

        Open(keysPath);
    }

    private void OpenKeysWindow()
    {
        // Not one taken off the screen without closing (everything put away at once), that one is gone however
        // visible it says it is
        if (keysWindow is { Visible: true, Parent: not null } open)
        {
            open.BringToFront();
            FillKeysTree();
            return;
        }

        UILayout made = layout.Instantiate(KEYS_LAYOUT, chrome.Root);
        var window = keysWindow = made.Get<WindowPanel>("window");
        window.Title = Said("keys.title");
        DressWindow(window);

        // As big as the settings window opens, a share of the screen, the list had a long row's key cut off at the fixed
        // size the layout says. Its corner makes it bigger or smaller from there and the list goes with it
        window.Size = KeysWindowSize(window);
        window.OnClosed = () =>
        {
            window.Parent?.Remove(window);
            keysWindow = null;
            keysTree = null;
            keysFilter = null;
            keysRecord = null;
            keysHint = null;
            keysReset = null;
            keysOpenFile = null;
            keysFunnel = null;
            keysSettings = null;
            keysNothingRow = null;
            keysHintFor = null;
            keysRecording = false;
            recordedFirst = null;
            (keysSortColumn, keysSortDown) = (0, false);
        };

        // Escape while it records stops the recording and the window stays, it took the window with it
        window.BeforeEscape = () =>
        {
            if (!keysRecording)
                return false;

            RecordKeys(false);
            return true;
        };

        keysTree = made.Get<TreeView>("keys_tree");

        // VS Code's table, a column each for the command, its key, where it goes off and where the key came from. A title
        // pressed sorts by it, pressed again the other way
        keysTree.Columns =
        [
            new UITreeColumn(Said("keys_table.command"), 0.47f),
            new UITreeColumn(Said("keys_table.keybinding"), 0.22f),
            new UITreeColumn(Said("keys_table.when"), 0.17f),
            new UITreeColumn(Said("keys_table.source"), 0.14f)
        ];
        keysTree.OnHeaderPressed = SortKeysBy;
        keysFilter = made.Get<TextBox>("keys_filter");
        keysRecord = made.Get<Button>("keys_record");
        keysHint = made.Get<Label>("keys_hint");
        keysHint.Color = DimColor;
        keysFilter.OnChanged = _ => FillKeysTree();

        // While it records keys the box types nothing, the keys pressed are written into it by UpdateKeysWindow. Nor with
        // Alt down, or the k of Alt+K went into it as recording stopped
        keysFilter.TakeKey = _ => keysRecording || Engine.Input.Keyboard.Alt;

        // The filter walks the list the way the palette's box does, up and down and the pages, and Enter sets a key for
        // the row it's on. The arrows did nothing there and the list had to be tabbed to
        keysFilter.OnKey = key =>
        {
            int by = key switch { UIKeyboard.UP => -1, UIKeyboard.DOWN => 1, UIKeyboard.PAGE_UP => -10, UIKeyboard.PAGE_DOWN => 10, _ => 0 };
            if (by == 0 || keysTree is not { Items.Count: > 0 } list || keysNothingRow is not null)
                return;

            int at = list.Selected is { } picked ? list.Items.IndexOf(picked) : -1;
            list.Selected = list.Items[Math.Clamp(at + by, 0, list.Items.Count - 1)];
        };
        keysFilter.OnSubmitted = _ =>
        {
            if (keysTree?.Selected is { Tag: MenuItem item })
                AskForKey(item);
        };
        keysTree.OnActivated = row =>
        {
            if (row.Tag is MenuItem item)
                AskForKey(item);
        };
        keysTree.OnItemContextMenu = (row, _) =>
        {
            if (row.Tag is MenuItem item)
            {
                keysTree.Selected = row;
                ShowKeyMenu(item);
            }
        };
        keysRecord.OnPressed = () => RecordKeys(!keysRecording);
        keysReset = made.Get<Button>("keys_reset");
        keysReset.OnPressed = PutKeysBack;
        keysOpenFile = made.Get<Button>("keys_open_file");
        keysOpenFile.OnPressed = OpenKeysFile;

        // The funnel straight after the filter the way the settings page has it, the gear at the end of the row
        var search = made.Get<StackPanel>("keys_search");
        keysFunnel = search.Insert(1, new Button
        {
            Size = new Vector2(28.0f, 28.0f),
            Icon = "filter",
            Style = "button_flat",
            Tooltip = Said("keys_conflicts.filter_tip"),
            OnPressed = ShowKeysFilters
        });
        keysSettings = search.Add(new Button
        {
            Size = new Vector2(28.0f, 28.0f),
            Icon = "settings-gear",
            Style = "button_flat",
            Tooltip = Said("keys_conflicts.settings_tip"),
            OnPressed = () => OpenSettings()
        });

        FillKeysTree();
        keysFilter.Focus();
    }

    // Whether a built-in row's keys are keys alone, drawn as keycaps, and not words about them ("Arrow keys", "Tab, Shift+Tab",
    // "Ctrl+Mouse wheel"), which stay words
    private static bool KeysOnly(string keys) =>
        keys.Length > 0 && !keys.Contains(',') && keys.Split([' ', '+'], StringSplitOptions.RemoveEmptyEntries).All(part => !char.IsLower(part[0]));

    // The size the keys window opens at, the settings window's for the room there is
    private Vector2 KeysWindowSize(WindowPanel window) =>
        Horizon.UI.Components.SettingsWindow.SizeFor(chrome.Root.Bounds.Size, window.MinSize, Horizon.UI.Components.SettingsWindow.ScaleIn(chrome));

    /// <summary>The commands with their keys, the ones the filter leaves, and the keys that aren't a command's after.</summary>
    private void FillKeysTree()
    {
        if (keysTree is not { } tree)
            return;

        // "@modified", "@conflicts", "@source:" and "@when:" anywhere in it leave the changed ones, the ones on a key in
        // conflict, the ones from there and the ones of a place, the rest of it narrows those down as usual, every word
        // somewhere in the row and in any order.
        // Recording, the filter is a key and a command is on it or isn't
        string typed = keysFilter?.Text.Trim() ?? string.Empty;
        string? recorded = keysRecording ? UIKeys.Tidy(typed) : null;
        KeysAsked asked = keysRecording ? new KeysAsked([], false, false, null, false) : ReadKeysFilter(typed);
        string[] words = asked.Words;

        bool Wanted(string keys, params string[] texts)
        {
            if (keysRecording)
                return recorded is null || keys.Split(',', StringSplitOptions.TrimEntries).Any(key => UIKeys.Tidy(key) == recorded);

            return SaysEvery(words, [.. texts, keys]);
        }

        // By category and then by name, the Git commands together the way the menus have them. The source is a word of
        // the row as well, "user" finds the changed ones
        // Where the key goes off is a word of the row too, "designer" finds the designer's
        var shared = SharedKeys();
        var rows = new List<UITreeItem>();
        foreach (var (item, path) in Commands().OrderBy(pair => CategoryOf(pair.Path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => LabelOf(pair.Item), StringComparer.OrdinalIgnoreCase))
        {
            string label = LabelOf(item);
            KeySource source = SourceOf(item);
            KeyWhen when = WhenOf(item);
            var (others, elsewhere) = AlsoOn(shared, HolderOf(item));
            if ((asked.Modified && source != KeySource.User) || (asked.Conflicts && others.Count == 0) || asked.Unknown
                || (asked.Source is { } from && from != source) || (asked.When is { } place && place != when)
                || !Wanted(item.Shortcut, CategoryOf(path), label, SourceSaid(source), WhenSaid(when)))
                continue;

            rows.Add(KeysRow(item, path, words, others, elsewhere));
        }

        rows.AddRange(ExtensionRows(asked, Wanted, shared));

        // The keys that aren't a command of a menu, said in the texts as lines of `key`  what it does. None of them is
        // ever changed or a conflict, and they're the default ones
        if (!asked.Modified && !asked.Conflicts && !asked.Unknown && asked.When is null && asked.Source is null or KeySource.Default)
        {
            string builtIn = Said("keys.built_in"), defaults = SourceSaid(KeySource.Default);
            foreach (string line in Said("keys.fixed").Split('\n'))
            {
                int gap = line.IndexOf("  ", StringComparison.Ordinal);
                if (!line.StartsWith("- ", StringComparison.Ordinal) || gap < 0)
                    continue;

                string keys = line[2..gap].Replace("`", string.Empty), what = line[gap..].Trim();
                string text = $"{builtIn}: {what}";
                if (Wanted(keys, builtIn, what))
                    rows.Add(new UITreeItem(text, "lock") { Detail = keys, DetailKeys = KeysOnly(keys), Color = DimColor, Tooltip = Said("keys.fixed_tip"), Badge = defaults, Lit = KeysLit(words, text) });
            }
        }

        // In the order of the column whose title was pressed last, the commands by name to begin with
        rows = SortedKeys(rows);
        tree.SortColumn = keysSortColumn;
        tree.SortDescending = keysSortDown;

        // Nothing left says so, rather than an empty list that looks broken
        keysNothingRow = null;
        if (rows.Count == 0)
            rows.Add(NothingMatches(typed));

        tree.Items = rows;
        tree.Refresh();
        if (keysNothingRow is not null)
            tree.Selected = null;
        else if (tree.Selected is null || !rows.Contains(tree.Selected))
            tree.Selected = rows[0];

        // The line under the list goes with the row picked now, not the one of the rows before
        ShowKeysHint(tree.Selected);
    }

    /// <summary>Whether the filter takes the keys pressed (and narrows the list to exactly that key) or letters.</summary>
    private void RecordKeys(bool on)
    {
        keysRecording = on;
        if (keysFilter is { } filter)
        {
            filter.Text = string.Empty;
            filter.Placeholder = Said(on ? "keys.record_placeholder" : "keys.filter");
            filter.Focus();
        }

        if (keysRecord is { } button)
            button.Selected = on;

        recordedFirst = null;
        FillKeysTree();
    }

    // The key recorded last while a second one could still make a chord of it, and when
    private UIKeyStroke? recordedFirst;
    private double recordedAt;

    /// <summary>
    /// A key pressed in the filter while it records, written the way the menus write it. One pressed soon enough after
    /// another makes a chord of the two ("Ctrl+K Ctrl+C"), Enter keeps the first on its own.
    /// </summary>
    private void KeyRecorded(Key key, bool control, bool shift, bool alt)
    {
        if (keysFilter is not { } filter)
            return;

        var stroke = new UIKeyStroke(key, control, shift, alt);
        if (recordedFirst is { } first && SecondsNow() - recordedAt <= CHORD_CAPTURE)
        {
            recordedFirst = null;
            if (key is Key.Enter or Key.KeypadEnter && !control && !shift && !alt)
                return;

            filter.Text = UIKeys.WriteChord(first, stroke);
        }
        else
        {
            recordedFirst = stroke;
            recordedAt = SecondsNow();
            filter.Text = stroke.ToString();
        }

        FillKeysTree();
    }

    /// <summary>Show Same Keys on a row, the filter recording and on that row's key, every command on it left.</summary>
    private void ShowSameKeys(MenuItem item)
    {
        if (keysFilter is not { } filter || item.Shortcut.Length == 0)
            return;

        RecordKeys(true);
        filter.Text = UIKeys.Tidy(item.Shortcut) ?? item.Shortcut;
        FillKeysTree();
    }

    /// <summary>A right click on a row, what can be done to its key.</summary>
    private void ShowKeyMenu(MenuItem item) => ContextMenu.ShowAt(chrome, compositor.Pointer.Position, KeyMenu(item));

    /// <summary>What can be done to a command's key, Reset to Default only on one that was changed.</summary>
    private Menu KeyMenu(MenuItem item)
    {
        string label = LabelOf(item);
        var shown = new Menu("keys");
        shown.Add(Said("keys.change"), () => AskForKey(item), "Enter", "edit");
        shown.Add(Said("keys.remove"), () => RemoveKeyOf(item), "Del", "close").IsEnabled = () => item.Shortcut.Length > 0;

        // On a key another command would want at the same time, the key can come off that one instead, one item each
        foreach (MenuItem other in ClashesWith(item))
            shown.Add(Said("keys_table.remove_from", ("other", LabelOf(other))), () => RemoveKeyOf(other), icon: "close");

        if (rebound.Any(pair => pair.Label == label))
            shown.Add(Said("keys.reset_one"), () => ResetKeyOf(item), "Backspace", "discard");
        shown.Add(Said("keys.same_keys"), () => ShowSameKeys(item), icon: "record-keys").IsEnabled = () => item.Shortcut.Length > 0;
        shown.Add(Said("keys.copy_key"), () => UIClipboard.Copy(item.Shortcut), icon: "copy").IsEnabled = () => item.Shortcut.Length > 0;
        shown.Add(Said("keys.copy_name"), () => UIClipboard.Copy(label), icon: "copy");
        return shown;
    }

    /// <summary>A dialog that waits for the key the command goes on, escape or its button to leave it as it was.</summary>
    private void AskForKey(MenuItem item)
    {
        keyFor = item;
        keyJustAsked = true;
        keyFirst = null;
        keyChordLine = null;
        keyDialog = UIDialog.Show(chrome, Said("keys.press_title", ("command", LabelOf(item))),
            Said("keys.press_text", ("key", item.Shortcut.Length > 0 ? item.Shortcut : Said("keys.no_key"))),
            new DialogChoice(Said("keys.leave_it")));
        keyDialog.Closed = () => keyFor = null;
    }

    /// <summary>
    /// Every tick while the window is up, its list and filter sized to it, delete and backspace in the list, and the
    /// keys pressed into the filter while it records. True when a key went to the window and nothing else should have it.
    /// </summary>
    private bool UpdateKeysWindow()
    {
        if (keysWindow is not { Visible: true } window || keysTree is not { } tree)
            return false;

        // The list takes what the window has left under the filter and over the buttons, the filter and the hint are as
        // wide as the window leaves them beside their buttons
        UIRect room = window.Bounds.Shrink(window.Padding);
        float taken = (keysFilter?.Size.Y ?? 28.0f) + 28.0f + 16.0f + 30.0f;
        if (room.Width > 0 && room.Height > taken)
        {
            tree.Size = new Vector2(room.Width, room.Height - taken);
            if (keysFilter is { } filter)
            {
                float buttons = (keysRecord?.Size.X ?? 0.0f) + (keysFunnel?.Size.X ?? 0.0f) + (keysSettings?.Size.X ?? 0.0f);
                filter.Size = filter.Size with { X = MathF.Max(80.0f, room.Width - buttons - 24.0f) };
            }
            if (keysHint is { } hint)
                hint.Size = hint.Size with { X = MathF.Max(80.0f, room.Width - (keysReset?.Size.X ?? 0.0f) - (keysOpenFile?.Size.X ?? 0.0f) - 16.0f) };
        }

        ShowKeysHint(tree.Selected);

        if (keyFor is not null)
            return false;

        var keyboard = Engine.Input.Keyboard;
        if (compositor.Focus == tree && tree.Selected is { Tag: MenuItem chosen })
        {
            if (keyboard.WasPressed(Key.Delete))
            {
                RemoveKeyOf(chosen);
                return true;
            }

            if (keyboard.WasPressed(Key.Backspace))
            {
                ResetKeyOf(chosen);
                return true;
            }
        }

        if (keysFilter is not { } box || compositor.Focus != box)
            return false;

        // Alt+K in the filter starts and stops recording, VS Code's key for it
        if (keyboard.WasPressed(Key.K) && keyboard.Alt && !keyboard.Control && !keyboard.Shift)
        {
            RecordKeys(!keysRecording);
            return true;
        }

        if (!keysRecording)
            return false;

        // Recording, the keys are the filter's and set nothing off, Ctrl+S is a key looked for and not a file saved.
        // Escape still closes the window
        foreach (Key key in PressableKeys)
        {
            if (key == Key.Escape || !keyboard.WasPressed(key))
                continue;

            KeyRecorded(key, keyboard.Control, keyboard.Shift, keyboard.Alt);
            break;
        }

        return true;
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

    /// <summary>
    /// Reset to Default on a row, the key it had to begin with put back on that command and nothing else. It's no
    /// longer a changed one, so keys.hor forgets it.
    /// </summary>
    private void ResetKeyOf(MenuItem item)
    {
        string label = LabelOf(item);
        if (!rebound.Any(pair => pair.Label == label) || !originalKeys.TryGetValue(label, out string? original))
            return;

        if (!Rebind(label, original))
            return;

        // Back on its own key it's as it always was, the key isn't one taken off of anything now, or the next menu put
        // together again would take it off this command as well
        rebound.RemoveAll(pair => pair.Label == label);
        originalKeys.Remove(label);
        if (UIKeys.Tidy(original) is { } tidy)
            keysTaken.Remove(tidy);

        SaveKeys();
        FillKeysTree();
        Say(Said("keys.reset_done", ("command", label), ("key", original.Length > 0 ? original : Said("keys.no_key"))));
    }

    /// <summary>Reset All. Every key back where it was, and keys.hor gone.</summary>
    private void PutKeysBack()
    {
        // Each back on its own key without taking it off anything, two that came with the same key go back to sharing it
        bool changed = rebound.Count > 0;
        foreach (var (label, original) in originalKeys.ToList())
            Rebind(label, original, keepOthers: true);

        rebound.Clear();
        keysTaken.Clear();
        originalKeys.Clear();
        if (keysPath is not null && File.Exists(keysPath))
            File.Delete(keysPath);

        FillKeysTree();
        Say(Said(changed ? "keys.back" : "keys.back_already"));
    }
}
