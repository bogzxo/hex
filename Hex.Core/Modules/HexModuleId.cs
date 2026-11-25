using System.Collections.Concurrent;

namespace Hex.Core.Modules;

/// <summary>
/// Stable string identifiers for Hex's optional feature modules. String-backed rather than an enum so future modules
/// can register without changing core types or settings/installer schemas.
/// </summary>
public static class HexModuleId
{
    public const string Link = "link";
    public const string LocalAi = "local_ai";
    public const string Agentic = "agentic";
    public const string Notebooks = "notebooks";
    public const string Designer = "designer";
}

/// <summary>
/// Describes one optional feature module that users can enable or disable in Settings and the Installer.
/// </summary>
/// <param name="Id">Stable module identifier, see <see cref="HexModuleId"/>.</param>
/// <param name="SettingKey">Key in <c>settings.hor</c> (e.g. <c>module_link</c>).</param>
/// <param name="TitleKey">Localisation key for the display name.</param>
/// <param name="HelpKey">Localisation key for the short description.</param>
/// <param name="DefaultEnabled">Whether the module is enabled on a fresh install when not overridden.</param>
/// <param name="RestartRequired">Whether toggling in Settings takes effect after restarting Hex.</param>
/// <param name="NativeLibrary">Optional cross-platform native shared library base name (without lib prefix or extension), null if pure managed.</param>
/// <param name="NativeFilePatterns">Optional file patterns (globs/substrings) inside the release archive that belong exclusively to this module so the installer can omit them when deselected.</param>
public sealed record HexModuleDescriptor(
    string Id,
    string SettingKey,
    string TitleKey,
    string HelpKey,
    bool DefaultEnabled = true,
    bool RestartRequired = true,
    string? NativeLibrary = null,
    string[]? NativeFilePatterns = null)
{
    /// <summary>Native archive file patterns owned solely by this module, never null.</summary>
    public string[] OwnedNativeFiles => NativeFilePatterns ?? [];
}

/// <summary>
/// Extensible registry of Hex optional modules. First-party modules are pre-registered here; additional modules can
/// register at startup via <see cref="Register"/> and automatically appear in module checks and diagnostics.
/// </summary>
public static class HexModuleCatalog
{
    private static readonly Lock Gate = new();
    private static readonly List<HexModuleDescriptor> Registered =
    [
        new(
            HexModuleId.Link,
            "module_link",
            "modules.link_title",
            "modules.link_help",
            DefaultEnabled: true,
            RestartRequired: false),
        new(
            HexModuleId.LocalAi,
            "module_local_ai",
            "modules.local_ai_title",
            "modules.local_ai_help",
            DefaultEnabled: true,
            RestartRequired: false),
        new(
            HexModuleId.Agentic,
            "module_agentic",
            "modules.agentic_title",
            "modules.agentic_help",
            DefaultEnabled: true,
            RestartRequired: true),
        new(
            HexModuleId.Notebooks,
            "module_notebooks",
            "modules.notebooks_title",
            "modules.notebooks_help",
            DefaultEnabled: true,
            RestartRequired: false,
            NativeLibrary: "Hex.Notebooks.Render",
            NativeFilePatterns: ["Hex.Notebooks.Render", "libSkiaSharp", "SkiaSharp", "Svg.Skia", "Svg.Custom", "Svg.SceneGraph", "CSharpMath", "ExCSS"]),
        new(
            HexModuleId.Designer,
            "module_designer",
            "modules.designer_title",
            "modules.designer_help",
            DefaultEnabled: true,
            RestartRequired: false)
    ];

    private static volatile HexModuleDescriptor[] snapshot = [.. Registered];

    /// <summary>All registered optional modules in display order.</summary>
    public static IReadOnlyList<HexModuleDescriptor> All => snapshot;

    /// <summary>Finds a registered module descriptor by id (case-insensitive), or null if unknown.</summary>
    public static HexModuleDescriptor? Find(string id)
    {
        foreach (HexModuleDescriptor descriptor in snapshot)
        {
            if (string.Equals(descriptor.Id, id, StringComparison.OrdinalIgnoreCase))
                return descriptor;
        }
        return null;
    }

    /// <summary>Registers a new optional module or replaces an existing descriptor with the same id.</summary>
    public static void Register(HexModuleDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (Gate)
        {
            int index = Registered.FindIndex(existing => string.Equals(existing.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                Registered[index] = descriptor;
            else
                Registered.Add(descriptor);
            snapshot = [.. Registered];
        }
    }
}

/// <summary>
/// Lifecycle contract for a runtime feature module host so new optional modules can plug into Dawn without adding
/// bespoke wiring throughout the scene.
/// </summary>
public interface IHexModuleHost
{
    /// <summary>Module identifier matching <see cref="HexModuleDescriptor.Id"/>.</summary>
    string Id { get; }

    /// <summary>Whether the host has active runtime state allocated.</summary>
    bool IsActive { get; }

    /// <summary>Activates the module lazily when first needed or toggled on. Simulation thread.</summary>
    void Activate();

    /// <summary>Releases runtime resources, timers, watchers and caches when disabled. Simulation thread.</summary>
    void Deactivate();

    /// <summary>Advances active module state on the simulation thread. Never called while inactive.</summary>
    void Tick(float dt);
}
