using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed record HexLinkAgentCall(HexLinkDevice Device, int? Chat, string? Receipt);

internal sealed record HexLinkAgentStarter(string Device, int Chat, string Receipt)
{
    internal JsonObject ToJson() => new() { ["device"] = Device, ["chat"] = Chat, ["receipt"] = Receipt };

    internal static HexLinkAgentStarter? Read(JsonObject? value) =>
        HexLinkData.String(value, "device", 64) is { Length: 64 } device &&
        HexLinkData.Int(value, "chat") is > 0 and var chat &&
        HexLinkData.String(value, "receipt", 32) is { } receipt && Guid.TryParseExact(receipt, "N", out _)
            ? new(device, chat, receipt) : null;

    internal bool Matches(HexLinkAgentCall call) => Device == call.Device.Id && Chat == call.Chat;
}

internal sealed class HexLinkAgentChild(HexLinkAgentStarter address)
{
    public HexLinkAgentStarter Address { get; } = address;
    private readonly HashSet<HexAgentApprovalStatus> reported = [];

    public IEnumerable<HexAgentApprovalStatus> NewApprovals(HexLinkRow row)
    {
        if (row.Id != Address.Chat || row.StarterReceipt != Address.Receipt)
            yield break;

        reported.IntersectWith(row.Approvals);
        foreach (HexAgentApprovalStatus approval in row.Approvals)
            if (reported.Add(approval))
                yield return approval;
    }
}
