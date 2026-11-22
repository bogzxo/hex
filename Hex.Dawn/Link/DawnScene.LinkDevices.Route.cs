using System.Net;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    internal static HexLinkEndpoint[] LinkDialRoutes(HexLinkKnown known, IReadOnlyList<HexLinkNearby> nearby,
        IReadOnlyList<HexLinkEndpoint> remembered) =>
        known.PreferredRoute is { } chosen ? [chosen] : LinkDeviceRoutes(known, nearby, remembered);

    private static HexLinkEndpoint[] LinkDeviceRoutes(HexLinkKnown known, IReadOnlyList<HexLinkNearby> nearby,
        IReadOnlyList<HexLinkEndpoint> remembered)
    {
        var endpoints = LinkAddressesOf(known, nearby, remembered)
            .Select(route => HexLinkRedial.Endpoint(route.Host, route.Port)).OfType<HexLinkEndpoint>();
        if (known.PreferredRoute is { } chosen)
            endpoints = endpoints.Append(chosen);
        return [.. endpoints.Distinct().OrderBy(route => LinkRouteIsVpn(route.Host))];
    }

    private HexLinkEndpoint[] LinkDeviceRoutes(HexLinkKnown known) =>
        LinkDeviceRoutes(known, LinkNearby, linkRedial.Addresses(known.Device.Id));

    private static bool LinkRouteIsVpn(string address) => IPAddress.TryParse(address, out IPAddress? ip) && LinkIsVpn(ip);

    private static string LinkRouteAddress(HexLinkEndpoint route) =>
        route.Host.Contains(':') ? $"[{route.Host}]:{route.Port}" : $"{route.Host}:{route.Port}";

    private void LinkAddRouteMenu(Menu menu, HexLinkKnown known)
    {
        var reconnect = new Menu(Said("link.devices_route_reconnect"));
        string id = known.Device.Id;
        menu.Add(new MenuItem { Label = reconnect.Title, Submenu = reconnect, Icon = "plug", IsEnabled = () => LinkOn });
        reconnect.Add(Said("link.devices_route_auto"), () => LinkChooseRoute(id, null))
            .IsChecked = () => linkTrust?.Find(id)?.PreferredRoute is null;
        foreach (var group in LinkDeviceRoutes(known).GroupBy(route => LinkRouteIsVpn(route.Host)))
        {
            reconnect.AddSeparator();
            string label = Said(group.Key ? "link.devices_route_vpn" : "link.devices_route_lan");
            foreach (HexLinkEndpoint route in group)
                reconnect.Add($"{label} · {LinkRouteAddress(route)}", () => LinkChooseRoute(id, route))
                    .IsChecked = () => linkTrust?.Find(id)?.PreferredRoute == route;
        }
    }

    private void LinkChooseRoute(string id, HexLinkEndpoint? endpoint)
    {
        if (LinkSetRoute(id, endpoint) is { } failure)
            Say(failure, error: true);
    }

    private string? LinkSetRoute(string id, HexLinkEndpoint? endpoint)
    {
        if (linkTransport is not { } transport || linkTrust is null)
            return Said("link.pair_off_text");
        try
        {
            if (!linkTrust.SetRoute(id, endpoint))
                return Said("link.devices_route_unknown");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Said("link.devices_route_save_failed", ("reason", error.Message));
        }

        if (linkManualDials.Remove(id, out LinkDeviceConnection? old))
            old.Attempt?.Cancel();
        link?.Remove(id, Said("link.devices_route_switching"));
        transport.LinkTo(id)?.Dispose(); // The authenticated transport may still be saying hello.
        linkHeldOff.Remove(id);
        linkRedial.Soon(id);
        linkDirty = linkWindowStale = true;
        LinkRedialDue();
        return null;
    }

    private HexMcpTool LinkRouteTool() => new(new McpTool("hex_link_route", "Reconnect a known device via an address",
        "Set a paired device's route on this Hex and reconnect with its existing identity. Use an endpoint shown by hex_link_devices, or automatic to prefer LAN with VPN as fallback. The selection is remembered until changed.",
        McpSchema.Object([
            ("device", McpSchema.String("The paired device's name or start of its id.")),
            ("address", McpSchema.String("A listed host:port, [IPv6]:port, or automatic."))], "device", "address")),
        LinkRouteToolRun, NeedsEditor: false, Alongside: true);

    private IEnumerable<McpToolResult?> LinkRouteToolRun(JsonObject args)
    {
        if (!LinkOn)
        {
            yield return McpToolResult.Error(LINK_OFF_TOOL);
            yield break;
        }
        string asked = args.String("device")?.Trim() ?? "";
        HexLinkKnown[] matches = [.. LinkKnown.Where(known => known.Shown.Equals(asked, StringComparison.OrdinalIgnoreCase)
            || known.Device.Name.Equals(asked, StringComparison.OrdinalIgnoreCase))];
        if (matches.Length == 0 && asked.Length >= 4)
            matches = [.. LinkKnown.Where(known => known.Device.Id.StartsWith(asked, StringComparison.OrdinalIgnoreCase))];
        if (matches is not [var device])
        {
            yield return McpToolResult.Error(matches.Length == 0
                ? "No paired device matches. Use a name or id shown by hex_link_devices."
                : "More than one paired device matches. Use an id shown by hex_link_devices.");
            yield break;
        }

        string address = args.String("address")?.Trim() ?? "";
        HexLinkEndpoint? route = null;
        if (!address.Equals("automatic", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryLinkAddress(address, 0, out string host, out int port) ||
                HexLinkRedial.Endpoint(host, port) is not { } picked || !LinkDeviceRoutes(device).Contains(picked))
            {
                yield return McpToolResult.Error("Choose an address and port shown by hex_link_devices, or use automatic.");
                yield break;
            }
            route = picked;
        }

        yield return LinkSetRoute(device.Device.Id, route) is { } failure
            ? McpToolResult.Error(failure)
            : McpToolResult.Text($"Reconnecting to {device.Shown} via {(route is { } endpoint ? LinkRouteAddress(endpoint) : "Automatic (LAN first)")}. Check hex_link_devices for the current route.");
    }
}
