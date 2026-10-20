using System.Numerics;

using Horizon.HIDL;
using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Extensions, HIDL that drives Hex. The ones that come with it are in Assets/hex/extensions, anybody's own go in an
// all folder next to Hex, a folder each with an extension.hor in it. They are loaded as Hex starts, every one that
// isn't switched off in the settings, and on_start is called on each once the window is up. What they are handed is
// the hex object (HexExtensions.Api), which is all an extension can touch. What they put on screen is the editor's
// (IExtensionScreens, HexScene.ExtensionUI), their settings' rows the settings window's (HexScene.ExtensionSettings).
internal sealed partial class HexExtensions
{
    private const string BUILT_IN_EXTENSIONS = "Assets/hex/extensions", OWN_EXTENSIONS = "extensions";

    // The section of the settings the extensions are switched on and off in
    internal const string SETTINGS = "Extensions · Installed";

    private readonly DawnContext context;
    private readonly IExtensionScreens screens;
    private readonly List<HexExtension> all = [];

    /// <summary>Every extension there is, on or off, in the order they were found.</summary>
    internal IReadOnlyList<HexExtension> All => all;

    /// <summary>An extension made elsewhere put among the rest, and taken out, for the self test's own.</summary>
    internal void Add(HexExtension extension) => all.Add(extension);
    internal bool Remove(HexExtension extension) => all.Remove(extension);

    /// <summary>The extensions, what is on screen of them the scene's (<paramref name="on"/>), the rest through the context.</summary>
    public HexExtensions(DawnContext with, IExtensionScreens on)
    {
        context = with;
        screens = on;

        // What they find wrong with a file goes under its code and in the Problems panel with the rest
        context.Documents.AddDiagnostics(of => of.Path is { } path && extensionFound.ContainsKey(Path.GetFullPath(path)) ? [.. ProblemsOf(path)] : null);
    }

    /// <summary>Every tick, the keys of their commands, on_change, their folders read again and the files they watch.</summary>
    internal void Update(float dt)
    {
        UpdateCommandKeys();
        UpdateExtensionChange(dt);
        UpdateExtensionReloads();
        UpdateWatched();
    }

    /// <summary>Whether the extension a native of the editor is the C# side of is switched on and started (the designer).</summary>
    internal bool NativeOn(string native) => all.Any(extension => extension is { Enabled: true, Failed: null } && extension.Native == native);

    /// <summary>
    /// Finds every extension there is, once, before any file is opened. The built-in ones that are on are loaded straight
    /// away (their files run, not their on_start), so the designer is there to open the layout Dawn starts on.
    /// </summary>
    internal void Find()
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

                var extension = new HexExtension(inside, builtIn) { Enabled = !context.Settings.Values.DisabledExtensions.Contains(Path.GetFileName(inside)) };
                all.Add(extension);
                if (builtIn && extension.Enabled)
                    Load(extension);
            }
        }
    }

    /// <summary>Starts the ones that are switched on, once the editor is up and a file open.</summary>
    internal void StartAll()
    {
        foreach (HexExtension extension in all)
        {
            if (extension.Enabled)
                Start(extension, loaded: extension.Runtime is not null);
        }
    }

    /// <summary>Runs an extension's file, false when it didn't.</summary>
    private bool Load(HexExtension extension)
    {
        ObjectValue hex = HexOf(extension);
        bool loaded = extension.Load(hex, line => context.Notices.Note($"{extension.Name}: {line}"));

        // hex.name was the folder's until the file said what the extension is called
        hex.Properties["name"] = new StringValue(extension.Name);
        return loaded;
    }

    /// <summary>Loads an extension (unless it was already) and calls its on_start, or says why that didn't work and leaves it be.</summary>
    internal void Start(HexExtension extension, bool loaded = false)
    {
        if ((loaded ? extension.Failed is null : Load(extension)) && extension.Raise("on_start", HexOf(extension)))
        {
            Console.WriteLine($"[Hex] Started the extension '{extension.Name}'.");
            return;
        }

        Failed(extension);
    }

    /// <summary>
    /// Tells every extension that is on that something happened, <c>on_open(hex, file)</c> and the like, by calling its
    /// function of that name if it has one. One that goes wrong is switched off and says why.
    /// </summary>
    internal void Raise(string name, params IRuntimeValue[] arguments)
    {
        foreach (HexExtension extension in all)
        {
            if (!extension.Enabled || extension.Runtime is null || extension.Failed is not null)
                continue;

            if (!extension.Raise(name, [HexOf(extension), .. arguments]))
                Failed(extension);
        }
    }

    internal void Failed(HexExtension extension)
    {
        context.Notices.Notify(context.Strings.Said("extensions.failed", ("name", extension.Name), ("why", extension.Failed ?? "?")), error: true, (context.Strings.Said("extensions.open"), () =>
        {
            context.Windows.OpenSettings(SETTINGS);
        }));
    }

    /// <summary>What the all that are on added to the command palette, run like any other command.</summary>
    internal IEnumerable<(string Text, Action Picked)> Commands()
    {
        foreach (HexExtension extension in all)
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
                        Failed(from);
                });
            }
        }
    }

    /// <summary>Switches an extension on (it starts straight away) or off (what it did that Hex can take back, is).</summary>
    internal void Switch(HexExtension extension, bool on)
    {
        extension.Enabled = on;
        if (on)
            context.Settings.Values.DisabledExtensions.Remove(extension.Id);
        else
            context.Settings.Values.DisabledExtensions.Add(extension.Id);
        context.Settings.Save();

        if (on)
        {
            Start(extension);
        }
        else
        {
            screens.ForgetTitleNote(extension);
        }
    }
}
