using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// What a linked device may do to the chats of this machine's Agents panel (docs/hex-link-plan.md, 4.7). Stopping,
/// restarting, renaming, closing and deleting a chat the device itself started here takes Steer, as it always has. A
/// chat it did not start (this machine's own, another device's) takes Manage as well, one grant per device that is off
/// until the person here ticks it. Stopping or deleting a chat also stops or deletes every chat it started, so a chat
/// of the device's own that has somebody else's chat under it takes Manage too. Deleting a chat takes <c>confirm</c>
/// true, there is no asking the person here. Every call is audited here and shown to the person as a notice.
/// </summary>
internal static class HexLinkAgentManage
{
    /// <summary>The tools that manage a chat, which this check stands in front of.</summary>
    public static bool Manages(string tool) => tool is "hex_agent_stop" or "hex_agent_restart" or "hex_agent_rename" or "hex_agent_close";

    /// <summary>What a call does, in a word for the audit log and the notice.</summary>
    public static string Action(string tool, JsonObject arguments) => tool switch
    {
        "hex_agent_stop" => "stop",
        "hex_agent_restart" => "restart",
        "hex_agent_rename" => "rename",
        _ => arguments["clear"] is JsonValue clear && clear.TryGetValue(out bool yes) && yes ? "clear" : "delete"
    };

    /// <summary>
    /// Why a call is refused, null when it may go on: the chat is the device's own and Steer is granted, or Manage is
    /// granted too. A chat that isn't there is refused with the tool's own words.
    /// </summary>
    public static string? Refusal(string tool, JsonObject arguments, HexLinkDevice device, HexLinkRights rights, HexHubSnapshot fleet)
    {
        if (HexLinkData.Int(arguments, "id") is not { } id || !fleet.Rows.TryGetValue(id, out HexHubRow? row))
            return "There's no such chat, hex_agent_list says which there are.";

        bool manage = HexLinkAllowed.Has(rights, HexLinkRights.Manage) && HexLinkAllowed.Has(rights, HexLinkRights.Steer);
        string action = Action(tool, arguments);
        if (!manage)
        {
            if (!Owns(row, device))
                return $"#{id} wasn't started by {device.Name}. To {action} a chat it didn't start, this device has to allow Manage agents for it (Devices window, Manage agents).";
            if (tool is "hex_agent_stop" or "hex_agent_close" && Family(fleet, row).FirstOrDefault(member => !Owns(member, device)) is { } other)
                return $"#{id} has #{other.Id} under it, which wasn't started by {device.Name}, and to {action} #{id} stops that too. This device has to allow Manage agents for it first.";
        }

        if (action == "delete" && HexLinkData.Bool(arguments, "confirm") != true)
            return $"Deleting #{id} \"{row.Shown}\" can't be undone and takes the chats it started with it. Say confirm true to delete it, or clear true to keep it and empty it.";
        return null;
    }

    /// <summary>Whether a chat was started by this device's call.</summary>
    public static bool Owns(HexHubRow row, HexLinkDevice device) =>
        row.LinkDevice is { } owner && string.Equals(owner, device.Id, StringComparison.OrdinalIgnoreCase);

    // The chat and every chat started under it, itself left out
    private static IEnumerable<HexHubRow> Family(HexHubSnapshot fleet, HexHubRow top)
    {
        var seen = new HashSet<int> { top.Id };
        var queue = new Queue<int>([top.Id]);
        while (queue.Count > 0)
        {
            int parent = queue.Dequeue();
            foreach (HexHubRow row in fleet.Rows.Values.Where(each => each.Parent == parent))
            {
                if (seen.Add(row.Id))
                {
                    queue.Enqueue(row.Id);
                    yield return row;
                }
            }
        }
    }

    /// <summary>The line for the audit log and the notice of a managed chat, whether it went through or was refused.</summary>
    public static string Describe(string action, int? id, HexHubSnapshot fleet, bool own)
    {
        string shown = id is { } number && fleet.Rows.TryGetValue(number, out HexHubRow? row) ? $" \"{row.Shown}\"" : string.Empty;
        return $"{action} #{id}{shown}{(own ? " (started by it)" : " (started by someone else)")}";
    }
}
