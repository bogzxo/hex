using Key = Silk.NET.Input.Key;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test, the keys window as VS Code's table. Its columns with their titles, sorted by the one pressed, no keys in
// conflict out of the box, chords that go off and are taken by the dialog and the keys file, and what a row in conflict
// offers to do about it.
internal sealed partial class DawnScene
{
    private void TestKeysTable(HexPointerScript test, Func<string, MenuItem?> command)
    {
        void Filter(string text)
        {
            keysFilter!.Text = text;
            keysFilter.OnChanged!(keysFilter.Text);
        }

        UITreeItem? Row(string label) => keysTree!.Items.FirstOrDefault(row => row.Tag is MenuItem item && LabelOf(item) == label);
        bool Clashes(UITreeItem row) => row.Icon == "warning" && row.IconColor == WarningColor && row.DetailColor == WarningColor;
        string zen = Said("menus.zen_mode").TrimEnd('.'), shortcuts = Said("commands.shortcuts").TrimEnd('.');

        // A column each, titled, and every text inside its own. The When of a row is where its column is, it was drawn
        // over the end of a long name
        test.Run(() =>
        {
            PutKeysBack();
            Filter(WHEN_TOKEN + "available");
        });
        test.Settle(0.2f);
        bool Inside(UIRect cell, UIRect column) => cell.Min.X >= column.Min.X - 0.5f && cell.Max.X <= column.Max.X + 0.5f;
        test.Check("keys: table has headers", () =>
            keysTree!.Columns is { Count: 4 } columns
            && columns.Select(column => column.Title).SequenceEqual([Said("keys_table.command"), Said("keys_table.keybinding"), Said("keys_table.when"), Said("keys_table.source")])
            && keysTree.Items.FirstOrDefault(row => row.Note == Said("keys_when.available")) is { } available
            && keysTree.CellBounds(available, 2) is { } when && keysTree.HeaderBounds(2) is { } whenColumn && Inside(when, whenColumn)
            && keysTree.CellBounds(available, 0) is { } name && keysTree.HeaderBounds(0) is { } nameColumn && Inside(name, nameColumn) && name.Max.X < when.Min.X
            && keysTree.CellBounds(available, 1) is { } key && keysTree.HeaderBounds(1) is { } keyColumn && Inside(key, keyColumn)
            && keysTree.CellBounds(available, 3) is { } source && keysTree.HeaderBounds(3) is { } sourceColumn && Inside(source, sourceColumn));
        test.Run(() => Filter("zen"));
        test.Settle(0.2f);
        test.Check("keys: table: Zen Mode's command and key each in their own column", () =>
            Row(zen) is { } row && keysTree!.CellBounds(row, 0) is { } name && keysTree.HeaderBounds(0) is { } nameColumn && Inside(name, nameColumn)
            && keysTree.CellBounds(row, 1) is { } key && keysTree.HeaderBounds(1) is { } keyColumn && Inside(key, keyColumn) && row.Detail == "Ctrl+K Z");
        test.Run(() => Shot("window-keys-table"));

        // The Keybinding title pressed by the pointer sorts by key, pressed again the other way, the command's sorts back
        List<string> Keys() => keysTree!.Items.Select(row => KeysSortText(row, 1)).ToList();
        test.Run(() => Filter(string.Empty));
        test.Settle(0.2f);
        test.Check("keys: the list starts sorted by command, the arrow on its title", () =>
            keysTree!.SortColumn == 0 && !keysTree.SortDescending
            && keysTree.Items.Select(row => row.Text).SequenceEqual(keysTree.Items.Select(row => row.Text).Order(StringComparer.OrdinalIgnoreCase)));
        test.Click(() => keysTree!.Module is { } module && keysTree.HeaderBounds(1) is { } title ? module.ToWorld(title.Center) : default);
        test.Settle(0.2f);
        List<string> up = [];
        test.Check("keys: header sorts", () =>
        {
            up = Keys();
            return keysTree!.SortColumn == 1 && !keysTree.SortDescending && up.Count > 10
                && up.SequenceEqual(up.Order(StringComparer.OrdinalIgnoreCase)) && up[^1] == "￿";
        });
        test.Run(() => keysTree!.PressHeader(1));
        test.Wait();
        test.Check("keys: header sorts: pressed again, the other way", () =>
            keysTree!.SortDescending && Keys() is var down && down.SequenceEqual(down.OrderDescending(StringComparer.OrdinalIgnoreCase))
            && down.Count == up.Count && down[0] == "￿" && down[^1] == up[0]);
        test.Run(() => keysTree!.PressHeader(0));
        test.Wait();
        test.Check("keys: header sorts: the command's title, by command again", () =>
            keysTree!.SortColumn == 0 && !keysTree.SortDescending && keysTree.Items.Select(row => row.Text).SequenceEqual(keysTree.Items.Select(row => row.Text).Order(StringComparer.OrdinalIgnoreCase)));

        // Out of the box no two commands want one key at once, Zen Mode is on VS Code's chord and Step Into has F11
        List<UITreeItem> conflicting = [];
        test.Run(() =>
        {
            conflicting = TreeAfter(CONFLICTS_TOKEN);
            if (conflicting.Any(row => row.Tag is not null))
                Console.WriteLine($"[Hex self-test]   conflicts: {string.Join(", ", conflicting.Select(row => $"{row.Text} {row.Detail}"))}");
        });
        test.Check("keys: no clash out of the box", () =>
            conflicting is [{ Tag: null }] && command(zen)!.Shortcut == "Ctrl+K Z" && command(shortcuts)!.Shortcut == "Ctrl+K Ctrl+S"
            && command(Said("debugging.step_into").TrimEnd('.'))?.Shortcut == "F11");

        TestKeysChords(test, command, zen, shortcuts);
        TestKeysClashActions(test, command, zen, shortcuts, Row, Clashes);
    }

