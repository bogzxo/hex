using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Two commands on one key, the way VS Code's keyboard shortcuts deal with it. Every key with whatever's on it, the menus'
// commands and the keys extensions put on theirs, a row on a key somebody else has too gets the warning's icon and says
// who. "@conflicts" leaves only those, "@source:default", "@source:user" and "@source:extension" leave the keys that came
// from there, and the funnel beside the filter puts those words in without having to know them, the settings page's
// funnel. A key pressed for a command that's somebody's already asks first, Replace (what it always did, the other one
// loses it), Keep Both (both on it, conflicting until one moves) or Cancel. Only a key two commands would both want at
// once is a conflict, one the designer has and the editor has too isn't (DawnScene.KeysWindow.When).
internal sealed partial class DawnScene
{
    // The words of the filter that narrow by a key's conflicts and where it came from
    private const string CONFLICTS_TOKEN = "@conflicts", SOURCE_TOKEN = "@source:";

    /// <summary>Where a key came from, the source column of the list.</summary>
    private enum KeySource { Default, User, Extension }

    /// <summary>What a key pressed onto a used one does, see <see cref="ResolveKeyClash"/>.</summary>
    private enum KeyClash { Replace, KeepBoth, Cancel }

    /// <summary>A key an extension put on one of its commands, a row of the list nothing can be done to here.</summary>
    private sealed record ExtensionShortcut(string Extension, string Name, string Key);

    /// <summary>What the filter asked for, the words to find and the @ ones taken out of them.</summary>
    private readonly record struct KeysAsked(string[] Words, bool Modified, bool Conflicts, KeySource? Source, bool Unknown, KeyWhen? When = null);

    // The funnel beside the filter and the gear that goes to Settings
    private Button? keysFunnel;
    private Button? keysSettings;

    // A key pressed for a command that something else had, while it asks what to do about it
    private (MenuItem Item, string Key, List<string> Others)? keyClash;
    private UIDialog? keyClashDialog;

    /// <summary>
    /// The filter read, its @ words out of the rest. "@source:" or "@when:" with nothing after it yet narrows nothing,
    /// a word after it that's no source or place leaves nothing.
    /// </summary>
    private static KeysAsked ReadKeysFilter(string typed)
    {
        var words = new List<string>();
        bool modified = false, conflicts = false, unknown = false;
        KeySource? source = null;
        KeyWhen? when = null;
        foreach (string word in typed.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Equals(MODIFIED_TOKEN, StringComparison.OrdinalIgnoreCase))
                modified = true;
            else if (word.Equals(CONFLICTS_TOKEN, StringComparison.OrdinalIgnoreCase))
                conflicts = true;
            else if (word.StartsWith(WHEN_TOKEN, StringComparison.OrdinalIgnoreCase))
            {
                string place = word[WHEN_TOKEN.Length..];
                if (place.Length == 0)
                    continue;

                if (WhenCalled(place) is { } read)
                    when = read;
                else
                    unknown = true;
            }
            else if (word.StartsWith(SOURCE_TOKEN, StringComparison.OrdinalIgnoreCase))
            {
                string from = word[SOURCE_TOKEN.Length..];
                if (from.Length == 0)
                    continue;

                if (Enum.TryParse(from, ignoreCase: true, out KeySource read) && !int.TryParse(from, out _))
                    source = read;
                else
                    unknown = true;
            }
            else
                words.Add(word);
        }

