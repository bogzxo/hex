using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkHost
{
    /// <summary>The tunnels over this link, serving what the device opens here and using what it shares.</summary>
    public HexLinkTunnels Tunnels { get; }

    /// <summary>The device said its shares changed (ports/changed), for <see cref="HexLink"/> to list them again.</summary>
    public Action? PortsChanged { get; set; }

    private Task<JsonNode?> PortsRequest(string method, JsonObject parameters, CancellationToken cancel) => method switch
    {
        "ports/list" => Task.FromResult<JsonNode?>(Tunnels.List()),
        "ports/connect" => Tunnels.Connect(parameters, cancel),
        "ports/ready" => Task.FromResult(Tunnels.Ready(parameters)),
        "ports/data" => Tunnels.Data(parameters, cancel),
        _ => Task.FromResult(Tunnels.Close(parameters))
    };
}
