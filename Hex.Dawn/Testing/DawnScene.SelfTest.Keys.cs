using System.Numerics;

using Key = Silk.NET.Input.Key;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test, keyboard shortcuts changed (Help, Keyboard Shortcuts, and keys.hor), and put back after.
internal sealed partial class DawnScene
{
    private void TestKeys(HexPointerScript test)
    {
        MenuItem? Item(string label) => Commands().FirstOrDefault(pair => LabelOf(pair.Item) == label).Item;
        static string Name(UITreeItem row) => row.Tag is MenuItem item ? LabelOf(item) : row.Text;

        test.Check("keys: the menus' keys read and write back as they're written, the odd ones among them", () =>
            new[] { "Ctrl+Shift+F", "Ctrl+\\", "Ctrl+,", "Ctrl+Shift+[", "Del", "Esc", "Shift+F12", "Ctrl+0", "Alt+Up", "Ctrl+=" }
                .All(written => UIKeys.Tidy(written) == written)
            && UIKeys.Read("Ctrl+Nope") is null && UIKeys.Read("Ctrl+F+G") is null && UIKeys.Tidy("alt+ctrl+f") == "Ctrl+Alt+F");

        // The window, every command in a list, narrowed by the filter, the arrows go about it
        test.Run(OpenKeysWindow);
        test.Settle(0.3f);
        test.Check("keys: the shortcuts window lists every command with its key, and the keys that aren't a command's", () =>
            keysWindow is { Visible: true } && keysTree is { } tree && tree.Items.Count > Commands().Count()
            && tree.Items.Any(row => Name(row) == "Find in Files" && row.Detail == "Ctrl+Shift+F") && tree.Items.Any(row => row.Tag is null && row.Detail.Contains("Ctrl+Space")));
        test.Run(() =>
        {
            keysFilter!.Text = "find in";
            keysFilter.OnChanged!(keysFilter.Text);
        });
        test.Wait();
        test.Check("keys: typed into, the filter leaves the commands that say it", () =>
            keysTree!.Items.Count >= 1 && keysTree.Items.Any(row => Name(row) == "Find in Files")
            && keysTree.Items.All(row => SaysEvery(["find", "in"], row.Text, row.Detail)));

        // Enter on a row waits for a key, the way the dialog takes it
        test.Run(() => keysTree!.OnActivated!(keysTree.Items.First(row => Name(row) == "Find in Files")));
        test.Wait();
        test.Check("keys: enter on a command waits for the key it goes on", () => keyFor is not null && keyDialog is { IsOpen: true });
        test.Run(() => KeyPressedFor(Item("Find in Files")!, Key.F, control: true, shift: false, alt: true));
        test.Wait();
        test.Check("keys: a command put on another key says so in its menu, the palette and the list, and its old key does nothing", () =>
            Item("Find in Files")!.Shortcut == "Ctrl+Alt+F" && AllCommands().Any(command => command.Text.EndsWith("Find in Files...   (Ctrl+Alt+F)", StringComparison.Ordinal))
            && keysTree!.Items.Any(row => Name(row) == "Find in Files" && row.Detail == "Ctrl+Alt+F")
            && commands.Keys.Has(Key.F, true, false, true) && !commands.Keys.Has(Key.F, true, true, false) && keyFor is null && keyDialog is not { IsOpen: true });

        // Something else onto a key that's somebody's asks first, Replace takes it off the other, and delete takes a key off
        test.Run(() => KeyPressedFor(Item("Go to File")!, Key.F, control: true, shift: false, alt: true));
        test.Wait();
        test.Check("keys: a key that something else has asks Replace, Keep Both or Cancel before taking it", () =>
            keyClash is { } clash && clash.Others is ["Find in Files"] && keyClashDialog is { IsOpen: true } dialog
            && dialog.Buttons.Select(button => button.Label).SequenceEqual([Said("keys_conflicts.replace"), Said("keys_conflicts.keep_both"), Said("keys_conflicts.cancel")])
            && Item("Go to File")!.Shortcut == "Ctrl+P");
        test.Run(() => ResolveKeyClash(KeyClash.Replace));
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

        TestKeysWindow(test, Item, file);
        TestKeysFilter(test, Item);
        TestKeysConflicts(test, Item);
        TestKeysWhen(test, Item);
        TestKeysTable(test, Item);
        test.Run(() => keysWindow?.OnClosed?.Invoke());
        test.Wait();
    }

