using System.Text.Json.Nodes;

namespace Hex.Installation;

/// <summary>Optional integrations and feature modules. Missing fields retain the defaults used by older installer versions.</summary>
public sealed record InstallChoices(bool DesktopHex = true, bool DesktopDusk = false, bool ApplicationMenu = true,
    bool MenuDusk = true, bool WindowsAppPaths = true, IReadOnlyDictionary<string, bool>? Modules = null)
{
    /// <summary>Known optional module ids in display order.</summary>
    public static readonly string[] KnownModules = ["link", "local_ai", "agentic", "notebooks", "designer"];

    /// <summary>Whether the named optional module is enabled (defaults to true when omitted).</summary>
    public bool IsModuleEnabled(string id) =>
        Modules is null || !Modules.TryGetValue(id, out bool enabled) || enabled;

    /// <summary>Effective module map including all known modules with their resolved enabled state.</summary>
    public Dictionary<string, bool> EffectiveModules()
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in KnownModules)
            map[id] = IsModuleEnabled(id);
        if (Modules is not null)
        {
            foreach (var (id, enabled) in Modules)
                map[id] = enabled;
        }
        return map;
    }

    public JsonObject ToJson()
    {
        var obj = new JsonObject
        {
            ["desktop_hex"] = DesktopHex,
            ["desktop_dusk"] = DesktopDusk,
            ["application_menu"] = ApplicationMenu,
            ["menu_dusk"] = MenuDusk,
            ["windows_app_paths"] = WindowsAppPaths
        };
        if (Modules is { Count: > 0 })
        {
            var mods = new JsonObject();
            foreach (var (id, enabled) in EffectiveModules().OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                mods[id] = enabled;
            obj["modules"] = mods;
        }
        return obj;
    }

    public static InstallChoices FromJson(JsonNode? node)
    {
        if (node is null)
            return new();

        Dictionary<string, bool>? modules = null;
        if (node["modules"] is JsonObject modObj)
        {
            modules = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in modObj)
            {
                if (value is not null)
                    modules[key] = value.GetValue<bool>();
            }
        }

        return new(
            node["desktop_hex"]?.GetValue<bool>() ?? true,
            node["desktop_dusk"]?.GetValue<bool>() ?? false,
            node["application_menu"]?.GetValue<bool>() ?? true,
            node["menu_dusk"]?.GetValue<bool>() ?? true,
            node["windows_app_paths"]?.GetValue<bool>() ?? true,
            modules);
    }

    public bool Equals(InstallChoices? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (DesktopHex != other.DesktopHex || DesktopDusk != other.DesktopDusk
            || ApplicationMenu != other.ApplicationMenu || MenuDusk != other.MenuDusk
            || WindowsAppPaths != other.WindowsAppPaths)
            return false;

        Dictionary<string, bool> left = EffectiveModules();
        Dictionary<string, bool> right = other.EffectiveModules();
        if (left.Count != right.Count)
            return false;
        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out bool otherValue) || value != otherValue)
                return false;
        }
        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(DesktopHex);
        hash.Add(DesktopDusk);
        hash.Add(ApplicationMenu);
        hash.Add(MenuDusk);
        hash.Add(WindowsAppPaths);
        foreach (var (key, value) in EffectiveModules().OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            hash.Add(key, StringComparer.OrdinalIgnoreCase);
            hash.Add(value);
        }
        return hash.ToHashCode();
    }
}
