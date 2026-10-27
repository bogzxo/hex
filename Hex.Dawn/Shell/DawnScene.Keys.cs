using System.Numerics;

using Key = Silk.NET.Input.Key;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Keys put where somebody wants them. File, Keyboard Shortcuts, is every command with its key in a list, typed into to
// narrow it down, the arrows up and down it, enter (or a double click) waits for a new key for the one chosen and delete
// takes its key off (the window is DawnScene.KeysWindow). keys.hor next to hex_settings.hor is what was changed, a
// command by what its menu calls it and the key it's on now ({ "Find in Files": "Ctrl+Alt+F" }, "" for none), read as
// Hex starts and written at every change. The
// old key of the command comes off, and whatever else was on the new one once asked (Replace), so a key does one thing
// unless Keep Both was picked, and the menus and
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

    private void LoadKeys()
    {
        // The first key of a chord says it's waiting for the second in the status bar, VS Code's words, and a second that
        // makes no chord says so too
        commands.Keys.OnChordPending = first => Say(Said("keys_chords.waiting", ("key", UIShortcutText.Shown(first))));
        commands.Keys.OnChordMissed = keys => Say(Said("keys_chords.not_a_command", ("keys", UIShortcutText.Shown(keys))));

        bool kept = !options.SelfTest && options.Check.Length == 0;
        keysPath = kept ? HexFiles.Kept(KEYS_FILE, was: "keys.hor") : null;
        if (keysPath is null || !File.Exists(keysPath))
            return;

        // The file says the whole of it, what came off a key is in it as "", so nothing's taken off of anything here. A key
        // kept on two commands is still on both next time
        foreach (var (label, key) in ReadKeys(keysPath))
        {
            if (!Rebind(label, key, keepOthers: true))
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
                "File, Keyboard Shortcuts writes this, and it can be edited by hand. Reset All deletes it.")
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

    /// <summary>
    /// Every item of every menu, with the menus it's in, the ones that do something. Not the files of Open Recent, which
    /// come and go and aren't a command to put on a key ("1  app.d" was in the list of keys to set).
    /// </summary>
    private IEnumerable<(MenuItem Item, string Path)> Commands() =>
        menu.Menus.SelectMany(bar => bar.Everything()).Where(pair => pair.Item is { IsSeparator: false, Submenu: null, OnPicked: not null }
            && (recentMenu is null || !recentMenu.Items.Contains(pair.Item)));

    private static string LabelOf(MenuItem item) => item.Label.TrimEnd('.');

    /// <summary>
    /// Puts the command a menu calls so on a key ("" for none), its old key and anything on the new one that would want
    /// it at the same time taken off, or with keepOthers the others left on it too (Keep Both, and keys.hor read back).
    /// What only has the key somewhere else keeps it either way. False when there's no such command or no such key.
    /// </summary>
    private bool Rebind(string label, string written, bool keepOthers = false)
    {
        string wanted = label.TrimEnd('.');
        if (Commands().FirstOrDefault(pair => LabelOf(pair.Item) == wanted).Item is not { } item)
            return false;

        // One key or a chord of two, "Ctrl+K Ctrl+C"
        var to = written.Length > 0 ? UIKeys.ReadChord(written) : null;
        if (written.Length > 0 && to is null)
            return false;

        originalKeys.TryAdd(wanted, UIKeys.Tidy(item.Shortcut) ?? string.Empty);

        // It goes off while typing if its old key did, and never on a key that types something (a letter on its own).
        // An old key another command shares stays that one's, it isn't a key taken off of everything
        bool? typing = null;
        string? left = null;
        if (UIKeys.ReadChord(item.Shortcut) is { } old)
        {
            string was = UIKeys.WriteChord(old.First, old.Second);
            typing = commands.Keys.Remove(old.First, old.Second);
            if (OthersOn(was, wanted).Count > 0)
                left = was;
            else
                keysTaken.Add(was);
        }

        string tidy = string.Empty;
        bool shared = false;
        bool? onNew = null;
        if (to is { } key)
        {
            tidy = UIKeys.WriteChord(key.First, key.Second);
            onNew = commands.Keys.Remove(key.First, key.Second);

            // Only what would want the key at the same time loses it, a command of the designer keeps its key when one
            // that's always there goes on it too (DawnScene.KeysWindow.When). One on a key a chord starts with clashes
            // with the chord as well, Ctrl+K would never get to be pressed with Ctrl+K Z waiting for its second key
            var others = OthersOn(tidy, wanted);
            List<MenuItem> taken = keepOthers ? [] : OverlapsOn(tidy, wanted).Where(other => KeysClash(item, other)).ToList();
            shared = others.Any(other => !taken.Contains(other));

            // Whatever else was on the key is written down as off it, so keys.hor says the whole of what changed
            foreach (MenuItem other in taken)
            {
                string its = UIKeys.Tidy(other.Shortcut) ?? tidy;
                originalKeys.TryAdd(LabelOf(other), its);
                rebound.RemoveAll(pair => pair.Label == LabelOf(other));
                rebound.Add((LabelOf(other), string.Empty));

                // A key that only starts the same is another key, taken off on its own
                if (its != tidy && UIKeys.ReadChord(its) is { } overlap)
                    commands.Keys.Remove(overlap.First, overlap.Second);
            }

            if (shared)
            {
                keysTaken.Remove(tidy);
            }
            else
            {
                keysTaken.Add(tidy);

                MenuItem picked = item;
                commands.Bind(key.First.Key, () => PickRebound(picked), control: key.First.Control, whileTyping: (typing ?? true) && TypesNothing(key.First),
                    shift: key.First.Shift, alt: key.First.Alt, then: key.Second);
            }
        }

        rebound.RemoveAll(pair => pair.Label == wanted);
        rebound.Add((wanted, tidy));
        foreach (var (same, _) in Commands())
        {
            if (LabelOf(same) == wanted)
                same.Shortcut = tidy;
        }

        MarkReboundKeys();

        // A key on more than one command goes to the first of them that can be picked, this one first
        if (shared)
            BindShared(tidy, onNew ?? typing ?? true, item);
        if (left is not null && left != tidy)
            BindShared(left, typing ?? true, null);
        return true;
    }

    /// <summary>
    /// A key bound to every command on it, the first of them that can be picked right now has it. The designer's before
    /// the ones that are always there and the ones that can only be picked some of the time before the rest, this one
    /// first among its own (see <see cref="InKeyOrder"/>).
    /// </summary>
    private void BindShared(string tidy, bool whileTyping, MenuItem? first)
    {
        if (UIKeys.ReadChord(tidy) is not { } key)
            return;

        commands.Keys.Remove(key.First, key.Second);
        var on = new List<MenuItem>();
        if (first is not null)
            on.Add(first);
        foreach (MenuItem other in OthersOn(tidy, first is null ? string.Empty : LabelOf(first)))
            on.Add(other);
        if (on.Count == 0)
            return;

        on = InKeyOrder(on);
        commands.Bind(key.First.Key, () => PickFirst(on), control: key.First.Control, whileTyping: whileTyping && TypesNothing(key.First),
            shift: key.First.Shift, alt: key.First.Alt, then: key.Second);
    }

    // Whether a key types nothing, held with control or alt or an F key, so it can go off in a text box. A chord goes by
    // its first key, that's the one pressed while typing
    private static bool TypesNothing(UIKeyStroke key) => key.Control || key.Alt || key.Key is >= Key.F1 and <= Key.F25;

    // A command off its new key, if its menu would let it be picked right now
    private void PickRebound(MenuItem item) => PickFirst([item]);

    // The first of the commands on a key its menu would let be picked right now, the menu on the bar as well (the
    // Layout menu's only there in the designer)
    private void PickFirst(List<MenuItem> items)
    {
        foreach (MenuItem item in items)
        {
            if (commands.CanPick(item))
            {
                item.OnPicked?.Invoke();
                return;
            }
        }
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

    /// <summary>
    /// Every tick, the window's own keys (see <see cref="UpdateKeysWindow"/>), and while a key is being waited for, the
    /// first one pressed that isn't shift, control or alt. True while it waits, the key is for that and nothing else.
    /// </summary>
    private bool UpdateKeyCapture()
    {
        var keyboard = Engine.Input.Keyboard;
        if (UpdateKeysWindow())
            return true;

        if (keyFor is null)
            return false;

        if (keyDialog is not { IsOpen: true })
        {
            keyFor = null;
            return false;
        }

        // Not the press that asked (Enter in the filter or on a row), which would be the key it was put on
        if (keyJustAsked)
        {
            keyJustAsked = false;
            return true;
        }

        // A first key that waited long enough for a second is the key on its own
        if (keyFirst is { } alone && SecondsNow() - keyFirstAt > CHORD_CAPTURE)
        {
            keyFirst = null;
            KeyPressedFor(keyFor, alone.ToString());
            return true;
        }

        foreach (Key key in PressableKeys)
        {
            if (!keyboard.WasPressed(key))
                continue;

            // Escape is the question's own, it cancels it. Closed here first, the same press went on to the window under
            // it and closed that too
            if (key == Key.Escape && !keyboard.Control && !keyboard.Shift && !keyboard.Alt)
                return true;

            KeyStrokeFor(keyFor, key, keyboard.Control, keyboard.Shift, keyboard.Alt);
            break;
        }

        return true;
    }

    // Whether the key for a command was asked for this tick, the press that asked is let go
    private bool keyJustAsked;

    private static readonly Key[] PressableKeys = [.. Enum.GetValues<Key>().Distinct().Where(key => !UIKeys.IsHeld(key))];

    // How long a second key is waited for after the first, for a chord, in seconds
    private const double CHORD_CAPTURE = 1.5;

    // The first key pressed for a command while a second one is waited for, when it was pressed, and the line in the
    // dialog saying so
    private UIKeyStroke? keyFirst;
    private double keyFirstAt;
    private Label? keyChordLine;

    private static double SecondsNow() => System.Environment.TickCount64 / 1000.0;

    /// <summary>
    /// A key pressed while the dialog waits for one. The first waits a moment for a second, which makes a chord of the two
    /// ("Ctrl+K Ctrl+C"), Enter keeps the first on its own and so does nothing more being pressed (see UpdateKeyCapture).
    /// </summary>
    private void KeyStrokeFor(MenuItem item, Key key, bool control, bool shift, bool alt)
    {
        var stroke = new UIKeyStroke(key, control, shift, alt);
        if (keyFirst is { } first)
        {
            keyFirst = null;
            bool enter = key is Key.Enter or Key.KeypadEnter && !control && !shift && !alt;
            KeyPressedFor(item, enter ? first.ToString() : UIKeys.WriteChord(first, stroke));
            return;
        }

        keyFirst = stroke;
        keyFirstAt = SecondsNow();
        string said = Said("keys_chords.press_more", ("keys", UIShortcutText.Shown(stroke.ToString())));
        if (keyChordLine is { Parent: not null } line)
            line.Text = said;
        else if (keyDialog is { IsOpen: true } dialog)
            keyChordLine = dialog.Body.Add(new Label(said) { Anchor = Origin.Left, Align = Origin.Left });
    }

    /// <summary>
    /// The key pressed for a command, put on it and written down. One that other commands would want at the same time
    /// asks first, Replace, Keep Both or Cancel (DawnScene.KeysWindow.Conflicts), it was taken from them without a word.
    /// One that's only somebody else's somewhere else (the designer's) is shared with it without asking.
    /// </summary>
    private void KeyPressedFor(MenuItem item, Key key, bool control, bool shift, bool alt) => KeyPressedFor(item, UIKeys.Write(key, control, shift, alt));

    /// <summary>The same with the key written, one key or a chord of two ("Ctrl+K Ctrl+C").</summary>
    private void KeyPressedFor(MenuItem item, string written)
    {
        written = UIKeys.Tidy(written) ?? written;
        var had = OverlapsOn(written, LabelOf(item)).Where(other => KeysClash(item, other)).Select(LabelOf).ToList();

        keyFor = null;
        keyDialog?.Close();
        if (had.Count > 0)
        {
            AskAboutClash(item, written, had);
            return;
        }

        if (!Rebind(item.Label, written))
            return;

        SaveKeys();
        FillKeysTree();
        Say(Said("keys.now", ("command", LabelOf(item)), ("key", written)));
    }

    /// <summary>Every tick, whatever keys were pressed set off (see <see cref="UICommands"/>).</summary>
    private void UpdateKeys()
    {
        // A text box of the editor, or one of the layout that is being tried out in the canvas
        // A tree that has the keys for its arrows isn't typing, the editor's shortcuts still go
        bool typing = compositor.Focus is not (null or TreeView) || stage.Focus is not null;

        // While Change a key waits for one, the key is for that and nothing else. While a terminal has the keyboard its
        // keys are the shell's, Ctrl+W is a word rubbed out and not a file closed, the keys of Hex's that have to work
        // from a terminal are terminal commands (DawnScene.Terminal) and go through it. While the picture behind the code is
        // laid out by hand the keys are the overlay's, Enter and Escape and the arrows
        if (!UpdateKeyCapture() && compositor.Focus is not (TerminalView or ArrangeOverlay) && !SettingsFindPressed())
            commands.Keys.Update(Engine.Input.Keyboard, typing);
    }

    // Ctrl+F while the settings page has the keyboard is the page's own search, the code's find bar stays shut behind it
    private bool SettingsFindPressed()
    {
        var keyboard = Engine.Input.Keyboard;
        return keyboard.Control && keyboard.WasPressed(Key.F) && SettingsPage?.HasKeyboard() == true;
    }
}