    /// <summary>The window's own conveniences, File beside Settings, the changed ones marked, @modified, record keys, Reset to Default.</summary>
    private void TestKeysWindow(HexPointerScript test, Func<string, MenuItem?> command, string file)
    {
        void Filter(string text)
        {
            keysFilter!.Text = text;
            keysFilter.OnChanged!(keysFilter.Text);
        }

        test.Check("keys: Keyboard Shortcuts is in File, Preferences, beside Settings", () =>
        {
            var items = menu.Menus.First(bar => bar.Title == "File").Items.First(item => item.Label == Said("menus.preferences")).Submenu!.Items.ToList();
            int settingsAt = items.FindIndex(item => item.Label == "Settings..."), keysAt = items.FindIndex(item => item.Label == Said("commands.shortcuts"));
            return settingsAt >= 0 && keysAt > settingsAt && !items.Skip(settingsAt).Take(keysAt - settingsAt).Any(item => item.IsSeparator)
                && !menu.Menus.First(bar => bar.Title == "Help").Items.Any(item => item.Label == Said("commands.shortcuts"));
        });

        // The settings window's room, not the layout's fixed size, and its corner makes it bigger with the list filling it
        bool TreeFills() => keysWindow is { } window && keysTree is { } tree && window.Bounds.Shrink(window.Padding) is var room
            && MathF.Abs(tree.Size.X - room.Width) < 1.0f && tree.Bounds.Max.Y <= room.Max.Y + 1.0f && tree.Size.Y > room.Height * 0.6f;
        test.Check("keys window: opens at the settings window's size, the list filling it", () =>
            keysWindow is { ResizeGrip: true } window && window.Size == KeysWindowSize(window) && window.Size.X > 430.0f && TreeFills());
        Vector2 opened = Vector2.Zero;
        test.Run(() =>
        {
            opened = keysWindow!.Size;
            keysWindow.Size = keysWindow.MinSize + new Vector2(140.0f, 60.0f);
        });
        test.Settle(0.2f);
        test.Check("keys window: made smaller by its corner, the list goes with it", () =>
            keysWindow!.Size == keysWindow.MinSize + new Vector2(140.0f, 60.0f) && TreeFills());
        test.Run(() => keysWindow!.Size = opened);
        test.Settle(0.2f);
        test.Check("keys window: and back to its size, the list as wide as the window again", TreeFills);

        // Go to File onto a key of its own, the only changed one, marked in the accent and saying what it was
        string goTo = UIKeys.Write(Key.F9, true, true, true), split = UIKeys.Write(Key.F10, true, true, true);
        test.Run(() =>
        {
            Filter(string.Empty);
            KeyPressedFor(command("Go to File")!, Key.F9, control: true, shift: true, alt: true);
            Filter("@modified");
        });
        test.Wait();
        test.Check("keys: a changed key is marked and @modified leaves only it", () =>
            keysTree!.Items is [{ Tag: MenuItem } row] && LabelOf((MenuItem)row.Tag) == "Go to File" && row.Detail == goTo && row.DetailColor == compositor.Skin?.AccentColor
            && row.IconColor == compositor.Skin?.AccentColor && row.Tooltip.Contains(Said("keys.changed_tip", ("key", "Ctrl+P"))));
        test.Check("keys: a row's menu changes, removes, finds the same keys and copies, and resets only a changed key", () =>
            KeyMenu(command("Go to File")!).Items.Select(item => item.Label)
                .SequenceEqual([Said("keys.change"), Said("keys.remove"), Said("keys.reset_one"), Said("keys.same_keys"), Said("keys.copy_key"), Said("keys.copy_name")])
            && !KeyMenu(command("Find in Files")!).Items.Any(item => item.Label == Said("keys.reset_one")));
        test.Run(() => Filter("go to"));
        test.Settle(0.2f);
        test.Run(() => Shot("window-keys-changed"));
        test.Run(() => Filter("find in"));
        test.Wait();
        test.Check("keys: a key that isn't changed is in the text's colour and not the accent", () =>
            keysTree!.Items.FirstOrDefault(row => row.Tag is MenuItem item && LabelOf(item) == "Find in Files") is { } row && row.DetailColor == compositor.Skin?.TextColor && row.IconColor is null
            && row.Edge is null && row.Badge == Said("keys_conflicts.source_default") && !row.Tooltip.Contains(Said("keys.changed_tip", ("key", string.Empty)).TrimEnd()));

        // Record keys, the filter takes the key pressed and leaves exactly the commands on it
        test.Run(() =>
        {
            RecordKeys(true);
            KeyRecorded(Key.F, control: true, shift: true, alt: false);
        });
        test.Wait();
        test.Check("keys: record keys writes the key pressed into the filter and leaves the commands on exactly that key", () =>
            keysRecording && keysRecord!.Selected && keysFilter!.Text == "Ctrl+Shift+F" && keysTree!.Items.Count >= 1
            && keysTree.Items.Any(row => row.Tag is MenuItem item && LabelOf(item) == "Find in Files") && keysTree.Items.All(row => UIKeys.Tidy(row.Detail) == "Ctrl+Shift+F"));
        test.Settle(0.2f);
        test.Run(() => Shot("window-keys-record"));
        test.Run(() => RecordKeys(false));
        test.Wait();
        test.Check("keys: record keys off, the filter types again", () => !keysRecording && !keysRecord!.Selected && keysFilter!.Text.Length == 0);

        // Split Editor changed as well, then Go to File reset on its own row, Split Editor keeps its new key
        List<(string Label, string Key)> read = [];
        test.Run(() =>
        {
            KeyPressedFor(command("Split Editor")!, Key.F10, control: true, shift: true, alt: true);
            ResetKeyOf(command("Go to File")!);
            MarkReboundKeys();
            WriteKeys(file);
            read = ReadKeys(file);
        });
        test.Wait();
        test.Check("keys: Reset to Default on a row puts back that key and nothing else", () =>
            command("Go to File")!.Shortcut == "Ctrl+P" && commands.Keys.Has(Key.P, true, false, false) && !commands.Keys.Has(Key.F9, true, true, true)
            && command("Split Editor")!.Shortcut == split && commands.Keys.Has(Key.F10, true, true, true)
            && rebound is [("Split Editor", var kept)] && kept == split && read is [("Split Editor", var written)] && written == split);
        test.Run(PutKeysBack);
        test.Wait();
    }