    /// <summary>A chord pressed goes off, its first key alone waits and Escape leaves it, the dialog and the file take one.</summary>
    private void TestKeysChords(HexPointerScript test, Func<string, MenuItem?> command, string zen, string shortcuts)
    {
        void Press(Key key, bool control = false) => commands.Keys.Update(pressed => pressed == key, control, shift: false, alt: false, typing: false);

        bool wasQuiet = false;
        test.Run(() =>
        {
            wasQuiet = Quiet;
            Press(Key.K, control: true);
        });
        test.Check("keys: chord works: Ctrl+K waits for the second key and says so", () =>
            commands.Keys.Pending == "Ctrl+K" && Quiet == wasQuiet && messageItem.Text.Contains(Said("keys_chords.waiting", ("key", "Ctrl+K"))));
        test.Run(() => Press(Key.Z));
        test.Wait();
        test.Check("keys: chord works, and the status bar stops saying it waits", () => Quiet != wasQuiet && commands.Keys.Pending is null
            && !messageItem.Text.Contains(Said("keys_chords.waiting", ("key", "Ctrl+K"))));
        test.Run(() =>
        {
            if (Quiet != wasQuiet)
                ToggleQuiet();
            Press(Key.K, control: true);
        });
        test.Run(() => Press(Key.Escape));
        test.Wait();
        test.Check("keys: chord works: Ctrl+K alone then Escape does nothing, and the waiting is taken off", () =>
            Quiet == wasQuiet && commands.Keys.Pending is null && !messageItem.Text.Contains(Said("keys_chords.waiting", ("key", "Ctrl+K"))));
        test.Run(() => Press(Key.K, control: true));
        test.Run(() => Press(Key.Q));
        test.Wait();
        test.Check("keys: chord works: a second key that's no chord says it's not a command", () =>
            Quiet == wasQuiet && commands.Keys.Pending is null && messageItem.Text.Contains(Said("keys_chords.not_a_command", ("keys", "Ctrl+K Q"))));

        // The dialog takes Ctrl+K and then Ctrl+C as one chord, the keys file keeps it, reset puts the default back
        string file = Path.Combine(testFolder, "selftest_chord_keys.hor");
        List<(string Label, string Key)> read = [];
        test.Run(() => AskForKey(command("Go to File")!));
        test.Wait();
        test.Run(() => KeyStrokeFor(keyFor!, Key.K, control: true, shift: false, alt: false));
        test.Check("keys: chord recorded: the dialog shows the first key and waits for a second", () =>
            keyDialog is { IsOpen: true } && keyFor is not null && keyChordLine is { } line && line.Text.Contains("Ctrl+K") && command("Go to File")!.Shortcut == "Ctrl+P");
        test.Run(() => KeyStrokeFor(keyFor!, Key.C, control: true, shift: false, alt: false));
        test.Wait();
        test.Run(() =>
        {
            WriteKeys(file);
            read = ReadKeys(file);
            PutKeysBack();
            foreach (var (label, key) in read)
                Rebind(label, key, keepOthers: true);
        });
        test.Wait();
        test.Check("keys: chord recorded and kept", () =>
            command("Go to File")!.Shortcut == "Ctrl+K Ctrl+C" && read is [("Go to File", "Ctrl+K Ctrl+C")] && keyDialog is not { IsOpen: true }
            && commands.Keys.Has(new UIKeyStroke(Key.K, true, false, false), new UIKeyStroke(Key.C, true, false, false)) && !commands.Keys.Has(Key.P, true, false, false));

        // The recorder in the filter takes the chord as well and leaves the command on it
        test.Run(() =>
        {
            RecordKeys(true);
            KeyRecorded(Key.K, control: true, shift: false, alt: false);
            KeyRecorded(Key.C, control: true, shift: false, alt: false);
        });
        test.Wait();
        test.Check("keys: chord recorded: the filter's recorder takes two keys as a chord", () =>
            keysFilter!.Text == "Ctrl+K Ctrl+C" && keysTree!.Items is [{ Tag: MenuItem item }] && LabelOf(item) == "Go to File");
        test.Run(() =>
        {
            RecordKeys(false);
            PutKeysBack();
        });
        test.Wait();
        test.Check("keys: chord recorded: reset puts the default back", () =>
            command("Go to File")!.Shortcut == "Ctrl+P" && commands.Keys.Has(Key.P, true, false, false)
            && !commands.Keys.Has(new UIKeyStroke(Key.K, true, false, false), new UIKeyStroke(Key.C, true, false, false))
            && command(zen)!.Shortcut == "Ctrl+K Z" && command(shortcuts)!.Shortcut == "Ctrl+K Ctrl+S");
    }

