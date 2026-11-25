using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkPeer
{
    /// <summary>The tunnels over this link, set by <see cref="HexLink"/> (they are shared with the host half).</summary>
    internal HexLinkTunnels? Tunnels { get; set; }

    /// <summary>What the device shares with this machine, which takes its Ports right here. Throws what it refused with.</summary>
    public async Task<IReadOnlyList<HexLinkRemoteShare>> ListPorts(CancellationToken cancel = default)
    {
        JsonNode? reply = await session.Ask("ports/list", new JsonObject(), cancel);
        var shares = new List<HexLinkRemoteShare>();
        foreach (JsonObject each in HexLinkData.Objects(reply as JsonObject, "shares", HexLinkPorts.MOST_SHARES))
        {
            if (HexLinkData.String(each, "id", 80) is { Length: > 0 } id && HexLinkData.Int(each, "port") is { } port)
                shares.Add(new HexLinkRemoteShare(id, HexLinkData.String(each, "name", 40) ?? id, port, HexLinkData.Bool(each, "lan") == true));
        }

        return shares;
    }
}