    /// <summary>The filter the way the settings page filters, categories, two commands on one key, Show Same Keys, nothing found, the keys file.</summary>
    private void TestKeysFilter(HexPointerScript test, Func<string, MenuItem?> command)
    {
        void Filter(string text)
        {
            keysFilter!.Text = text;
            keysFilter.OnChanged!(keysFilter.Text);
        }

        UITreeItem? Row(string label) => keysTree!.Items.FirstOrDefault(row => row.Tag is MenuItem item && LabelOf(item) == label);

        // Every word somewhere in the row, in any order, "commit git" is Git's Commit and so is "files find" Find in Files
        test.Run(() => Filter("commit git"));
        test.Wait();
        test.Check("keys: filter: words in any order", () =>
            keysTree!.Items.Any(row => row.Text.StartsWith("Git: Commit", StringComparison.Ordinal))
            && keysTree.Items.All(row => SaysEvery(["commit", "git"], row.Text, row.Detail))
            && TreeAfter("files find").Any(row => row.Tag is MenuItem item && LabelOf(item) == "Find in Files"));

        // The menu a command is in is written before it and found by the filter, the Git commands are all there and
        // nothing of another menu unless it says git itself
        test.Run(() => Filter("git"));
        test.Wait();
        test.Check("keys: filter: category git lists git commands", () =>
        {
            var git = Commands().Where(pair => CategoryOf(pair.Path) == "Git").Select(pair => LabelOf(pair.Item)).ToHashSet();
            var shown = keysTree!.Items.Where(row => row.Tag is MenuItem).ToList();
            return git.Count > 0 && git.All(label => Row(label) is not null)
                && shown.All(row => row.Text.StartsWith("Git: ", StringComparison.Ordinal) || LabelOf((MenuItem)row.Tag!).Contains("git", StringComparison.OrdinalIgnoreCase))
                && shown.Select(row => row.Text).SequenceEqual(shown.Select(row => row.Text).OrderBy(text => text, StringComparer.OrdinalIgnoreCase));
        });
        test.Run(() => Filter(string.Empty));
        test.Wait();
        test.Check("keys: every row starts with its category, the commands in category order", () =>
        {
            var pathOf = new Dictionary<MenuItem, string>();
            foreach (var (item, path) in Commands())
                pathOf.TryAdd(item, path);

            var shown = keysTree!.Items.Where(row => row.Tag is MenuItem).ToList();
            var categories = shown.Select(row => CategoryOf(pathOf[(MenuItem)row.Tag!])).ToList();
            return shown.Count == Commands().Count()
                && shown.All(row => row.Text == $"{CategoryOf(pathOf[(MenuItem)row.Tag!])}: {LabelOf((MenuItem)row.Tag!)}")
                && categories.SequenceEqual(categories.OrderBy(category => category, StringComparer.OrdinalIgnoreCase));
        });

        // Two commands on one key are both in the warning colour and name each other, one moved off it clears both
        string shared = UIKeys.Write(Key.F11, true, true, true), goToKey = string.Empty, splitKey = string.Empty;
        test.Run(() =>
        {
            (goToKey, splitKey) = (command("Go to File")!.Shortcut, command("Split Editor")!.Shortcut);
            command("Go to File")!.Shortcut = shared;
            command("Split Editor")!.Shortcut = shared;
            Filter(string.Empty);
        });
        test.Wait();
        test.Check("keys: duplicates: two commands on one key are both marked and name each other", () =>
            Row("Go to File") is { } goTo && Row("Split Editor") is { } split && goTo.DetailColor == WarningColor && split.DetailColor == WarningColor
            && goTo.Tooltip.Contains(Said("keys_conflicts.conflict_tip", ("key", shared), ("other", "Split Editor")))
            && split.Tooltip.Contains(Said("keys_conflicts.conflict_tip", ("key", shared), ("other", "Go to File"))));
        test.Run(() =>
        {
            command("Go to File")!.Shortcut = goToKey;
            command("Split Editor")!.Shortcut = splitKey;
            Filter(string.Empty);
        });
        test.Wait();
        test.Check("keys: duplicates: one moved off the key and neither is marked", () =>
            Row("Go to File") is { } goTo && Row("Split Editor") is { } split && goTo.DetailColor != WarningColor && split.DetailColor != WarningColor
            && !goTo.Tooltip.Contains(Said("keys_conflicts.conflict_tip", ("key", shared), ("other", "Split Editor"))));

        // Show Same Keys on a row records and puts that row's key in the filter
        test.Run(() => KeyMenu(command("Find in Files")!).Items.First(item => item.Label == Said("keys.same_keys")).OnPicked!());
        test.Wait();
        test.Check("keys: same keys: menu entry filters by the row key", () =>
            keysRecording && keysFilter!.Text == "Ctrl+Shift+F" && Row("Find in Files") is not null
            && keysTree!.Items.All(row => UIKeys.Tidy(row.Detail) == "Ctrl+Shift+F"));
        test.Run(() => RecordKeys(false));
        test.Wait();

        // A filter that leaves nothing says so in one dim row nothing can be done to
        test.Run(() => Filter("zzqx nothing like it"));
        test.Wait();
        test.Check("keys: empty: says nothing matches", () =>
            keysTree!.Items is [{ Tag: null } row] && row.Text == Said("keys.nothing", ("filter", "zzqx nothing like it")) && row.Color == DimColor
            && keysTree.Selected is null);
        test.Settle(0.2f);
        test.Run(() => Shot("window-keys-nothing"));
        test.Settle(0.2f);
        test.Run(() => Filter(string.Empty));

        // Open Keyboard Shortcuts File, written out with its comment when there's none and opened in a tab
        string file = Path.Combine(testFolder, "selftest_dawn_keys.hor");
        HexDocument? before = null;
        test.Run(() =>
        {
            before = document;
            if (File.Exists(file))
                File.Delete(file);
            keysPath = file;
        });
        Pick("File", () => Said("commands.shortcuts_file"));
        test.Wait();
        test.Check("keys: Open Keyboard Shortcuts File opens the keys file in a tab, written out when there was none", () =>
            document.Path is { } path && Path.GetFullPath(path) == Path.GetFullPath(file) && documents.Contains(document)
            && document.Editor.Text.Contains("Keyboard shortcuts") && document.Editor.Text.Contains("let keys"));
        test.Run(() =>
        {
            if (document.Path is { } path && Path.GetFullPath(path) == Path.GetFullPath(file))
                Close();
            keysPath = null;
            if (before is not null && documents.Contains(before))
                Show(before);
        });
        test.Wait();
    }

