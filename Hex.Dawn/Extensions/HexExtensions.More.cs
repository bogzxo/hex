using System.Collections.Concurrent;

using Key = Silk.NET.Input.Key;

using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// What all were missing once one had a real UI (the move editor's plan asked for every one of these). A menu at
// the pointer (hex.menu), a question with buttons (hex.ask), a name asked for (hex.prompt), a key on a command
// (hex.command's third argument, shown in the palette), letters, digits and shift reaching a view as well as the arrows,
// on_change as a file is typed into, and an extension read again when a file of its folder changes, its views of open
// files put back on them, so writing one isn't a restart of Hex a change.
internal sealed partial class HexExtensions
{
    // How long a file is left alone after it was typed into before on_change, and an extension's folder after it changed
    private const float CHANGE_QUIET = 0.3f;
    private const long RELOAD_QUIET_MS = 300;

    private (string, IRuntimeValue)[] Prompting(HexExtension extension) =>
    [
        ("menu", Natives.Action("menu", args =>
        {
            if (args[0] is not ListValue items)
                throw args.Wrong("hex.menu takes a list of items, { label, icon, key, on_pressed, items }.");
            var made = new Menu(extension.Name);
            FillMenu(extension, made, items);
            context.Windows.Menu(made);
        })),
        ("ask", Natives.Action("ask", args =>
        {
            ObjectValue asked = args.Object(0);
            IRuntimeValue then = args[1];
            string[] buttons = asked.Properties.TryGetValue("buttons", out IRuntimeValue? listed) && listed is ListValue list
                ? [.. list.Items.Select(item => item is StringValue text ? text.Value : "?")]
                : ["OK", "Cancel"];

            context.Windows.Ask(Text(asked, "title") ?? extension.Name, Text(asked, "text") ?? string.Empty, buttons,
                label => Answered(extension, then, new StringValue(label)));
        })),
        ("prompt", Natives.Action("prompt", args =>
        {
            ObjectValue asked = args.Object(0);
            IRuntimeValue then = args[1];
            context.Windows.Prompt(Text(asked, "title") ?? extension.Name, Text(asked, "text") ?? string.Empty, Text(asked, "value") ?? string.Empty,
                Text(asked, "button") ?? "OK", Text(asked, "placeholder") ?? string.Empty, typed => Answered(extension, then, new StringValue(typed)));
        }))
    ];

    // What an extension handed over to be told the answer, told it, switched off if it goes wrong
    private void Answered(HexExtension extension, IRuntimeValue then, IRuntimeValue answer)
    {
        if (extension.Enabled && !extension.Invoke(then, HexOf(extension), answer))
            Failed(extension);
    }

    /// <summary>A menu filled out of an extension's list, "-" a separator, an item with items a menu of its own.</summary>
    private void FillMenu(HexExtension extension, Menu made, ListValue items)
    {
        foreach (IRuntimeValue item in items.Items)
        {
            if (item is StringValue { Value: "-" } || item is ObjectValue { Properties.Count: 0 })
            {
                made.AddSeparator();
                continue;
            }

            if (item is not ObjectValue entry)
                continue;

            string label = Text(entry, "label") ?? "?";
            if (entry.Properties.TryGetValue("items", out IRuntimeValue? inside) && inside is ListValue more)
            {
                FillMenu(extension, made.AddMenu(label, Text(entry, "icon") ?? string.Empty), more);
                continue;
            }

            IRuntimeValue? pressed = entry.Properties.GetValueOrDefault("on_pressed");
            MenuItem added = made.Add(label, () =>
            {
                if (pressed is not null && pressed is not NullValue && !extension.Invoke(pressed, HexOf(extension)))
                    Failed(extension);
            }, Text(entry, "key") ?? string.Empty, Text(entry, "icon") ?? string.Empty);
            if (Flag(entry, "enabled") is false)
                added.IsEnabled = () => false;
        }
    }

    /* A key on a command */

    /// <summary>A key as Hex's menus write it, "Ctrl+Shift+K", as the key and what's held with it, null for one it can't read.</summary>
    private static (Key Key, bool Control, bool Shift, bool Alt)? ReadKey(string written) => UIKeys.Read(written);

    // The commands with keys, checked every tick, they come and go with their extension so they aren't Hex's own shortcuts
    private readonly Dictionary<(Key, bool, bool, bool), bool> commandKeysDown = [];

