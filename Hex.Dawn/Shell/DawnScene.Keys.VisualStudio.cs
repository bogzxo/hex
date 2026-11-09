using Horizon.UI;
using Horizon.UI.Components;

using Key = Horizon.Input.Key;

namespace Hex.Dawn;

// Visual Studio's keys, a setting of Editor, Code that the Visual Studio look turns on too. Hex's keys are VS Code's,
// and somebody coming from Visual Studio pressed Ctrl+- to go back and zoomed the code out. With it on, the commands
// where the two editors differ go on Visual Studio's keys through the same rebinding keys.hor goes through (see
// DawnScene.Keys), as a layer under keys.hor. keys.hor only ever has what was changed by hand, what the layer did
// isn't written to it, and turning the layer on or off puts Hex's own keys back, the layer on them and keys.hor over
// the top again.
internal sealed partial class DawnScene
{
    // What the layer moves, a command by its words and the key it goes on. "" takes a key off a command that Visual
    // Studio has on something else (Ctrl+, is Go to All there, not the settings). The zoom goes first, so Ctrl+- is free
    // for going back by the time that's put on it
    private static readonly (string Command, string Key)[] VisualStudioLayer =
    [
        ("designer_selection.zoom_in", "Ctrl+Shift+."),
        ("designer_selection.zoom_out", "Ctrl+Shift+,"),
        ("places.back", "Ctrl+-"),
        ("places.forward", "Ctrl+Shift+-"),
        ("code_server.format_document", "Ctrl+K Ctrl+D"),
        ("settings_window.open", ""),
        ("code.collapse_to_definitions", "Ctrl+M Ctrl+O"),
        ("code.toggle_all_outlining", "Ctrl+M Ctrl+L"),
        ("tests.show", "Ctrl+E T"),

        // The editing keys, which the editor answers itself and UIKeyboard.VisualStudioKeys moves there. Here it's what
        // the menus say. Ctrl+D duplicates, the next occurrence is Shift+Alt+. and Shift+Alt+arrows grow a box selection
        // where the lines were copied, Visual Studio has no key for that
        ("code.duplicate_line", "Ctrl+D"),
        ("code.add_next_occurrence", "Shift+Alt+."),
        ("code.select_all_occurrences", "Shift+Alt+;"),
        ("code.copy_line_up", ""),
        ("code.copy_line_down", ""),

        // The windows of the panel and the side on Visual Studio's keys. Ctrl+\ was splitting the editor, and the
        // Error List's Ctrl+\ Ctrl+E split it rather than going anywhere, so the split has no key, Visual Studio's hasn't.
        // Open Folder goes where Visual Studio has it, so Ctrl+Alt+O is the Output's
        ("menus.split_editor", ""),
        ("menus.problems", "Ctrl+\\ Ctrl+E"),
        ("welcome.open_folder", "Ctrl+Shift+Alt+O"),
        ("menus.output", "Ctrl+Alt+O"),

        // Quick Info, the Solution Explorer and closing a tab where Visual Studio has them, and Ctrl+Shift+T is Go to File
        // there, which has no reopening a closed tab. Hex's own keys for them stay as second keys
        ("code_server.show_hover", "Ctrl+K Ctrl+I"),
        ("menus.explorer", "Ctrl+Alt+L"),
        ("documents.close", "Ctrl+F4"),
        ("documents.reopen_closed", ""),
        ("find_menu.go_to_file", "Ctrl+Shift+T")
    ];

    // Second keys the layer adds, the command keeps its own as well. Visual Studio renames on F2 and Ctrl+R Ctrl+R both,
    // and going back on Alt+Left still works for a mouse's side buttons
    private static readonly (string Command, string Key)[] VisualStudioExtras =
    [
        ("go_to_all.command", "Ctrl+,"),
        ("code.rename_symbol", "Ctrl+R Ctrl+R"),
        ("places.back", "Alt+Left"),
        ("places.forward", "Alt+Right"),
        ("code_server.show_hover", "Ctrl+Shift+I"),
        ("menus.explorer", "Ctrl+Shift+E"),
        ("documents.close", "Ctrl+W"),
        ("find_menu.go_to_file", "Ctrl+P"),
        ("find_menu.go_to_file", "Ctrl+;"),
        ("menus.command_palette", "Ctrl+Q"),
        ("code_server.format_document", "Ctrl+E Ctrl+D"),
        ("code.toggle_comment", "Ctrl+K Ctrl+/"),
        ("code.next_problem", "Ctrl+Shift+F12"),
        ("code.go_to_bracket", "Ctrl+]"),

        // Quick Actions, Visual Studio has the lightbulb on Alt+Enter as well as Ctrl+.
        ("code_server.quick_fix", "Alt+Enter")
    ];

    // Second keys that are there with or without the layer, Visual Studio's for formatting, nothing else of Hex's is on it
    private static readonly (string Command, string Key)[] DefaultExtras =
    [
        ("code_server.format_document", "Ctrl+K Ctrl+D")
    ];

    // Whether the layer is on the keys right now, what keys.hor and the menus say is under it
    private bool keyLayerOn;

    // What the layer put in the list of changed keys, which isn't written to keys.hor
    private readonly List<(string Label, string Key)> layerKeys = [];

    // The second keys bound right now, to take off again
    private readonly List<string> extrasBound = [];

    // The second keys bound on a key something else has too, for as long as the code has the keyboard (the designer's
    // Ctrl+] brings to front, Visual Studio's goes to the other bracket). Bound once and never taken off, they ask
    // whether they're wanted right now
    private readonly HashSet<string> extrasShared = [];
    private readonly HashSet<string> extrasSharedOn = [];

