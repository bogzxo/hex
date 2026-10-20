using System.Numerics;

using Horizon.HIDL;
using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Extensions, HIDL that drives Hex. The ones that come with it are in Assets/hex/extensions, anybody's own go in an
// extensions folder next to Hex, a folder each with an extension.hor in it. They are loaded as Hex starts, every one that
// isn't switched off in the settings, and on_start is called on each once the window is up. What they are handed is
// the hex object (HexScene.ExtensionApi), which is all an extension can touch. What they put on screen is
// HexScene.ExtensionUI, their settings and tabs HexScene.ExtensionSettings.
internal sealed partial class HexScene
{
    private const string BUILT_IN_EXTENSIONS = "Assets/hex/extensions", OWN_EXTENSIONS = "extensions";

    private readonly List<HexExtension> extensions = [];

    // What an extension put in the title bar after Hex, in brackets, and which one it was
    private string titleNote = string.Empty;
    private HexExtension? titleNoteBy;

    /// <summary>
    /// Finds every extension there is, once, before any file is opened. The built-in ones that are on are loaded straight
    /// away (their files run, not their on_start), so the designer is there to open the layout Dawn starts on.
    /// </summary>
    private void FindExtensions()
    {
        foreach (var (folder, builtIn) in new[] { (BUILT_IN_EXTENSIONS, true), (OWN_EXTENSIONS, false) })
        {
            string full = Path.Combine(AppContext.BaseDirectory, folder);
            if (!Directory.Exists(full))
                continue;

            foreach (string inside in Directory.GetDirectories(full).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(Path.Combine(inside, HexExtension.FILE)))
                    continue;

                var extension = new HexExtension(inside, builtIn) { Enabled = !settings.DisabledExtensions.Contains(Path.GetFileName(inside)) };
                extensions.Add(extension);
                if (builtIn && extension.Enabled)
                    LoadExtension(extension);
            }
        }
    }

    /// <summary>Starts the ones that are switched on, once the editor is up and a file open.</summary>
    private void LoadExtensions()
    {
        foreach (HexExtension extension in extensions)
        {
            if (extension.Enabled)
                StartExtension(extension, loaded: extension.Runtime is not null);
        }
    }

    /// <summary>Runs an extension's file, false when it didn't.</summary>
    private bool LoadExtension(HexExtension extension)
    {
        ObjectValue hex = HexOf(extension);
        bool loaded = extension.Load(hex, line => Note($"{extension.Name}: {line}"));

        // hex.name was the folder's until the file said what the extension is called
        hex.Properties["name"] = new StringValue(extension.Name);
        return loaded;
    }

    /// <summary>Loads an extension (unless it was already) and calls its on_start, or says why that didn't work and leaves it be.</summary>
    private void StartExtension(HexExtension extension, bool loaded = false)
    {
        if ((loaded ? extension.Failed is null : LoadExtension(extension)) && extension.Raise("on_start", HexOf(extension)))
        {
            Console.WriteLine($"[Hex] Started the extension '{extension.Name}'.");
            return;
        }

        ExtensionFailed(extension);
    }

    /// <summary>
    /// Tells every extension that is on that something happened, <c>on_open(hex, file)</c> and the like, by calling its
    /// function of that name if it has one. One that goes wrong is switched off and says why.
    /// </summary>
    private void RaiseExtensions(string name, params IRuntimeValue[] arguments)
    {
        foreach (HexExtension extension in extensions)
        {
            if (!extension.Enabled || extension.Runtime is null || extension.Failed is not null)
                continue;

            if (!extension.Raise(name, [HexOf(extension), .. arguments]))
                ExtensionFailed(extension);
        }
    }

    private void ExtensionFailed(HexExtension extension)
    {
        Notify(Said("extensions.failed", ("name", extension.Name), ("why", extension.Failed ?? "?")), error: true, (Said("extensions.open"), () =>
        {
            OpenSettings(EXTENSIONS);
        }));
    }

    /// <summary>What the extensions that are on added to the command palette, run like any other command.</summary>
    private IEnumerable<(string Text, Action Picked)> ExtensionCommands()
    {
        foreach (HexExtension extension in extensions)
        {
            if (!extension.Enabled || extension.Failed is not null)
                continue;

            foreach (var (name, function) in extension.Commands)
            {
                HexExtension from = extension;
                IRuntimeValue called = function;
                string key = extension.CommandKeys.FirstOrDefault(command => command.Name == name).Key ?? string.Empty;
                yield return ($"{extension.Name}: {name}{(key.Length > 0 ? $"   ({key})" : string.Empty)}", () =>
                {
                    if (!from.Invoke(called, HexOf(from)))
                        ExtensionFailed(from);
                });
            }
        }
    }

    /// <summary>Switches an extension on (it starts straight away) or off (what it did that Hex can take back, is).</summary>
    private void SwitchExtension(HexExtension extension, bool on)
    {
        extension.Enabled = on;
        if (on)
            settings.DisabledExtensions.Remove(extension.Id);
        else
            settings.DisabledExtensions.Add(extension.Id);
        SaveSettings();

        if (on)
        {
            StartExtension(extension);
        }
        else if (!on && titleNoteBy == extension)
        {
            titleNote = string.Empty;
            titleNoteBy = null;
            tabsDirty = true;
        }
    }
}