    private void UpdateCommandKeys()
    {
        var keyboard = context.Keyboard;
        foreach (HexExtension extension in all)
        {
            if (!extension.Enabled || extension.Failed is not null || extension.CommandKeys.Count == 0)
                continue;

            foreach (var (name, written) in extension.CommandKeys)
            {
                // A key without control or alt is a letter being typed while anything has the keyboard
                if (ReadKey(written) is not { } key || (context.Typing && !key.Control && !key.Alt))
                    continue;

                bool down = keyboard.IsDown(key.Key) && keyboard.Control == key.Control && keyboard.Shift == key.Shift && keyboard.Alt == key.Alt;
                bool was = commandKeysDown.GetValueOrDefault(key);
                commandKeysDown[key] = down;
                if (!down || was)
                    continue;

                foreach (var (commandName, function) in extension.Commands)
                {
                    if (commandName == name && !extension.Invoke(function, HexOf(extension)))
                        Failed(extension);
                }
            }
        }
    }

    /* on_change, as a file is typed into */

    private (HexDocument? Document, int Version) changeTold = (null, -1), changeSeen = (null, -1);
    private float changeQuiet;

    private void UpdateExtensionChange(float dt)
    {
        HexDocument document = context.Documents.Current;
        if (document.Path is not { } path || !document.IsText)
            return;

        var now = (document, document.Editor.Document.Version);
        if (now != changeSeen)
        {
            // A file just shown isn't a change, only typing into it is
            bool typed = changeSeen.Item1 == document;
            changeSeen = now;
            changeQuiet = 0.0f;
            if (!typed)
                changeTold = now;
            return;
        }

        changeQuiet += dt;
        if (changeQuiet >= CHANGE_QUIET && changeTold != now)
        {
            changeTold = now;
            Raise("on_change", new StringValue(Path.GetFullPath(path)));
        }
    }

    /* Reading an extension again when its folder changes */

    internal readonly Dictionary<HexExtension, FileSystemWatcher> extensionWatchers = [];
    private readonly ConcurrentDictionary<HexExtension, long> extensionsChanged = new();

    /// <summary>The folder of every extension that is on watched, so a change to it reads it again.</summary>
    internal void WatchFolders()
    {
        foreach (HexExtension extension in all)
        {
            if (extensionWatchers.ContainsKey(extension) || !Directory.Exists(extension.Folder))
                continue;

            try
            {
                var watcher = new FileSystemWatcher(extension.Folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
                };
                HexExtension changed = extension;
                watcher.Changed += (_, e) => ExtensionFileChanged(changed, e.FullPath);
                watcher.Created += (_, e) => ExtensionFileChanged(changed, e.FullPath);
                watcher.Renamed += (_, e) => ExtensionFileChanged(changed, e.FullPath);
                watcher.EnableRaisingEvents = true;
                extensionWatchers[extension] = watcher;
            }
            catch (Exception)
            {
                // a folder that can't be watched is read again by switching the extension off and on
            }
        }
    }

    // Only its code and its layouts, an extension writing a file of its own folder isn't a change to it
    private void ExtensionFileChanged(HexExtension extension, string path)
    {
        if (path.EndsWith(".hor", StringComparison.OrdinalIgnoreCase))
            extensionsChanged[extension] = System.Environment.TickCount64;
    }

    /// <summary>Every tick, an extension whose folder changed and was left alone a moment read again.</summary>
    private void UpdateExtensionReloads()
    {
        if (extensionsChanged.IsEmpty)
            return;

        long now = System.Environment.TickCount64;
        foreach (var (extension, at) in extensionsChanged)
        {
            if (now - at < RELOAD_QUIET_MS)
                continue;

            extensionsChanged.TryRemove(extension, out _);
            if (extension.Enabled)
                ReloadExtension(extension);
        }
    }

    /// <summary>
    /// An extension read again and started anew. What it put up comes down first (its views of open files, its windows, its
    /// commands), and its views go back on the files that were showing them once it has said again what views it has.
    /// </summary>
    private void ReloadExtension(HexExtension extension)
    {
        screens.Reload(extension, () => Start(extension));
        context.Notices.Say($"{extension.Name} read again");
    }
}
