using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// The agents of linked devices in Agentic's fleet and the Agents view's list (docs/hex-link-plan.md, 5.1 and 5.5).
// Under this machine's chats a section for each connected device that lets this one see, headed by its name in its own
// colour and chips of what this machine may do there, its agents as the same cards local ones are, started ones under
// the one that started them. A card is made from a row of the device's fleet as a stand-in chat that's only ever drawn
// (never in the hub, never saved, never counted), and picking it opens the chat itself, mirrored (DawnScene.LinkChats).
//
// Every card of another device carries a badge of it, a computer's glyph and its name in its colour. While any device
// is linked this machine's own cards carry a quiet one with this device's name, so two screens side by side read the
// same: every agent says which machine it runs on. With nothing linked none of this is made and the lists' keys don't
// move for it.
internal sealed partial class DawnScene
{
    /// <summary>A chat of another device as a card knows it: the device by id and the name it goes by, and the chat's id there.</summary>
    internal sealed record LinkRemoteCard(string DeviceId, string DeviceName, int Id);

    // The stand-in chats the cards are drawn from, by device and the chat's id there, with the fleet version each was
    // made at, and every remote chat (stand-in or mirror) to the card it is
    private readonly Dictionary<(string Device, int Id), HexAgentChat> linkStandIns = [];
    private readonly Dictionary<HexAgentChat, LinkRemoteCard> linkRemoteOf = [];
    private readonly Dictionary<string, (long Version, string Name)> linkStandInsAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the cards carry the badge of the machine they run on, while any device is linked.</summary>
    private bool LinkBadgesShown => link is not null && LinkPeers.Count > 0;

    /// <summary>Tracks the linked fleets and the open remote chat without building a list of devices.</summary>
    private long LinkFleetKey()
    {
        IReadOnlyList<HexLinkPeer> peers = LinkPeers;
        if (peers.Count == 0)
            return 0;
        long key = 23 + (linkRemoteOpen is { } open ? string.GetHashCode(open.DeviceId, StringComparison.Ordinal) * 31L + open.Id : 0);
        for (int i = 0; i < peers.Count; i++)
        {
            HexLinkPeer peer = peers[i];
            key = key * 31 + string.GetHashCode(peer.Device.Id, StringComparison.Ordinal);
            key = key * 31 + string.GetHashCode(LinkNameOf(peer.Device.Id, peer.Device.Name), StringComparison.Ordinal);
            key = key * 31 + (int)peer.Granted;
            key = key * 31 + (peer.IsClosed ? 1 : 0);
            key = key * 31 + peer.FleetVersion;
            key = key * 31 + peer.Fleet.Count;
            key = key * 31 + string.GetHashCode(peer.Session.Connection.Address, StringComparison.Ordinal);
        }

        return key;
    }

    /// <summary>
    /// A device's fleet in the order its cards go: the chats nobody started first, each followed by those it started
    /// (and theirs), how deep each is the step its card is in by. A row whose starter isn't in the fleet goes at the top.
    /// </summary>
    internal static List<(HexLinkRow Row, int Step)> LinkFleetInOrder(IReadOnlyList<HexLinkRow> fleet)
    {
        var ordered = new List<(HexLinkRow, int)>(fleet.Count);
        var ids = new HashSet<int>(fleet.Select(row => row.Id));
        var children = new Dictionary<int, List<HexLinkRow>>();
        var tops = new List<HexLinkRow>();
        foreach (HexLinkRow row in fleet)
        {
            if (row.Parent is { } parent && parent != row.Id && ids.Contains(parent))
            {
                if (!children.TryGetValue(parent, out List<HexLinkRow>? under))
                    children[parent] = under = [];
                under.Add(row);
            }
            else
                tops.Add(row);
        }

        var placed = new HashSet<int>();
        void Place(HexLinkRow row, int step)
        {
            if (!placed.Add(row.Id) || step > 16)
                return;
            ordered.Add((row, step));
            if (children.TryGetValue(row.Id, out List<HexLinkRow>? under))
            {
                foreach (HexLinkRow child in under.OrderBy(each => each.Id))
                    Place(child, step + 1);
            }
        }

        foreach (HexLinkRow top in tops.OrderByDescending(each => each.Id))
            Place(top, 0);
        return ordered;
    }

