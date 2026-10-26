using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Where a key goes off, VS Code's "when" column. A command of the Layout menu has its key only in the designer and one of
// the Selection menu only in the code editor, those menus are only on the bar there, and a command that can only be
// picked some of the time (greyed out, or left out of its menu) has its key only then. Two commands on one key are a
// conflict only when both can have it at the same time, the same place and not both of them only some of the time. The
// designer's Ctrl+G and Go to Line share a key and never fight over it, and Run's F5 is whichever F5 makes sense for
// what's open. Those rows say where else the key is instead of being warned about, and "@when:designer" and
// "@when:editor" leave the keys of one place.
internal sealed partial class DawnScene
{
    // The word of the filter that leaves the keys of one place
    private const string WHEN_TOKEN = "@when:";

    /// <summary>Where a command's key goes off, the When of its row.</summary>
    private enum KeyWhen { Always, Designer, Editor, Available }

    /// <summary>
    /// A command or an extension's shortcut on a key, by its name, where it is (a menu that's only on the bar some of the
    /// time, empty for always) and whether it can have the key only some of the time there.
    /// </summary>
    private sealed record KeyHolder(string Name, string Key, string Context, bool Conditional, KeyWhen When);

    /// <summary>Where a command's key goes off, the menus that come and go first and then whether it can always be picked.</summary>
    private KeyWhen WhenOf(MenuItem item) => commands.ContextOf(item) switch
    {
        "Layout" => KeyWhen.Designer,
        "Selection" => KeyWhen.Editor,
        not null => KeyWhen.Available,
        null => item.IsEnabled is not null || item.IsVisible is not null ? KeyWhen.Available : KeyWhen.Always
    };

    // The tag a row has right of its name, nothing for a key that goes off everywhere
    private static string WhenSaid(KeyWhen when) => when switch
    {
        KeyWhen.Designer => Said("keys_when.designer"),
        KeyWhen.Editor => Said("keys_when.editor"),
        KeyWhen.Available => Said("keys_when.available"),
        _ => string.Empty
    };

    // The same said after a command's name, "Group in the designer"
    private static string WhereSaid(KeyWhen when) => when switch
    {
        KeyWhen.Designer => Said("keys_when.in_designer"),
        KeyWhen.Editor => Said("keys_when.in_editor"),
        KeyWhen.Available => Said("keys_when.when_available"),
        _ => Said("keys_when.everywhere")
    };

    // What @when: is followed by for each place, null for a word that's none of them
    private static KeyWhen? WhenCalled(string word) => word.ToLowerInvariant() switch
    {
        "designer" => KeyWhen.Designer,
        "editor" => KeyWhen.Editor,
        "available" => KeyWhen.Available,
        _ => null
    };

    private KeyHolder HolderOf(MenuItem item) =>
        new(LabelOf(item), UIKeys.Tidy(item.Shortcut) ?? string.Empty, commands.ContextOf(item) ?? string.Empty,
            item.IsEnabled is not null || item.IsVisible is not null, WhenOf(item));

    // An extension's key goes off wherever, as far as Hex can tell
    private static KeyHolder HolderOf(ExtensionShortcut key) => new(NameOf(key), key.Key, string.Empty, false, KeyWhen.Always);

    /// <summary>
    /// Whether two commands would both want a key at once, whatever key it is. Not the same command, in the same place,
    /// and not both of them only some of the time.
    /// </summary>
    private static bool SameTime(KeyHolder one, KeyHolder other) =>
        one.Name != other.Name && one.Context == other.Context && !(one.Conditional && other.Conditional);

    /// <summary>Whether a key pressed for one command would take it from the other, see <see cref="SameTime"/>.</summary>
    private bool KeysClash(MenuItem item, MenuItem other) => SameTime(HolderOf(item), HolderOf(other));

    /// <summary>Every key that's on more than one command or extension shortcut, with what's on it, each once a place.</summary>
    private Dictionary<string, List<KeyHolder>> SharedKeys()
    {
        var on = new Dictionary<string, List<KeyHolder>>(StringComparer.Ordinal);
        void Put(KeyHolder holder)
        {
            if (holder.Key.Length == 0)
                return;

            if (!on.TryGetValue(holder.Key, out var holders))
                on[holder.Key] = holders = [];

            Join(holders, holder);
        }

        foreach (var (item, _) in Commands())
            Put(HolderOf(item));

        foreach (ExtensionShortcut key in ExtensionKeys())
            Put(HolderOf(key));

        // A chord and the one key it starts with are in each other's way, each has the other's holders as well
        foreach (var (key, holders) in on.ToList())
        {
            int gap = key.IndexOf(' ');
            if (gap < 0 || !on.TryGetValue(key[..gap], out var first))
                continue;

            foreach (KeyHolder one in first.ToList())
                Join(holders, one);
            foreach (KeyHolder chord in holders.Where(holder => holder.Key == key).ToList())
                Join(first, chord);
        }

        foreach (string key in on.Where(pair => pair.Value.Count < 2).Select(pair => pair.Key).ToList())
            on.Remove(key);
        return on;
    }

    // A holder put with the others of a key, once a place, the same command in two menus of one place is the one command
    private static void Join(List<KeyHolder> holders, KeyHolder holder)
    {
        if (!holders.Any(already => already.Name == holder.Name && already.Context == holder.Context))
            holders.Add(holder);
    }

    /// <summary>
    /// What else is on a key besides one command, the ones it conflicts with by name and the ones that only share it,
    /// somewhere else or each of them only some of the time.
    /// </summary>
    private static (List<string> Clashes, List<KeyHolder> Elsewhere) AlsoOn(Dictionary<string, List<KeyHolder>> shared, KeyHolder holder)
    {
        List<string> clashes = [];
        List<KeyHolder> elsewhere = [];
        if (holder.Key.Length == 0 || !shared.TryGetValue(holder.Key, out var holders))
            return (clashes, elsewhere);

        foreach (KeyHolder other in holders)
        {
            if (other.Name == holder.Name)
                continue;

            if (SameTime(holder, other))
            {
                if (!clashes.Contains(other.Name))
                    clashes.Add(other.Name);
            }
            else if (!elsewhere.Any(already => already.Name == other.Name))
                elsewhere.Add(other);
        }

        return (clashes, elsewhere);
    }

    // The lines of a row's tooltip that say where else its key is, "Also on Group in the designer"
    private static string AlsoTip(List<KeyHolder> elsewhere) =>
        string.Join("\n", elsewhere.Select(other => Said("keys_when.also_tip", ("other", other.Name), ("when", WhereSaid(other.When)))));

    /// <summary>
    /// The commands on a key in the order the key tries them, the ones of a menu that comes and goes first the way
    /// <see cref="UICommands"/> has them, then the ones that can only be picked some of the time, then the rest, the
    /// order they came in otherwise. Each gets it while it can be picked and passes it on when it can't.
    /// </summary>
    private List<MenuItem> InKeyOrder(List<MenuItem> on) =>
        [.. on.OrderBy(item => commands.ContextOf(item) is not null ? 0 : item.IsEnabled is not null || item.IsVisible is not null ? 1 : 2)];
}
