using System.Numerics;
using System.Runtime.CompilerServices;

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
    private Dictionary<(string Device, int Id), HexAgentChat> linkStandIns { get => LazyInitializer.EnsureInitialized(ref field, static () => []); }
    private Dictionary<HexAgentChat, LinkRemoteCard> linkRemoteOf { get => LazyInitializer.EnsureInitialized(ref field, static () => []); }
    private HashSet<(string Device, int Id)> linkStartedHere { get => LazyInitializer.EnsureInitialized(ref field, static () => []); }
    private Dictionary<string, (long Version, string Name, IReadOnlyList<HexLinkRow> Rows)> linkStandInsAt { get => LazyInitializer.EnsureInitialized(ref field, static () => new(StringComparer.OrdinalIgnoreCase)); }

    /// <summary>Whether the cards carry the badge of the machine they run on, while any device is linked.</summary>
    private bool LinkBadgesShown => link is not null && LinkPeers.Count > 0;
    /// <summary>Tracks the linked fleets and the open remote chat without building a list of devices.</summary>
    private long LinkFleetKey()
    {
        IReadOnlyList<HexLinkPeer> peers = LinkPeers;
        if (peers.Count == 0 && linkChatsLast.Count == 0 && linkChatFakes is not { Count: > 0 })
            return 0;
        long key = (Skin is { } skin ? RuntimeHelpers.GetHashCode(skin) : 23) + (linkRemoteOpen is { } open ? string.GetHashCode(open.DeviceId, StringComparison.Ordinal) * 31L + open.Id : 0);
        for (int i = 0; i < peers.Count; i++)
        {
            HexLinkPeer peer = peers[i];
            string id = peer.Device.Id;
            key = key * 31 + string.GetHashCode(id, StringComparison.Ordinal);
            key = key * 31 + string.GetHashCode(LinkNameOf(id, peer.Device.Name), StringComparison.Ordinal);
            key = key * 31 + (int)peer.Granted;
            key = key * 31 + (peer.IsClosed ? 1 : 0);
            key = key * 31 + peer.FleetVersion;
            IReadOnlyList<HexLinkRow> fleet = peer.Fleet;
            key = key * 31 + fleet.Count;
            key = key * 31 + RuntimeHelpers.GetHashCode(fleet);
            key = key * 31 + string.GetHashCode(peer.Session.Connection.Address, StringComparison.Ordinal);
            key = key * 31 + (linkTrust?.Find(id)?.PreferredRoute?.GetHashCode() ?? 0);
            if (ShowsLinkChats(peer))
                linkChatsSeenAt[id] = DateTime.UtcNow;
        }
        foreach (string id in linkChatsLast.Keys)
        {
            if (FindLinkChatPeer(peers, id) is null && linkTrust?.Find(id) is { } known)
            {
                key = key * 31 + string.GetHashCode(id, StringComparison.Ordinal);
                key = key * 31 + string.GetHashCode(known.Shown, StringComparison.Ordinal);
            }
        }
        if (linkChatFakes is { } fakes)
        {
            foreach (LinkChatDevice fake in fakes)
            {
                key = key * 31 + string.GetHashCode(fake.Id, StringComparison.Ordinal);
                key = key * 31 + string.GetHashCode(fake.Name, StringComparison.Ordinal);
                key = key * 31 + fake.Version;
                key = key * 31 + fake.Rows.Count;
                key = key * 31 + (int)fake.Granted;
                key = key * 31 + (fake.Online ? 1 : 0);
            }
        }
        key = key * 31 + linkFailedUnseen.Count;
        return key * 31 + linkChatFoldsVersion;
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

    private void PrepareLinkStandIns(LinkChatDevice device)
    {
        // Refresh the stand-ins when a new fleet arrives or the device is shown by another name.
        var now = (device.Version, device.Name, device.Rows);
        if (linkStandInsAt.TryGetValue(device.Id, out var was) && was == now)
            return;
        RemoveLinkStandIns(device.Id);
        linkStandInsAt[device.Id] = now;
    }

    private void RemoveLinkStandIns(string id)
    {
        foreach (var key in linkStandIns.Keys.Where(key => key.Device == id).ToArray())
        {
            HexAgentChat old = linkStandIns[key];
            linkStandIns.Remove(key);
            linkStartedHere.Remove(key);
            if (old != agentChat)
                linkRemoteOf.Remove(old);
        }
        linkStandInsAt.Remove(id);
    }

    /// <summary>
    /// The stand-in chat a card of a device's fleet is drawn from, made again when the device's fleet moved: its name,
    /// agent and model, who started it, how it's doing (running, stopped, failed, done with its last reply) and what it
    /// cost, as the row says. Only ever drawn, never in the hub.
    /// </summary>
    private HexAgentChat LinkStandIn(string device, string name, HexLinkRow row)
    {
        if (row.StartedByYou)
            linkStartedHere.Add((device, row.Id));
        else
            linkStartedHere.Remove((device, row.Id));

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
        linkRemoteOf[chat] = new LinkRemoteCard(device, name, row.Id);
        return chat;
    }

    /// <summary>The remote chat a chat is (a stand-in or a mirror), null for one of this machine's.</summary>
    private LinkRemoteCard? LinkRemoteOf(HexAgentChat? chat) =>
        chat is not null && linkRemoteOf.Count > 0 && linkRemoteOf.TryGetValue(chat, out LinkRemoteCard? remote) ? remote : null;

    /// <summary>What started a remote chat as its card says it, the starter's number and name there, and its agent system.</summary>
    private string LinkStarterOf(LinkRemoteCard remote, string system, string startedHere)
    {
        // Its own number there leads, who started it is in the card's tooltip (LinkStarterTip)
        string line = $"#{remote.Id} · {system}";
        return startedHere.Length > 0 ? $"{line} · {startedHere}" : line;
    }

    /// <summary>The line of a remote card's tooltip that says which chat started it, empty for one that nothing started.</summary>
    private string LinkStarterTip(LinkRemoteCard remote, HexAgentChat chat)
    {
        if (chat.Parent is not { } parent)
            return string.Empty;
        IReadOnlyList<HexLinkRow>? rows = LinkChatRows(remote.DeviceId);
        if (rows is null && linkChatsLast.TryGetValue(remote.DeviceId, out var last))
            rows = last.Device.Rows;
        HexLinkRow? starter = rows?.FirstOrDefault(row => row.Id == parent);
        return Said("agents_panel.started_by", ("name", starter is null ? $"#{parent}" : $"#{parent} {starter.Name}"));
    }

    /// <summary>
    /// The badge of the machine a card's agent runs on, put on the card at its top beside the agent system's: another
    /// device's in its colour, this one's quiet, and none while nothing is linked. How much room it takes there.
    /// </summary>
    private float LinkCardBadge(UIComponent root, LinkRemoteCard? remote, float y, out Badge? badge)
    {
        badge = null;
        if (remote is null && !LinkBadgesShown)
            return 0.0f;
        string id = remote?.DeviceId ?? LinkDevice?.Id ?? string.Empty;
        string name = OneLine(remote?.DeviceName ?? LinkDevice?.Name ?? LinkNameWanted, 18);
        Vector4 tint = LinkDeviceTint(id, Skin);
        if (remote is not null)
            tint = LinkChatReadableInk(tint, chip: true);
        badge = root.Add(new Badge
        {
            Text = name, Icon = "vm", Colour = tint, TextScale = 0.1f, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Position = new Vector2(38, y),
            Tooltip = Said(remote is null ? "link.badge_here" : "link.badge_there", ("device", remote?.DeviceName ?? name))
        });
        badge.Opacity = remote is null ? 0.55f : 1.0f;
        float wide = (Skin?.Font.Measure(name, 0.1f, markup: false).X ?? name.Length * 6.0f) + 30.0f;
        return wide + 6.0f;
    }

}