    /// <summary>
    /// The stand-in chat a card of a device's fleet is drawn from, made again when the device's fleet moved: its name,
    /// agent and model, who started it, how it's doing (running, stopped, failed, done with its last reply) and what it
    /// cost, as the row says. Only ever drawn, never in the hub.
    /// </summary>
    private HexAgentChat LinkStandIn(string device, string name, long version, HexLinkRow row)
    {
        // Made again when its fleet moved or it's shown by another name now
        var now = (version, name);
        if (linkStandInsAt.GetValueOrDefault(device, (-1, string.Empty)) != now)
        {
            // A new version of the fleet, every stand-in of the device made again from it
            foreach (var key in linkStandIns.Keys.Where(key => key.Device == device).ToList())
            {
                HexAgentChat old = linkStandIns[key];
                linkStandIns.Remove(key);
                if (old != agentChat)
                    linkRemoteOf.Remove(old);
            }

            linkStandInsAt[device] = now;
        }

        if (linkStandIns.TryGetValue((device, row.Id), out HexAgentChat? made))
            return made;

        var chat = new HexAgentChat(row.Id, row.Agent.Length > 0 ? row.Agent : "agent")
        {
            Parent = row.Parent, Depth = row.Depth, Name = row.Name.Length > 0 ? row.Name : null, Model = row.Model, Cost = row.Cost
        };
        string reply = row.Reply.Length > 0 ? row.Reply : row.PlanStep ?? string.Empty;
        bool running = row.State == "running";
        if (row.State.StartsWith("running (", StringComparison.Ordinal))
            chat.RunGoing = true;
        else if (row.State == "stopped")
        {
            chat.Say(reply);
            chat.Stopped = true;
        }
        else if (row.State == "failed")
            chat.Error(reply.Length > 0 ? reply : Said("agents_panel.card_failed"));
        else if (row.State != "new" && !running)
            chat.Say(reply);
        chat.MirrorHead(row.Name, running, row.Seconds);
        chat.Seen = chat.Version;
        linkStandIns[(device, row.Id)] = chat;
        linkRemoteOf[chat] = new LinkRemoteCard(device, now.Item2, row.Id);
        return chat;
    }

    /// <summary>The remote chat a chat is (a stand-in or a mirror), null for one of this machine's.</summary>
    private LinkRemoteCard? LinkRemoteOf(HexAgentChat? chat) =>
        chat is not null && linkRemoteOf.Count > 0 && linkRemoteOf.TryGetValue(chat, out LinkRemoteCard? remote) ? remote : null;

    /// <summary>What started a remote chat as its card says it, the starter's number and name there, and its agent system.</summary>
    private string LinkStarterOf(LinkRemoteCard remote, HexAgentChat chat, string system)
    {
        // Its own number there leads, who started it is in the card's tooltip (LinkStarterTip)
        return $"#{remote.Id} · {system}";
    }

    /// <summary>The line of a remote card's tooltip that says which chat started it, empty for one that nothing started.</summary>
    private string LinkStarterTip(LinkRemoteCard remote, HexAgentChat chat)
    {
        if (chat.Parent is not { } parent)
            return string.Empty;
        HexLinkRow? starter = LinkPeerOf(remote.DeviceId)?.Fleet.FirstOrDefault(row => row.Id == parent);
        return Said("agents_panel.started_by", ("name", starter is null ? $"#{parent}" : $"#{parent} {starter.Name}"));
    }

    /// <summary>
    /// The badge of the machine a card's agent runs on, put on the card at its top beside the agent system's: another
    /// device's in its colour, this one's quiet, and none while nothing is linked. How much room it takes there.
    /// </summary>
    private float LinkCardBadge(UIComponent root, LinkRemoteCard? remote, float y)
    {
        if (remote is null && !LinkBadgesShown)
            return 0.0f;
        string id = remote?.DeviceId ?? LinkDevice?.Id ?? string.Empty;
        string name = OneLine(remote?.DeviceName ?? LinkDevice?.Name ?? LinkNameWanted, 18);
        Vector4 tint = LinkDeviceTint(id, Skin);
        var badge = root.Add(new Badge
        {
            Text = name, Icon = "vm", Colour = tint, TextScale = 0.1f, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Position = new Vector2(38, y),
            Tooltip = Said(remote is null ? "link.badge_here" : "link.badge_there", ("device", remote?.DeviceName ?? name))
        });
        badge.Opacity = remote is null ? 0.55f : 1.0f;
        float wide = (Skin?.Font.Measure(name, 0.1f, markup: false).X ?? name.Length * 6.0f) + 30.0f;
        return wide + 6.0f;
    }

    /// <summary>A remote chat's menu: open it, stop or restart it there when Steer was granted.</summary>
    private Menu LinkRemoteMenu(LinkRemoteCard remote)
    {
        var menu = new Menu("remote chat");
        menu.Add(Said("agents_panel.chat_open"), () => OpenRemoteChat(remote), icon: "comment");
        bool steer = LinkPeerOf(remote.DeviceId) is { } peer && HexLinkAllowed.Has(peer.Granted, HexLinkRights.Steer);
        menu.Add(Said("link.chat_stop"), () => LinkRemoteStop(remote), icon: "debug-stop").IsEnabled = () => steer;
        menu.Add(Said("link.chat_restart"), () => LinkRemoteRestart(remote), icon: "debug-restart").IsEnabled = () => steer;
        return menu;
    }
}
