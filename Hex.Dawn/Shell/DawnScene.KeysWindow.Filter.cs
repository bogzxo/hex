using System.Numerics;

using Horizon.UI.Components;

namespace Hex.Dawn;

// What the keys window's filter leaves and how a row of it reads. Every word typed has to be somewhere in the row, in
// any order, the way the settings page finds a setting, "commit git" finds Commit in the Git menu. A command's category is the
// menu it's in, written before its name the way VS Code writes Git and then Commit, and the filter finds it by that too, so
// "git" is every command of the Git menu. The words found are lit in the accent the way the settings page lights them, a
// changed key has the accent's bar down its edge like a changed setting, and where a key came from is at the right end.
// Two commands on the same key both have the warning's icon and say which other one is on it, until one of them is
// moved (DawnScene.KeysWindow.Conflicts).
internal sealed partial class DawnScene
{
    // The row that says nothing matched, when that's all the list has
    private UITreeItem? keysNothingRow;

    /// <summary>The menu a command is in, the top of where it is, "Git" of "Git > Stash".</summary>
    private static string CategoryOf(string path)
    {
        int cut = path.IndexOf(" > ", StringComparison.Ordinal);
        return cut < 0 ? path : path[..cut];
    }

    /// <summary>Whether every word is somewhere in one of the texts, in whatever order they came.</summary>
    private static bool SaysEvery(string[] words, params string[] texts) =>
        words.All(word => texts.Any(text => text.Contains(word, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Every place one of the words is in a row's text, lit in the accent, null for none.</summary>
    private static List<(int Start, int Length)>? KeysLit(string[] words, string text)
    {
        List<(int Start, int Length)>? lit = null;
        foreach (string word in words)
        {
            for (int at = text.IndexOf(word, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(word, at + word.Length, StringComparison.OrdinalIgnoreCase))
                (lit ??= []).Add((at, word.Length));
        }

        return lit;
    }

    // The column the list is sorted by and whether the other way, kept while the window is up
    private int keysSortColumn;
    private bool keysSortDown;

    /// <summary>A column's title pressed, the list sorted by it, the same one again the other way.</summary>
    private void SortKeysBy(int column)
    {
        (keysSortColumn, keysSortDown) = column == keysSortColumn ? (column, !keysSortDown) : (column, false);
        FillKeysTree();
    }

    /// <summary>
    /// What a row is sorted by in a column, its command, key, when or source. A command with no key goes after the ones
    /// with one, the way VS Code has them.
    /// </summary>
    private static string KeysSortText(UITreeItem row, int column) => column switch
    {
        1 => row.Tag is MenuItem { Shortcut.Length: 0 } ? "￿" : row.Detail,
        2 => row.Note,
        3 => row.Badge,
        _ => row.Text
    };

    /// <summary>The rows in the order of the column the list is sorted by, by command among the ones that say the same.</summary>
    private List<UITreeItem> SortedKeys(List<UITreeItem> rows)
    {
        int column = keysSortColumn;
        var by = StringComparer.OrdinalIgnoreCase;
        var sorted = keysSortDown ? rows.OrderByDescending(row => KeysSortText(row, column), by) : rows.OrderBy(row => KeysSortText(row, column), by);
        return [.. keysSortDown ? sorted.ThenByDescending(row => row.Text, by) : sorted.ThenBy(row => row.Text, by)];
    }

    /// <summary>The one dim row of a filter that left nothing, in the words the settings page uses.</summary>
    private UITreeItem NothingMatches(string typed)
    {
        keysNothingRow = new UITreeItem(Said("keys.nothing", ("filter", typed.Trim()))) { Color = DimColor };
        return keysNothingRow;
    }

    /// <summary>
    /// A command's row in the list, a column each (see FillKeysTree), its category before its name, its key, where the
    /// key goes off and where it came from.
    /// </summary>
    private UITreeItem KeysRow(MenuItem item, string path, string[] words, List<string> others, List<KeyHolder> elsewhere)
    {
        Vector4 accent = compositor.Skin?.AccentColor ?? new Vector4(0.85f, 0.42f, 0.3f, 1.0f);
        Vector4 keyColor = compositor.Skin?.TextColor ?? Vector4.One;

        string label = LabelOf(item), prefix = $"{CategoryOf(path)}: ";
        KeySource source = SourceOf(item);
        bool changed = source == KeySource.User;

        // A changed key is in the accent, icon and all, with the bar down its edge, and says what it was before. One
        // that isn't is the text's colour and not the dim of a detail, the key is what the list is for
        string tooltip = path;
        if (changed)
        {
            string was = originalKeys.TryGetValue(label, out string? original) && original.Length > 0 ? original : Said("keys.no_key");
            tooltip = $"{path}\n{Said("keys.changed_tip", ("key", was))}";
        }

        // A key somebody else is on as well has the warning's icon and colour and says who, either one of them moved
        // clears it
        bool clash = others.Count > 0;
        if (clash)
            tooltip = $"{tooltip}\n{Said("keys_conflicts.conflict_tip", ("key", item.Shortcut), ("other", string.Join(", ", others)))}";

        // One that only shares its key, in another place or each of them only some of the time, isn't warned about and
        // says where else the key is
        if (elsewhere.Count > 0)
            tooltip = $"{tooltip}\n{AlsoTip(elsewhere)}";
        tooltip = $"{tooltip}\n{Said("keys_conflicts.source_tip", ("source", SourceSaid(source)))}";

        string text = prefix + label;
        return new UITreeItem(text, clash ? "warning" : item.Icon.Length > 0 ? item.Icon : "symbol-event", item)
        {
            Note = WhenSaid(WhenOf(item)),
            Detail = item.Shortcut.Length > 0 ? item.Shortcut : Said("keys.no_key"),
            DetailColor = clash ? WarningColor : changed ? accent : item.Shortcut.Length > 0 ? keyColor : DimColor,
            IconColor = clash ? WarningColor : changed ? accent : null,
            Edge = changed ? accent : null,
            Badge = SourceSaid(source),
            BadgeColor = changed ? accent : null,
            Tooltip = tooltip,
            Lit = KeysLit(words, text)
        };
    }
}
