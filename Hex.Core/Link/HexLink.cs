using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// The links of a running Hex. A connection the transport hands over (dialled or taken in) becomes a session with both
/// halves over it, the host answering what the device asks here and the peer asking what this machine wants there,
/// and says hello. What the editor tells it (the hub moved, a chat moved, a device's rights changed) goes to every
/// link; where a device's person is goes on to the other devices that may see here. At most <see cref="MOST"/> at once.
/// </summary>
internal sealed class HexLink(IHexLinkServices services) : IDisposable
{
    /// <summary>The most links at once.</summary>
    public const int MOST = 8;

    // A link up, its session and its two halves
    private sealed record Joined(HexLinkSession Session, HexLinkHost Host, HexLinkPeer Peer);

    private readonly List<Joined> links = [];

    /// <summary>How often a link is pinged, 20 seconds unless a test wants it sooner.</summary>
    public TimeSpan PingEvery { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How often a watched file is looked at, a second unless a test wants it sooner.</summary>
    public TimeSpan WatchEvery { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>A link came or went, or something shown of one moved. On whatever thread it happened on.</summary>
    public Action? Changed { get; set; }

    /// <summary>A link went, with the device and why, for the audit log and a notice.</summary>
    public Action<HexLinkDevice, string>? Closed { get; set; }

    /// <summary>The other machines linked now, those that said hello.</summary>
    public IReadOnlyList<HexLinkPeer> Peers
    {
        get
        {
            lock (links)
                return [.. links.Select(link => link.Peer)];
        }
    }

    /// <summary>The linked device of an id, null when it isn't linked.</summary>
    public HexLinkPeer? PeerOf(string deviceId)
    {
        lock (links)
            return links.FirstOrDefault(link => link.Peer.Device.Id == deviceId)?.Peer;
    }

    /// <summary>
    /// A connection taken on: the session made and started, hello said both ways. The peer once it's up; throws (and
    /// closes the connection) when there are <see cref="MOST"/> already, the device is linked already, or hello failed.
    /// </summary>
    public async Task<HexLinkPeer> Add(HexLinkConnection connection, CancellationToken cancel = default)
    {
        var session = new HexLinkSession(connection) { PingEvery = PingEvery };
        var host = new HexLinkHost(session, services) { WatchEvery = WatchEvery };
        var peer = new HexLinkPeer(session, services.Device);
        var joined = new Joined(session, host, peer);
        lock (links)
        {
            string? refused = links.Count >= MOST ? $"this Hex has {MOST} links already"
                : links.Any(link => link.Session.Connection.Device.Id == connection.Device.Id) ? "it's linked already" : null;
            if (refused is not null)
            {
                connection.Dispose();
                throw new HexLinkException(HexLinkErrors.BUSY, $"{connection.Device.Name} wasn't linked, {refused}.");
            }

            links.Add(joined);
        }

        session.Requested = host.Handle;
        session.Told = (method, parameters) =>
        {
            if (!host.Told(method, parameters))
                peer.Told(method, parameters);
        };
        host.PresenceSaid = Said;
        peer.Changed = () => Changed?.Invoke();
        session.Closed += (_, why) => Gone(joined, why);
        session.Start();

        try
        {
            await peer.Hello(cancel);
        }
        catch (Exception)
        {
            session.Close("hello failed");
            throw;
        }

        Changed?.Invoke();
        return peer;
    }

    // Where a device's person is, heard by its link's host half: its peer told, and the other devices that may see here
    private void Said(HexLinkHost from, HexLinkPresence? presence)
    {
        Joined[] all;
        lock (links)
            all = [.. links];
        foreach (Joined link in all)
        {
            if (link.Host == from)
                link.Peer.Heard(presence);
            else
                link.Host.PassOn(from.Device, presence);
        }
    }

    private void Gone(Joined joined, string why)
    {
        lock (links)
            links.Remove(joined);
        joined.Host.Dispose();
        joined.Peer.Dispose();
        Closed?.Invoke(joined.Session.Connection.Device, why);
        Changed?.Invoke();
    }

    /// <summary>A device's link cut (forgotten, or the person said so), false when it wasn't linked.</summary>
    public bool Remove(string deviceId, string why = "it was cut here")
    {
        Joined? found;
        lock (links)
            found = links.FirstOrDefault(link => link.Session.Connection.Device.Id == deviceId);
        found?.Session.Close(why);
        return found is not null;
    }

    private Joined[] All()
    {
        lock (links)
            return [.. links];
    }

    /// <summary>The hub's snapshot moved, the fleet sent on to the devices watching it.</summary>
    public void FleetMoved()
    {
        foreach (Joined link in All())
            link.Host.FleetMoved();
    }

    /// <summary>A chat moved, what changed sent on to the devices that have it open.</summary>
    public void ChatMoved(int id)
    {
        foreach (Joined link in All())
            link.Host.ChatMoved(id);
    }

    /// <summary>What a device may do here changed, told to it; with nothing left the link stays, it just can't do anything.</summary>
    public void RightsMoved(string deviceId)
    {
        foreach (Joined link in All())
        {
            if (link.Session.Connection.Device.Id == deviceId)
                link.Host.RightsMoved();
        }
    }

    /// <summary>Where this machine's person is, told to every linked device (each at most twice a second).</summary>
    public void SetPresence(HexLinkPresence? presence)
    {
        foreach (Joined link in All())
            link.Peer.SetPresence(presence);
    }

    /// <summary>The mirrored chats of every link patched, on the editor's thread every tick. True when one moved.</summary>
    public bool Pump()
    {
        bool moved = false;
        foreach (Joined link in All())
            moved |= link.Peer.Pump();
        return moved;
    }

    /// <summary>Every link closed.</summary>
    public void Dispose()
    {
        foreach (Joined link in All())
            link.Session.Close("Link was turned off");
    }
}