        return new KeysAsked([.. words], modified, conflicts, source, unknown, when);
    }

    /// <summary>Whether a command's key is a changed one or the one it came with.</summary>
    private KeySource SourceOf(MenuItem item)
    {
        string label = LabelOf(item);
        return rebound.Any(pair => pair.Label == label) ? KeySource.User : KeySource.Default;
    }

    private static string SourceSaid(KeySource source) => source switch
    {
        KeySource.User => Said("keys_conflicts.source_user"),
        KeySource.Extension => Said("keys_conflicts.source_extension"),
        _ => Said("keys_conflicts.source_default")
    };

    /// <summary>The keys the extensions that are on put on their commands.</summary>
    private IEnumerable<ExtensionShortcut> ExtensionKeys()
    {
        foreach (HexExtension extension in extensions.All)
        {
            if (!extension.Enabled || extension.Failed is not null)
                continue;

            foreach (var (name, key) in extension.CommandKeys)
            {
                // One key each, an extension doesn't put its commands on chords
                if (UIKeys.Read(key) is { } read)
                    yield return new ExtensionShortcut(extension.Name, name, UIKeys.Write(read.Key, read.Control, read.Shift, read.Alt));
            }
        }
    }

    // How an extension's command is named in the list and in what it conflicts with
    private static string NameOf(ExtensionShortcut key) => $"{key.Extension}: {key.Name}";

    /// <summary>The other menu commands on a key, each once, the ones a key pressed for a command would take it from.</summary>
    private List<MenuItem> OthersOn(string tidy, string label)
    {
        var others = new List<MenuItem>();
        foreach (var (other, _) in Commands())
        {
            string named = LabelOf(other);
            if (named != label && UIKeys.Tidy(other.Shortcut) == tidy && !others.Any(already => LabelOf(already) == named))
                others.Add(other);
        }

        return others;
    }

    /// <summary>
    /// The other menu commands a key would get in the way of, the ones on the same key and, for one key, the ones on a
    /// chord it starts (Ctrl+K and Ctrl+K Z), or for a chord the one on its first key. Each once.
    /// </summary>
    private List<MenuItem> OverlapsOn(string tidy, string label)
    {
        var others = new List<MenuItem>();
        foreach (var (other, _) in Commands())
        {
            string named = LabelOf(other);
            if (named != label && KeysOverlap(UIKeys.Tidy(other.Shortcut) ?? string.Empty, tidy) && !others.Any(already => LabelOf(already) == named))
                others.Add(other);
        }

        return others;
    }

    /// <summary>The other menu commands a command's key is in conflict with, the ones that would want it at the same time.</summary>
    private List<MenuItem> ClashesWith(MenuItem item) =>
        UIKeys.Tidy(item.Shortcut) is { Length: > 0 } tidy ? OverlapsOn(tidy, LabelOf(item)).Where(other => KeysClash(item, other)).ToList() : [];

    // The row the line under the list was written for last
    private UITreeItem? keysHintFor;

    /// <summary>
    /// The line under the list, what the keys do there, or with a row in conflict picked what it's in conflict with, in
    /// the warning's colour.
    /// </summary>
    private void ShowKeysHint(UITreeItem? row)
    {
        if (keysHint is not { } hint || ReferenceEquals(row, keysHintFor) && hint.Text.Length > 0)
            return;

        keysHintFor = row;
        if (row?.Tag is MenuItem item && ClashesWith(item) is { Count: > 0 } others)
        {
            hint.Text = Said("keys_table.clash_line", ("key", UIShortcutText.Shown(item.Shortcut)), ("other", string.Join(", ", others.Select(LabelOf))));
            hint.Color = WarningColor;
        }
        else
        {
            hint.Text = Said("keys.hint");
            hint.Color = DimColor;
        }
    }

    /// <summary>
    /// Whether two keys as the menus write them get in each other's way, the same key, or one key and a chord that starts
    /// with it. Two chords that only start the same are two keys, Ctrl+K Z and Ctrl+K Ctrl+S both work.
    /// </summary>
    private static bool KeysOverlap(string one, string other)
    {
        if (one.Length == 0 || other.Length == 0)
            return false;

        if (one == other)
            return true;

        int gap = one.IndexOf(' '), otherGap = other.IndexOf(' ');
        return gap >= 0 && otherGap < 0 ? one[..gap] == other : otherGap >= 0 && gap < 0 && other[..otherGap] == one;
    }

    /// <summary>The rows of the keys extensions put on their commands, the ones the filter leaves.</summary>
    private IEnumerable<UITreeItem> ExtensionRows(KeysAsked asked, Func<string, string[], bool> wanted, Dictionary<string, List<KeyHolder>> shared)
    {
        if (asked.Modified || (asked.Source is { } source && source != KeySource.Extension) || asked.Unknown || asked.When is not null)
            yield break;

        string said = SourceSaid(KeySource.Extension);
        foreach (ExtensionShortcut key in ExtensionKeys())
        {
            string text = NameOf(key);
            var (others, elsewhere) = AlsoOn(shared, HolderOf(key));
            if ((asked.Conflicts && others.Count == 0) || !wanted(key.Key, [text, said]))
                continue;

            string tooltip = Said("keys_conflicts.extension_tip", ("extension", key.Extension));
            if (others.Count > 0)
                tooltip = $"{tooltip}\n{Said("keys_conflicts.conflict_tip", ("key", key.Key), ("other", string.Join(", ", others)))}";
            if (elsewhere.Count > 0)
                tooltip = $"{tooltip}\n{AlsoTip(elsewhere)}";

            yield return new UITreeItem(text, others.Count > 0 ? "warning" : "extensions", key)
            {
                Detail = key.Key,
                DetailKeys = key.Key.Length > 0,
                DetailColor = others.Count > 0 ? WarningColor : null,
                IconColor = others.Count > 0 ? WarningColor : null,
                Badge = said,
                Tooltip = tooltip,
                Lit = KeysLit(asked.Words, text)
            };
        }
    }

    /// <summary>What the funnel beside the filter offers, the words that narrow it. The funnel shows it, a test asks for it.</summary>
    private Menu KeysFilterMenu()
    {
        var shown = new Menu("filter");
        shown.Add(Said("keys_conflicts.filter_modified"), () => AddToKeysFilter(MODIFIED_TOKEN), MODIFIED_TOKEN);
        shown.Add(Said("keys_conflicts.filter_conflicts"), () => AddToKeysFilter(CONFLICTS_TOKEN), CONFLICTS_TOKEN, "warning");
        shown.Add(Said("keys_conflicts.filter_user"), () => AddToKeysFilter(SOURCE_TOKEN + "user"), SOURCE_TOKEN + "user");
        shown.Add(Said("keys_conflicts.filter_default"), () => AddToKeysFilter(SOURCE_TOKEN + "default"), SOURCE_TOKEN + "default");
        shown.Add(Said("keys_conflicts.filter_extension"), () => AddToKeysFilter(SOURCE_TOKEN + "extension"), SOURCE_TOKEN + "extension");
        shown.AddSeparator();
        shown.Add(Said("keys_when.filter_designer"), () => AddToKeysFilter(WHEN_TOKEN + "designer"), WHEN_TOKEN + "designer", "layout");
        shown.Add(Said("keys_when.filter_editor"), () => AddToKeysFilter(WHEN_TOKEN + "editor"), WHEN_TOKEN + "editor", "file-code");
        return shown;
    }

    /// <summary>
    /// A word put on the end of the filter the way the funnel's menu does, the keyboard back in it to go on typing. Not
    /// twice, and recording stops, the word's for the typed filter.
    /// </summary>
    private void AddToKeysFilter(string word)
    {
        if (keysFilter is not { } filter)
            return;

        if (keysRecording)
            RecordKeys(false);

        string typed = filter.Text.TrimEnd();
        if (!typed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(already => already.Equals(word, StringComparison.OrdinalIgnoreCase)))
            typed = typed.Length == 0 ? word : $"{typed} {word}";

        filter.Text = typed + " ";
        filter.Focus();
        FillKeysTree();
    }

    // The funnel pressed, its menu under it
    private void ShowKeysFilters()
    {
        if (keysFunnel is { } funnel)
            ContextMenu.ShowAt(chrome, new Vector2(funnel.Bounds.Min.X, funnel.Bounds.Min.Y), KeysFilterMenu());
    }

    /// <summary>A key pressed for a command that other ones are on, asked about rather than taken from them.</summary>
    private void AskAboutClash(MenuItem item, string key, List<string> others)
    {
        keyClash = (item, key, others);
        string other = string.Join(", ", others);
        keyClashDialog = UIDialog.Show(chrome, Said("keys_conflicts.clash_title", ("key", key)),
            Said("keys_conflicts.clash_text", ("key", key), ("other", other)),
            new DialogChoice(Said("keys_conflicts.replace"), () => ResolveKeyClash(KeyClash.Replace)),
            new DialogChoice(Said("keys_conflicts.keep_both"), () => ResolveKeyClash(KeyClash.KeepBoth)),
            new DialogChoice(Said("keys_conflicts.cancel"), () => ResolveKeyClash(KeyClash.Cancel)));
    }

    /// <summary>
    /// What was picked for a key pressed onto a used one. Replace takes it off the others, Keep Both leaves it on them as
    /// well (the rows then conflict), Cancel changes nothing.
    /// </summary>
    private void ResolveKeyClash(KeyClash choice)
    {
        if (keyClash is not { } clash)
            return;

        var (item, key, others) = clash;
        keyClash = null;
        if (keyClashDialog is { IsOpen: true } dialog)
            dialog.Close();
        keyClashDialog = null;

        if (choice == KeyClash.Cancel || !Rebind(item.Label, key, keepOthers: choice == KeyClash.KeepBoth))
            return;

        SaveKeys();
        FillKeysTree();
        string other = string.Join(", ", others);
        Say(choice == KeyClash.KeepBoth
            ? Said("keys_conflicts.kept_both", ("command", LabelOf(item)), ("key", key), ("other", other))
            : Said("keys.now_instead", ("command", LabelOf(item)), ("key", key), ("other", other)));
    }
}
