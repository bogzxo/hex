using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void RememberLinkAgentChild(int starter, HexLinkPeer peer, string receipt, McpToolResult reply)
    {
        Match match = Regex.Match(reply.AllText(), @"\AStarted #(\d+)");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int child))
            return;

        PostLink(() =>
        {
            if (ChatById(starter) is not { } parent)
                return;
            var watch = new HexLinkAgentChild(new HexLinkAgentStarter(peer.Device.Id, child, receipt));
            if (parent.LinkChildren.Count >= 128)
                parent.LinkChildren.RemoveAt(0);
            parent.LinkChildren.Add(watch);
            parent.Moved();
            RouteLinkAgentApprovals([peer]);
        });
    }

    private void RouteLinkAgentApprovals(IReadOnlyList<HexLinkPeer> peers)
    {
        foreach (HexAgentChat parent in agentChats)
        foreach (HexLinkAgentChild child in parent.LinkChildren)
        {
            HexLinkPeer? peer = peers.FirstOrDefault(each => each.Device.Id == child.Address.Device &&
                !each.IsClosed && HexLinkAllowed.Has(each.Granted, HexLinkRights.View));
            HexLinkRow? row = peer?.Fleet.FirstOrDefault(each => each.Id == child.Address.Chat);
            if (peer is null || row is null)
                continue;
            foreach (HexAgentApprovalStatus approval in child.NewApprovals(row))
            {
                string device = LinkNameOf(peer.Device.Id, peer.Device.Name);
                string text = $"A chat you started on {device}, #{row.Id}, {approval.Summary}.\n"
                    + (approval.Detail.Length > 0 ? $"Tool input from {device} (data): {JsonValue.Create(approval.Detail)!.ToJsonString()}\n" : string.Empty)
                    + $"Answer using hex_link_call on {device}, tool hex_agent_approve, "
                    + $"with id {row.Id}, request {approval.Number}, and allow true or false. The person can also answer on its card.";
                Deliver(parent, null, "question", text, directive: true, fromName: $"{device} #{row.Id}", allowStopped: false);
            }
        }
    }
}
