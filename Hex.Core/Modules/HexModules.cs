using System.Collections.Concurrent;

namespace Hex.Core.Modules;

/// <summary>
/// Runtime feature gate and host registry for Hex's optional modules. Checked on startup and on live toggles so
/// disabled modules allocate nothing, register no ticks, and never load optional native libraries.
/// </summary>
public static class HexModules
{
    /// <summary>CLI flag to disable a module for this run: <c>--no-module link</c> or <c>--no-module=link,notebooks</c>.</summary>
    public const string DISABLE_ARGUMENT = "--no-module";

    private static readonly ConcurrentDictionary<string, bool> EnabledMap = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, bool> ForcedOff = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock HostsLock = new();
    private static readonly List<IHexModuleHost> Hosts = [];
    private static volatile IHexModuleHost[] hostSnapshot = [];

    /// <summary>Parses <c>--no-module</c> CLI flags before the editor starts.</summary>
    public static void ApplyArguments(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, DISABLE_ARGUMENT, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                ForceOffList(args[++i]);
            }
            else if (arg.StartsWith(DISABLE_ARGUMENT + "=", StringComparison.OrdinalIgnoreCase))
            {
                ForceOffList(arg[(DISABLE_ARGUMENT.Length + 1)..]);
            }
        }
    }

    private static void ForceOffList(string csv)
    {
        foreach (string raw in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            ForceOff(raw);
    }

    /// <summary>Forces a module off for the lifetime of this process (used by CLI overrides and tests).</summary>
    public static void ForceOff(string id)
    {
        ForcedOff[id] = true;
        EnabledMap[id] = false;
    }

    /// <summary>Whether the named module was forced off by a CLI override for this process.</summary>
    public static bool IsForcedOff(string id) => ForcedOff.ContainsKey(id);

    /// <summary>
    /// Whether the saved setting for <paramref name="moduleId"/> differs from the active session's state, meaning a
    /// restart is needed for the change to take effect. Returns false when the module is forced off for this run.
    /// </summary>
    public static bool IsConfiguredDifferent(string moduleId, bool configuredEnabled)
    {
        if (IsForcedOff(moduleId))
            return false;
        if (HexAi.Off && (string.Equals(moduleId, HexModuleId.Agentic, StringComparison.OrdinalIgnoreCase)
            || string.Equals(moduleId, HexModuleId.LocalAi, StringComparison.OrdinalIgnoreCase)))
            return false;

        return configuredEnabled != IsEnabled(moduleId);
    }

    /// <summary>Maps a settings key (such as <c>module_agentic</c>) to its registered module identifier.</summary>
    public static bool TryGetModuleIdForSettingKey(string key, out string moduleId)
    {
        foreach (HexModuleDescriptor descriptor in HexModuleCatalog.All)
        {
            if (string.Equals(descriptor.SettingKey, key, StringComparison.OrdinalIgnoreCase))
            {
                moduleId = descriptor.Id;
                return true;
            }
        }

        moduleId = string.Empty;
        return false;
    }

    /// <summary>Whether the given settings key is a module toggle whose configured value differs from the active session.</summary>
    public static bool ModuleKeyNeedsRestart(string key, bool configuredValue) =>
        TryGetModuleIdForSettingKey(key, out string moduleId) && IsConfiguredDifferent(moduleId, configuredValue);

    /// <summary>Counts how many registered optional modules have a configured value that differs from the active session.</summary>
    public static int PendingRestartCount(Func<string, bool> configuredOf)
    {
        ArgumentNullException.ThrowIfNull(configuredOf);
        int count = 0;
        foreach (HexModuleDescriptor descriptor in HexModuleCatalog.All)
        {
            if (IsConfiguredDifferent(descriptor.Id, configuredOf(descriptor.SettingKey)))
                count++;
        }
        return count;
    }

    /// <summary>Counts how many optional module flags in <paramref name="settings"/> differ from the active session.</summary>
    internal static int PendingModuleRestartCount(HexSettings settings)
    {
        int count = 0;
        if (IsConfiguredDifferent(HexModuleId.Link, settings.ModuleLink)) count++;
        if (IsConfiguredDifferent(HexModuleId.LocalAi, settings.ModuleLocalAi)) count++;
        if (IsConfiguredDifferent(HexModuleId.Agentic, settings.ModuleAgentic)) count++;
        if (IsConfiguredDifferent(HexModuleId.Notebooks, settings.ModuleNotebooks)) count++;
        if (IsConfiguredDifferent(HexModuleId.Designer, settings.ModuleDesigner)) count++;
        return count;
    }

    /// <summary>Whether any optional module flag in <paramref name="settings"/> requires a restart to take effect.</summary>
    internal static bool HasPendingModuleRestart(HexSettings settings) => PendingModuleRestartCount(settings) > 0;

    /// <summary>Applies user settings to the runtime module flags. CLI forced-off overrides always win.</summary>
    public static void SetEnabled(string id, bool enabled)
    {
        if (ForcedOff.ContainsKey(id))
        {
            EnabledMap[id] = false;
            return;
        }
        EnabledMap[id] = enabled;
    }

    /// <summary>
    /// Whether the named module is enabled right now. Respects CLI overrides, <see cref="HexAi.Off"/> (which turns off
    /// <see cref="HexModuleId.Agentic"/> and <see cref="HexModuleId.LocalAi"/>), and defaults from <see cref="HexModuleCatalog"/>.
    /// Zero allocations on the hot path.
    /// </summary>
    public static bool IsEnabled(string id)
    {
        if (HexAi.Off && (string.Equals(id, HexModuleId.Agentic, StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, HexModuleId.LocalAi, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (ForcedOff.ContainsKey(id))
            return false;

        if (EnabledMap.TryGetValue(id, out bool enabled))
            return enabled;

        return HexModuleCatalog.Find(id)?.DefaultEnabled ?? true;
    }

    /// <summary>
    /// Whether a module's optional native DLL is present on disk (or the module has no separate native library).
    /// Does NOT load the native library into the process.
    /// </summary>
    public static bool IsInstalled(string id, string? programFolder = null)
    {
        HexModuleDescriptor? descriptor = HexModuleCatalog.Find(id);
        if (descriptor?.NativeLibrary is not { Length: > 0 } lib)
            return true;

        return HexNativeModule.ExistsOnDisk(lib, programFolder ?? HexHost.ProgramFolder);
    }

    /// <summary>Registers a module host for centralized activation, deactivation, and ticking.</summary>
    public static void RegisterHost(IHexModuleHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        lock (HostsLock)
        {
            Hosts.RemoveAll(existing => string.Equals(existing.Id, host.Id, StringComparison.OrdinalIgnoreCase));
            Hosts.Add(host);
            hostSnapshot = [.. Hosts];
        }
    }

    /// <summary>Reconciles all registered hosts with the current enabled state. Simulation thread.</summary>
    public static void ReconcileHosts()
    {
        foreach (IHexModuleHost host in hostSnapshot)
        {
            bool wanted = IsEnabled(host.Id);
            if (!wanted && host.IsActive)
                host.Deactivate();
        }
    }

    /// <summary>Ticks only currently active registered hosts. Zero allocations.</summary>
    public static void TickActiveHosts(float dt)
    {
        IHexModuleHost[] current = hostSnapshot;
        for (int i = 0; i < current.Length; i++)
        {
            IHexModuleHost host = current[i];
            if (host.IsActive)
                host.Tick(dt);
        }
    }

    /// <summary>Resets runtime state. For unit tests only.</summary>
    internal static void ResetForTests()
    {
        EnabledMap.Clear();
        ForcedOff.Clear();
        lock (HostsLock)
        {
            Hosts.Clear();
            hostSnapshot = [];
        }
    }
}