    /// <summary>One key on the first key of a chord is a conflict, the row says so under the list and can take it off the other.</summary>
    private void TestKeysClashActions(HexPointerScript test, Func<string, MenuItem?> command, string zen, string shortcuts,
        Func<string, UITreeItem?> row, Func<UITreeItem, bool> clashes)
    {
        test.Run(() =>
        {
            KeyPressedFor(command("Go to File")!, Key.K, control: true, shift: false, alt: false);
            ResolveKeyClash(KeyClash.KeepBoth);
            keysFilter!.Text = string.Empty;
            FillKeysTree();
        });
        test.Wait();
        test.Run(() => keysTree!.Selected = row("Go to File"));
        test.Wait();
        test.Check("keys: conflict actions: the line under the list names the clash", () =>
            keysHint!.Text == Said("keys_table.clash_line", ("key", "Ctrl+K"), ("other", string.Join(", ", ClashesWith(command("Go to File")!).Select(LabelOf))))
            && keysHint.Text.Contains(zen) && keysHint.Text.Contains(shortcuts) && keysHint.Color == WarningColor);
        test.Check("keys: conflict actions: a key a chord starts with is a conflict with the chord", () =>
            command("Go to File")!.Shortcut == "Ctrl+K" && row("Go to File") is { } goTo && clashes(goTo) && row(zen) is { } quiet && clashes(quiet)
            && TreeAfter(CONFLICTS_TOKEN).Any(found => found.Tag is MenuItem item && LabelOf(item) == zen));
        test.Check("keys: conflict actions: the row's menu removes the key from each other command", () =>
            KeyMenu(command("Go to File")!).Items.Select(item => item.Label).ToList() is var labels
            && labels.Contains(Said("keys_table.remove_from", ("other", zen))) && labels.Contains(Said("keys_table.remove_from", ("other", shortcuts)))
            && !KeyMenu(command("Find in Files")!).Items.Any(item => item.Label.StartsWith(Said("keys_table.remove_from", ("other", string.Empty)), StringComparison.Ordinal)));
        test.Run(() => KeyMenu(command("Go to File")!).Items.First(item => item.Label == Said("keys_table.remove_from", ("other", zen))).OnPicked!());
        test.Wait();
        test.Check("keys: conflict actions: Remove Key from Zen Mode takes the chord off it", () =>
            command(zen)!.Shortcut.Length == 0 && command("Go to File")!.Shortcut == "Ctrl+K"
            && !commands.Keys.Has(new UIKeyStroke(Key.K, true, false, false), new UIKeyStroke(Key.Z, false, false, false)));
        test.Run(PutKeysBack);
        test.Wait();
        test.Check("keys: conflict actions: reset puts both back", () =>
            command(zen)!.Shortcut == "Ctrl+K Z" && command("Go to File")!.Shortcut == "Ctrl+P"
            && commands.Keys.Has(new UIKeyStroke(Key.K, true, false, false), new UIKeyStroke(Key.Z, false, false, false))
            && TreeAfter(CONFLICTS_TOKEN) is [{ Tag: null }]);
    }
}