    /// <summary>What was changed by hand, the changed keys less what the layer did.</summary>
    private IEnumerable<(string Label, string Key)> HandKeys() => rebound.Where(pair => !layerKeys.Contains(pair));

    /// <summary>The key the layer puts a command on, null for one it leaves alone.</summary>
    private string? LayerKeyOf(string label) => layerKeys.FindIndex(pair => pair.Label == label) is var at and >= 0 ? layerKeys[at].Key : null;

    /// <summary>
    /// The keys put together again for the setting, Hex's own, the layer when it's on and keys.hor over them. Nothing
    /// happens when the layer is already as the setting says, unless it's forced (as Hex starts, and Reset All).
    /// </summary>
    internal void ApplyKeyLayer(bool force = false)
    {
        if (!force && settings.VisualStudioKeys == keyLayerOn)
            return;

        // The second keys off first, a command whose own key is one the layer had as a second (Ctrl+W, once Close went on
        // Ctrl+F4) gets it back below and keeps it. Then everything back where it started, each on its own key without
        // taking it off anything
        foreach (string extra in extrasBound)
        {
            if (UIKeys.ReadChord(extra) is { } bound)
                commands.Keys.Remove(bound.First, bound.Second);
        }
        extrasBound.Clear();
        extrasSharedOn.Clear();
        foreach (var (label, original) in originalKeys.ToList())
            Rebind(label, original, keepOthers: true);
        rebound.Clear();
        keysTaken.Clear();
        originalKeys.Clear();
        layerKeys.Clear();
        BindZoom(new UIKeyStroke(Key.Equal, true, false, false), inward: true);
        BindZoom(new UIKeyStroke(Key.Minus, true, false, false), inward: false);
        BindExtras(DefaultExtras);

        // Changed rather than read as Hex starts, somebody chose. The editor's own keys go with it
        if (!force && keyLayerOn != settings.VisualStudioKeys)
            settings.VisualStudioKeysChosen = true;
        keyLayerOn = settings.VisualStudioKeys;
        UIKeyboard.VisualStudioKeys = keyLayerOn;
        if (keyLayerOn)
        {
            foreach (var (command, key) in VisualStudioLayer)
                Rebind(Said(command), key);
            BindZoom(new UIKeyStroke(Key.Period, true, true, false), inward: true);
            BindZoom(new UIKeyStroke(Key.Comma, true, true, false), inward: false);
            BindExtras(VisualStudioExtras);
            layerKeys.AddRange(rebound);
        }

        // keys.hor says the whole of what was changed by hand, what came off a key is in it as "", so nothing's taken
        // off of anything here. A key kept on two commands is still on both
        if (keysPath is not null && File.Exists(keysPath))
        {
            foreach (var (label, key) in ReadKeys(keysPath))
            {
                if (!Rebind(label, key, keepOthers: true))
                    Say(Said("keys_window.nothing_called", ("label", label), ("key", key)), error: true);
            }
        }

        if (keysTree is not null)
            FillKeysTree();
        shortcutHintsDirty = true;
    }

    /// <summary>
    /// A key of the zoom, the canvas's in the designer and the code's anywhere else. Two commands have the one name, and
    /// a key put on the name went to whichever came first, so it's bound the way DawnScene.Menus binds the usual ones.
    /// </summary>
    private void BindZoom(UIKeyStroke key, bool inward)
    {
        commands.Keys.Remove(key, null);
        commands.Bind(key.Key, () =>
        {
            if (LayoutTools)
            {
                if (inward) designer.ZoomCanvasIn();
                else designer.ZoomCanvasOut();
            }
            else
            {
                ZoomCode(inward ? CODE_ZOOM_STEP : 1.0f / CODE_ZOOM_STEP);
            }
        }, control: key.Control, whileTyping: true, shift: key.Shift, alt: key.Alt);
    }

    /// <summary>Second keys for commands, each going to the command the way its own key does. One already on something stays its.</summary>
    private void BindExtras((string Command, string Key)[] extras)
    {
        foreach (var (command, key) in extras)
        {
            string label = Said(command).TrimEnd('.');
            List<MenuItem> items = [.. Commands().Select(pair => pair.Item).Where(item => LabelOf(item) == label)];
            if (items.Count == 0 || UIKeys.ReadChord(key) is not { } read)
                continue;

            // A key something that isn't a command has is shared with it while the code has the keyboard, that's
            // asked first and goes no further when it was the code's. A notebook's cells keep theirs, Alt+Enter runs one
            if (commands.Keys.Has(read.First, read.Second))
            {
                if (read.Second is not null || extrasBound.Contains(key))
                    continue;

                extrasSharedOn.Add(key);
                if (extrasShared.Add(key))
                {
                    commands.Keys.Add(read.First.Key, () => extrasSharedOn.Contains(key) && compositor.Focus is CodeEditor && !NotebookShown && PickFirst(items),
                        control: read.First.Control, whileTyping: true, shift: read.First.Shift, alt: read.First.Alt, first: true);
                }
                continue;
            }

            commands.Bind(read.First.Key, () => PickFirst(items), control: read.First.Control, whileTyping: TypesNothing(read.First),
                shift: read.First.Shift, alt: read.First.Alt, then: read.Second);
            extrasBound.Add(UIKeys.WriteChord(read.First, read.Second));
        }
    }
}
