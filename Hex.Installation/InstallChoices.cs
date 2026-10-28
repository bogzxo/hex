using System.Text.Json.Nodes;

namespace Hex.Installation;

/// <summary>Optional integrations. Missing fields retain the defaults used by older installer versions.</summary>
public sealed record InstallChoices(bool DesktopHex = true, bool DesktopDusk = false, bool ApplicationMenu = true,
    bool MenuDusk = true, bool WindowsAppPaths = true)
{
    public JsonObject ToJson() => new()
    {
        ["desktop_hex"] = DesktopHex, ["desktop_dusk"] = DesktopDusk, ["application_menu"] = ApplicationMenu,
        ["menu_dusk"] = MenuDusk, ["windows_app_paths"] = WindowsAppPaths
    };

    public static InstallChoices FromJson(JsonNode? node) => node is null ? new() : new(
        node["desktop_hex"]?.GetValue<bool>() ?? true, node["desktop_dusk"]?.GetValue<bool>() ?? false,
        node["application_menu"]?.GetValue<bool>() ?? true, node["menu_dusk"]?.GetValue<bool>() ?? true,
        node["windows_app_paths"]?.GetValue<bool>() ?? true);
}