    /// <summary>
    /// Two commands on one key the way VS Code has it. Asked before a key is taken, Cancel and Keep Both and Replace each
    /// doing what they say, the warning's icon and @conflicts, the source of a key and @source:, the accent's bar and the
    /// words lit, the funnel and the gear, and Escape while recording.
    /// </summary>
    private void TestKeysConflicts(HexPointerScript test, Func<string, MenuItem?> command)
    {
        void Filter(string text)
        {
            keysFilter!.Text = text;
            keysFilter.OnChanged!(keysFilter.Text);
        }

        UITreeItem? Row(string label) => keysTree!.Items.FirstOrDefault(row => row.Tag is MenuItem item && LabelOf(item) == label);
        bool Clashes(UITreeItem row) => row.Icon == "warning" && row.IconColor == WarningColor && row.DetailColor == WarningColor;
        Vector4? Accent() => compositor.Skin?.AccentColor;

        // Go to File onto Find in Files' key, asked, and Cancel leaves both where they were
        test.Run(() =>
        {
            Filter(string.Empty);
            KeyPressedFor(command("Go to File")!, Key.F, control: true, shift: true, alt: false);
        });
        test.Wait();
        test.Check("keys: conflicts: a used key asks first and takes nothing yet", () =>
            keyClashDialog is { IsOpen: true } && keyClash is { Key: "Ctrl+Shift+F" } && command("Go to File")!.Shortcut == "Ctrl+P"
            && command("Find in Files")!.Shortcut == "Ctrl+Shift+F" && rebound.Count == 0);
        test.Run(() => ResolveKeyClash(KeyClash.Cancel));
        test.Wait();
        test.Check("keys: conflicts: Cancel changes nothing", () =>
            keyClash is null && keyClashDialog is not { IsOpen: true } && command("Go to File")!.Shortcut == "Ctrl+P"
            && command("Find in Files")!.Shortcut == "Ctrl+Shift+F" && rebound.Count == 0 && commands.Keys.Has(Key.P, true, false, false));

        // Keep Both, both on it, both with the warning's icon, and @conflicts leaves only the ones on a shared key
        test.Run(() =>
        {
            KeyPressedFor(command("Go to File")!, Key.F, control: true, shift: true, alt: false);
            ResolveKeyClash(KeyClash.KeepBoth);
            Filter(string.Empty);
        });
        test.Wait();
        test.Check("keys: conflicts: Keep Both leaves the key on both, the two rows marked", () =>
            command("Go to File")!.Shortcut == "Ctrl+Shift+F" && command("Find in Files")!.Shortcut == "Ctrl+Shift+F"
            && commands.Keys.Has(Key.F, true, true, false) && !commands.Keys.Has(Key.P, true, false, false)
            && rebound is [("Go to File", "Ctrl+Shift+F")] && keyClashDialog is not { IsOpen: true }
            && Row("Go to File") is { } goTo && Clashes(goTo) && Row("Find in Files") is { } find && Clashes(find)
            && find.Tooltip.Contains(Said("keys_conflicts.conflict_tip", ("key", "Ctrl+Shift+F"), ("other", "Go to File"))));
        int clashing = 0;
        test.Run(() =>
        {
            clashing = keysTree!.Items.Count(Clashes);
            Filter(CONFLICTS_TOKEN);
        });
        test.Wait();
        test.Check("keys: conflicts: @conflicts leaves only the commands on a shared key", () =>
            keysTree!.Items.Count == clashing && keysTree.Items.All(Clashes) && Row("Go to File") is not null && Row("Find in Files") is not null
            && Row("Split Editor") is null);
        test.Settle(0.2f);
        test.Run(() => Shot("window-keys-conflicts"));

        // Reset All with the two sharing, each back on its own key, Find in Files' key works again
        test.Run(PutKeysBack);
        test.Wait();
        test.Check("keys: conflicts: reset after Keep Both puts both back, the key still Find in Files'", () =>
            command("Go to File")!.Shortcut == "Ctrl+P" && command("Find in Files")!.Shortcut == "Ctrl+Shift+F"
            && commands.Keys.Has(Key.F, true, true, false) && commands.Keys.Has(Key.P, true, false, false) && rebound.Count == 0);

        // Replace takes it off the other one, the old way
        test.Run(() =>
        {
            KeyPressedFor(command("Go to File")!, Key.F, control: true, shift: true, alt: false);
            ResolveKeyClash(KeyClash.Replace);
            Filter(string.Empty);
        });
        test.Wait();
        test.Check("keys: conflicts: Replace takes the key off the other command", () =>
            command("Go to File")!.Shortcut == "Ctrl+Shift+F" && command("Find in Files")!.Shortcut.Length == 0
            && Row("Go to File") is { } goTo && !Clashes(goTo) && Row("Find in Files") is { } find && !Clashes(find));
        test.Run(PutKeysBack);
        test.Wait();

        // The source of every key, @source:user leaves the changed ones, and the changed one has the accent's bar
        test.Run(() =>
        {
            KeyPressedFor(command("Go to File")!, Key.F9, control: true, shift: true, alt: true);
            Filter(SOURCE_TOKEN + "user");
        });
        test.Wait();
        test.Check("keys: source: @source:user leaves only the changed keys, tagged User with the accent's bar", () =>
            keysTree!.Items is [{ Tag: MenuItem } row] && LabelOf((MenuItem)row.Tag) == "Go to File" && row.Badge == Said("keys_conflicts.source_user")
            && row.Edge == Accent() && row.BadgeColor == Accent());
        test.Run(() => Filter(string.Empty));
        test.Wait();
        test.Check("keys: source: every row says where its key came from, Default for the rest with no bar", () =>
            keysTree!.Items.Where(row => row.Tag is MenuItem).All(row => row.Badge.Length > 0)
            && Row("Find in Files") is { Edge: null } find && find.Badge == Said("keys_conflicts.source_default")
            && keysTree.Items.Where(row => row.Tag is ExtensionShortcut).All(row => row.Badge == Said("keys_conflicts.source_extension"))
            && TreeAfter(SOURCE_TOKEN + "default").All(row => row.Tag is not MenuItem item || LabelOf(item) != "Go to File")
            && TreeAfter(SOURCE_TOKEN + "nonsense") is [{ Tag: null }]);

        // The words found lit in the accent, the way the settings page lights them
        test.Run(() => Filter("save"));
        test.Wait();
        test.Check("keys: filter: the word found is lit in the accent", () =>
            Row("Save") is { Lit: { Count: > 0 } lit } save && save.LitColor is null
            && lit.Any(piece => save.Text.Substring(piece.Start, piece.Length) == "Save"));
        test.Settle(0.2f);
        test.Run(() => Shot("window-keys-lit"));

        // The funnel puts the words in, the arrows still walk the list from the filter
        test.Run(() =>
        {
            Filter(string.Empty);
            KeysFilterMenu().Items.First(item => item.Shortcut == CONFLICTS_TOKEN).OnPicked!();
        });
        test.Wait();
        test.Check("keys: funnel: offers @modified, @conflicts and @source:user beside the filter, and puts the one picked in", () =>
            keysFunnel is { Visible: true } && KeysFilterMenu().Items.Select(item => item.Shortcut).ToList() is var offered
            && offered.Contains(MODIFIED_TOKEN) && offered.Contains(CONFLICTS_TOKEN) && offered.Contains(SOURCE_TOKEN + "user")
            && keysFilter!.Text.Trim() == CONFLICTS_TOKEN);
        UITreeItem? second = null;
        test.Run(() =>
        {
            Filter(string.Empty);
            keysTree!.Selected = keysTree.Items[0];
            second = keysTree.Items[1];
            keysFilter!.OnKey!(UIKeyboard.DOWN);
        });
        test.Wait();
        test.Check("keys: keyboard: down in the filter goes down the list", () => keysTree!.Selected == second);

        // Escape while recording stops the recording and the window stays
        bool took = false;
        test.Run(() =>
        {
            RecordKeys(true);
            took = keysWindow!.BeforeEscape!();
        });
        test.Wait();
        test.Check("keys: escape while recording stops recording and keeps the window", () =>
            took && !keysRecording && keysWindow is { Visible: true, Parent: not null } && !keysWindow.BeforeEscape!());

        // The gear goes to Settings, the keys window still there under it
        test.Run(() => keysSettings!.OnPressed!());
        test.Settle(0.3f);
        test.Check("keys: the gear in the keys window opens Settings", () =>
            keysSettings is { Visible: true } && SettingsWindow is { Visible: true } && keysWindow is { Visible: true, Parent: not null });
        test.Run(() => CloseTopWindow());
        test.Wait();
        test.Run(PutKeysBack);
        test.Wait();
    }

    // The rows a filter would leave, the filter put back after
    private List<UITreeItem> TreeAfter(string filter)
    {
        string was = keysFilter!.Text;
        keysFilter.Text = filter;
        FillKeysTree();
        var rows = keysTree!.Items.ToList();
        keysFilter.Text = was;
        FillKeysTree();
        return rows;
    }
}
