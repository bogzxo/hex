using Key = Silk.NET.Input.Key;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test, where a key goes off. The designer's keys and the editor's tagged so and kept out of the conflicts, Run's
// F5s that take turns not warned about, two commands that really fight over a key still warned about and asked about, and
// @when: leaving the keys of one place.
internal sealed partial class DawnScene
{
    private void TestKeysWhen(HexPointerScript test, Func<string, MenuItem?> command)
    {
        void Filter(string text)
        {
            keysFilter!.Text = text;
            keysFilter.OnChanged!(keysFilter.Text);
        }

        UITreeItem? Row(string label) => keysTree!.Items.FirstOrDefault(row => row.Tag is MenuItem item && LabelOf(item) == label);
        bool Clashes(UITreeItem row) => row.Icon == "warning" && row.IconColor == WarningColor && row.DetailColor == WarningColor;
        string group = Said("designer_selection.group").TrimEnd('.'), goToLine = Said("find_menu.go_to_line").TrimEnd('.');
        string playIntros = Said("designer_selection.play_entrances").TrimEnd('.'), runFile = Said("code.run_file").TrimEnd('.');

        test.Run(() =>
        {
            PutKeysBack();
            Filter(string.Empty);
        });
        test.Wait();

        // Where a command was added and what its key does, the menus' own word for it
        test.Check("keys window: the commands know their menu's place, Layout the designer's and Selection the editor's", () =>
            command(group) is { } grouping && commands.ContextOf(grouping) == "Layout" && commands.UseOf(grouping) == UIKeyUse.Bound
            && command("Find in Files") is { } find && commands.ContextOf(find) is null && commands.UseOf(find) == UIKeyUse.BoundWhileTyping
            && Commands().Any(pair => commands.ContextOf(pair.Item) == "Selection"));

        // The designer's rows say so dim after their name, the editor's too, the rest nothing
        test.Check("keys window: a Layout menu command's row has a dim Designer tag, a Selection one Code editor, Find in Files none", () =>
            Row(group) is { } grouping && grouping.Note == Said("keys_when.designer")
            && Commands().First(pair => commands.ContextOf(pair.Item) == "Selection").Item is var selecting
            && keysTree!.Items.First(row => row.Tag == selecting).Note == Said("keys_when.editor")
            && Row("Find in Files") is { Note.Length: 0 });
        test.Run(() => Filter(goToLine));
        test.Settle(0.2f);
        test.Run(() => Shot("window-keys-when"));

        // Keys split by place, or taking turns, aren't conflicts. Ctrl+G is the designer's Group and Go to Line elsewhere,
        // every F5 is Run's only when it's the one for what's open
        List<UITreeItem> conflicting = [];
        test.Run(() =>
        {
            Filter(string.Empty);
            conflicting = TreeAfter(CONFLICTS_TOKEN);
        });
        test.Wait();
        test.Check("keys window: default keys split by place aren't conflicts, F5's Layout/Run pair and Ctrl+G neither", () =>
            Row(group) is { } grouping && !Clashes(grouping) && Row(goToLine) is { } line && !Clashes(line)
            && Row(playIntros) is { } play && !Clashes(play) && Row(runFile) is { } run && !Clashes(run)
            && !conflicting.Any(row => row.Detail is "F5" or "Ctrl+G" or "Ctrl+A" or "Ctrl+D" or "Esc" or "F12")
            && conflicting.Count(row => row.Tag is MenuItem) <= 4);
        test.Check("keys window: a key shared with another place says where else it is instead of a warning", () =>
            Row(goToLine) is { } line && line.Tooltip.Contains(Said("keys_when.also_tip", ("other", group), ("when", Said("keys_when.in_designer"))))
            && !line.Tooltip.Contains(Said("keys_conflicts.conflict_tip", ("key", "Ctrl+G"), ("other", group)))
            && Row(playIntros) is { } play && play.Tooltip.Contains(Said("keys_when.also_tip", ("other", runFile), ("when", Said("keys_when.when_available")))));

        // @when:designer leaves the Layout menu's commands and nothing else, @when:editor the Selection menu's
        test.Check("keys window: @when:designer leaves only the Layout menu's commands, @when:editor only the Selection menu's", () =>
            TreeAfter(WHEN_TOKEN + "designer") is { Count: > 0 } designer && designer.All(row => row.Tag is MenuItem item && commands.ContextOf(item) == "Layout")
            && designer.Count == Commands().Count(pair => commands.ContextOf(pair.Item) == "Layout")
            && TreeAfter(WHEN_TOKEN + "editor") is { Count: > 0 } editor && editor.All(row => row.Tag is MenuItem item && commands.ContextOf(item) == "Selection")
            && TreeAfter(WHEN_TOKEN + "nowhere") is [{ Tag: null }]
            && KeysFilterMenu().Items.Select(item => item.Shortcut).ToList() is var offered
            && offered.Contains(WHEN_TOKEN + "designer") && offered.Contains(WHEN_TOKEN + "editor"));

        // Two commands that are always there on one unused key really do fight, both warned about, both in @conflicts,
        // and the second one asked about
        string fought = UIKeys.Write(Key.F8, true, true, true);
        test.Run(() => KeyPressedFor(command("Go to File")!, Key.F8, control: true, shift: true, alt: true));
        test.Wait();
        test.Run(() => KeyPressedFor(command("Split Editor")!, Key.F8, control: true, shift: true, alt: true));
        test.Wait();
        test.Check("keys window: a real clash still asks, Replace, Keep Both or Cancel", () =>
            keyClash is { Others: ["Go to File"] } clash && clash.Key == fought && keyClashDialog is { IsOpen: true }
            && command("Go to File")!.Shortcut == fought && command("Split Editor")!.Shortcut == "Ctrl+\\");
        test.Run(() =>
        {
            ResolveKeyClash(KeyClash.KeepBoth);
            Filter(string.Empty);
        });
        test.Wait();
        test.Check("keys window: a real clash still shows, both warned about and both in @conflicts", () =>
            Row("Go to File") is { } goTo && Clashes(goTo) && Row("Split Editor") is { } split && Clashes(split)
            && TreeAfter(CONFLICTS_TOKEN) is var listed && listed.Any(row => row.Tag is MenuItem item && LabelOf(item) == "Go to File")
            && listed.Any(row => row.Tag is MenuItem item && LabelOf(item) == "Split Editor"));
        test.Run(PutKeysBack);
        test.Wait();

        // A key only the designer has goes on a command that's always there without asking, and both keep it
        string sharedKey = UIKeys.Write(Key.F7, true, true, true);
        test.Run(() =>
        {
            Rebind(group, sharedKey);
            KeyPressedFor(command("Go to File")!, Key.F7, control: true, shift: true, alt: true);
            Filter(string.Empty);
        });
        test.Wait();
        test.Check("keys window: a key the designer has is shared without asking, and neither is warned about", () =>
            keyClash is null && keyClashDialog is not { IsOpen: true } && command("Go to File")!.Shortcut == sharedKey && command(group)!.Shortcut == sharedKey
            && commands.Keys.Has(Key.F7, true, true, true) && Row("Go to File") is { } goTo && !Clashes(goTo) && Row(group) is { } grouping && !Clashes(grouping)
            && goTo.Tooltip.Contains(Said("keys_when.also_tip", ("other", group), ("when", Said("keys_when.in_designer"))))
            && !TreeAfter(CONFLICTS_TOKEN).Any(row => row.Detail == sharedKey));
        test.Run(PutKeysBack);
        test.Wait();
        test.Check("keys window: reset puts the designer's key back on its own", () =>
            command(group)!.Shortcut == "Ctrl+G" && command("Go to File")!.Shortcut == "Ctrl+P" && !commands.Keys.Has(Key.F7, true, true, true));
    }
}
