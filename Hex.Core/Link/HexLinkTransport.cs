using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace Hex.Core;

/// <summary>
/// Link's door (docs/hex-link-plan.md, 3.2 and 3.5): the listener, the dialling, and the few lines either end says after
/// the TLS handshake before a connection is handed to the protocol as a <see cref="HexLinkConnection"/>.
/// <para>
/// Nothing listens before <see cref="Start"/>. A connection from an address that isn't private is closed before TLS
/// starts. TLS 1.2 or 1.3, both ends present their certificate, and neither is checked against a CA or a host name: the
/// validation callback only notes the SHA-256 of the other's public key, its pin, and lets the handshake finish. Who's
/// let in is decided after it, by the first lines, because on TLS 1.3 a client only learns that the server didn't like
/// its certificate when it next reads, so a refusal made inside the handshake can't be told from a broken network.
/// </para>
/// <para>
/// The lines, each ending with a line feed:
/// <code>
/// dialler:  HEX-LINK 1 link {"name":"Mac","os":"macos","arch":"arm64","version":"3.3.0"}
/// listener: NO unknown                       (and closes) or its own HEX-LINK 1 link {...}
/// dialler:  OK                               or NO busy, and both hand the connection on
/// </code>
/// The listener only ever says <c>NO unknown</c> to a pin it doesn't know, which is all an unpaired device is shown.
/// A pairing starts the same with <c>pair</c> for <c>link</c> and carries on with the exchange in HexLinkTransport.Pairing.
/// </para>
/// <para>
/// One link a device. A second to a device already linked is refused (<c>NO busy</c>) unless the first is dead. When two
/// machines dial each other at the same moment both connections reach the last line together, and the one dialled by the
/// device with the lower id is kept on both sides (<see cref="Claim"/>), so they agree without a further word.
/// </para>
/// </summary>
internal sealed partial class HexLinkTransport : IDisposable
{
    /// <summary>The port Link listens on unless the <c>link_port</c> setting says another.</summary>
    public const int DEFAULT_PORT = 47833;

    // How long the handshake and the first lines may take, from the connection coming in or going out
    private const int HANDSHAKE_MS = 10000;

    // How many connections may be in the handshake at once, more are closed straight away
    private const int MOST_HANDSHAKES = 4;

    // How many links at once
    private const int MOST_LINKS = HexLink.MOST;

    // The longest first line, the device's name is the most of it
    private const int LINE_MOST = 4096;

    // The longest name a device may give itself
    private const int NAME_MOST = 64;

    private readonly HexLinkIdentity identity;
    private readonly HexLinkTrust trust;
    private readonly Lock gate = new();
    private readonly Dictionary<string, Slot> slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Socket> handshaking = [];
    private readonly SemaphoreSlim handshakes = new(MOST_HANDSHAKES, MOST_HANDSHAKES);
    private Socket? listener;
    private CancellationTokenSource? listening;

    // A device's place in the links: a link up, or one the listener said yes to and waits on the dialler's OK for
    private sealed class Slot(Socket socket, bool dialledByMe, string device)
    {
        public Socket Socket { get; } = socket;
        public bool DialledByMe { get; } = dialledByMe;
        public string Device { get; } = device;
        public bool Up { get; set; }
        public HexLinkConnection? Connection { get; set; }
    }

    /// <param name="identity">This device, what it presents.</param>
    /// <param name="trust">The devices it knows, which it lets in and dials, and whose forgetting cuts their link.</param>
    public HexLinkTransport(HexLinkIdentity identity, HexLinkTrust trust)
    {
        this.identity = identity;
        this.trust = trust;
        Admits = address => AllowPublic || HexLinkAddresses.IsPrivate(address);
        trust.Changed += TrustChanged;
    }

    /// <summary>The port it listens on, the one picked when Start was given 0, 0 while it isn't listening.</summary>
    public int Port { get; private set; }

    /// <summary>The address the listener is bound to, if it is running.</summary>
    internal IPAddress? ListeningAddress
    {
        get
        {
            lock (gate)
                return (listener?.LocalEndPoint as IPEndPoint)?.Address;
        }
    }

    /// <summary>Whether it's listening.</summary>
    public bool Listening
    {
        get
        {
            lock (gate)
                return listener is not null;
        }
    }

    /// <summary>Let connections in from addresses that aren't private. Off, and nothing in Hex turns it on.</summary>
    public bool AllowPublic { get; set; }

    /// <summary>
    /// Whether a connection from an address is let as far as TLS, private ones unless <see cref="AllowPublic"/>. A test
    /// changes it to see a refused address refused, there being no public address to connect from in one.
    /// </summary>
    public Func<IPAddress, bool> Admits { get; set; }

    /// <summary>A known device linked in, on a background thread. Whoever takes it owns it and disposes it when done.</summary>
    public Action<HexLinkConnection>? Accepted { get; set; }

    /// <summary>A link came or went, or pairing opened or closed, on whichever thread it happened on.</summary>
    public Action? Changed { get; set; }

    /// <summary>Where refusals, links and pairings are written, none when null.</summary>
    public HexLinkAudit? Audit { get; set; }

    internal HexLinkBulkSupport BulkSupport { get; init; } = HexLinkBulkSupport.Zstandard;

    /// <summary>The links up now.</summary>
    public IReadOnlyList<HexLinkConnection> Links
    {
        get
        {
            lock (gate)
                return [.. slots.Values.Where(slot => slot.Up && slot.Connection is not null).Select(slot => slot.Connection!)];
        }
    }

    /// <summary>The link to a device, null when there's none up.</summary>
    public HexLinkConnection? LinkTo(string id)
    {
        lock (gate)
            return slots.TryGetValue(id, out Slot? slot) && slot.Up ? slot.Connection : null;
    }

    /// <summary>
    /// Starts listening on a port, 0 for any free one (<see cref="Port"/> says which). The default listens on every
    /// address, dual-stack where available. Local tests can bind only to IPv4 loopback.
    /// </summary>
    public void Start(int port, bool loopbackOnly = false)
    {
        lock (gate)
        {
            if (listener is not null)
                return;

            Socket socket = Bind(port, loopbackOnly);
            socket.Listen(16);
            listener = socket;
            Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
            listening = new CancellationTokenSource();
            CancellationToken stop = listening.Token;
            _ = Task.Run(() => AcceptAll(socket, stop));
        }

        Audit?.Write("listening", "", $"port {Port}");
        Changed?.Invoke();
    }

    private static Socket Bind(int port, bool loopbackOnly)
    {
        if (!loopbackOnly && Socket.OSSupportsIPv6)
        {
            var dual = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                dual.DualMode = true;
                dual.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                return dual;
            }
            catch (SocketException e) when (e.SocketErrorCode is not (SocketError.AddressAlreadyInUse or SocketError.AccessDenied))
            {
                dual.Dispose();
            }
        }

        var four = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            four.Bind(new IPEndPoint(loopbackOnly ? IPAddress.Loopback : IPAddress.Any, port));
            return four;
        }
        catch
        {
            four.Dispose();
            throw;
        }
    }

    /// <summary>Stops listening and closes every link and every connection still in its handshake. Pairing closes too.</summary>
    public void Stop()
    {
        List<HexLinkConnection> connections;
        List<Socket> sockets;
        bool was;
        lock (gate)
        {
            was = listener is not null;
            listening?.Cancel();
            listening?.Dispose();
            listening = null;
            listener?.Dispose();
            listener = null;
            Port = 0;
            connections = [.. slots.Values.Select(slot => slot.Connection).OfType<HexLinkConnection>()];
            sockets = [.. handshaking, .. slots.Values.Where(slot => slot.Connection is null).Select(slot => slot.Socket)];
            slots.Clear();
            handshaking.Clear();
        }

        ClosePairing();
        foreach (HexLinkConnection connection in connections)
            connection.Dispose();
        foreach (Socket socket in sockets)
            socket.Dispose();

        if (was)
        {
            Audit?.Write("stopped");
            Changed?.Invoke();
        }
    }

    private async Task AcceptAll(Socket socket, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            Socket taken;
            try
            {
                taken = await socket.AcceptAsync(stop);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // A connection that went before it was taken, the next
                continue;
            }

            _ = Task.Run(() => Admit(taken, stop));
        }
    }

    /// <summary>
    /// Takes a connection that came in through the door: the address, the handshake, the first line, then a link handed
    /// to <see cref="Accepted"/>, a pairing, or a refusal.
    /// </summary>
    private async Task Admit(Socket socket, CancellationToken stop)
    {
        IPAddress from = (socket.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        string address = HexLinkAddresses.Show(from);
        if (!Admits(from))
        {
            Audit?.Write("refused", "", $"{address}, not a private address");
            socket.Dispose();
            return;
        }

        if (!handshakes.Wait(0))
        {
            Audit?.Write("refused", "", $"{address}, too many handshakes at once");
            socket.Dispose();
            return;
        }

        bool handshakeHeld = true;
        bool kept = false;
        Track(socket, true);
        SslStream? ssl = null;
        try
        {
            socket.NoDelay = true;
            ssl = new SslStream(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(HANDSHAKE_MS);

            string? pin = null;
            await ssl.AuthenticateAsServerAsync(ServerOptions(seen => pin = seen), timeout.Token);
            if (pin is null)
                return;

            string? line = await ReadLine(ssl, timeout.Token);
            Hello? hello = ParseHello(line, pin);
            if (hello is null)
            {
                Audit?.Write("refused", pin, $"{address}, said something that isn't Link's first line");
                return;
            }

            if (hello.Version != 1)
            {
                await Say(ssl, "NO version", timeout.Token);
                Audit?.Write("refused", pin, $"{address}, protocol version {hello.Version}");
                return;
            }

            if (hello.Mode == "invite")
            {
                kept = await HostInvite(ssl, socket, hello.Device, address, stop, timeout.Token, NegotiateBulk(hello.Bulk), () =>
                {
                    handshakes.Release();
                    handshakeHeld = false;
                });
                return;
            }

            if (hello.Mode == "pair")
            {
                // The person takes as long as they take, this connection stops counting against the handshakes
                handshakes.Release();
                handshakeHeld = false;
                kept = await HostPairing(ssl, socket, hello.Device, address, stop, NegotiateBulk(hello.Bulk));
                return;
            }

            if (hello.Mode != "link" || trust.Find(pin) is null)
            {
                await Say(ssl, "NO unknown", timeout.Token);
                Audit?.Write("refused", pin, $"{address}, unknown device \"{hello.Device.Name}\"");
                return;
            }

            kept = await ListenerSettles(ssl, socket, hello.Device, address, port: 0, timeout.Token, bulk: NegotiateBulk(hello.Bulk));
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException or ObjectDisposedException or SocketException or InvalidOperationException)
        {
            Audit?.Write("refused", "", $"{address}, {Describe(e)}");
        }
        finally
        {
            if (handshakeHeld)
                handshakes.Release();
            Track(socket, false);
            if (!kept)
            {
                if (ssl is not null)
                    await ssl.DisposeAsync();
                else
                    socket.Dispose();
            }
        }
    }

    /// <summary>
    /// The listener's end of the last two lines: a place for the device, its own first line (or <c>NO busy</c>), then the
    /// dialler's OK, after which the link is up and handed to <see cref="Accepted"/>. Whether the connection was kept.
    /// </summary>
    private async Task<bool> ListenerSettles(SslStream ssl, Socket socket, HexLinkDevice device, string address, int port, CancellationToken cancel, string yes = "",
        HexLinkBulkSupport bulk = HexLinkBulkSupport.None)
    {
        Slot? slot = Claim(device.Id, socket, dialledByMe: false, out string refusal);
        if (slot is null)
        {
            await Say(ssl, "NO " + refusal, cancel);
            Audit?.Write("refused", device.Id, $"{address}, {refusal}");
            return false;
        }

        try
        {
            await Say(ssl, yes.Length > 0 ? yes : HelloLine("link"), cancel);
            string? answer = await ReadLine(ssl, cancel);
            if (answer != "OK" || !Raise(slot))
            {
                Release(slot);
                Audit?.Write("refused", device.Id, $"{address}, the other end said {answer ?? "nothing"}");
                return false;
            }
        }
        catch
        {
            Release(slot);
            throw;
        }

        HexLinkConnection connection = Up(slot, ssl, device, address, port, dialled: false, bulk);
        try
        {
            Accepted?.Invoke(connection);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Hex] taking a link in failed, {e.Message}");
        }

        return true;
    }

    /// <summary>
    /// A place for a device's link, or null with the word to refuse it with. A device with a link up keeps it (busy)
    /// unless that link's socket is closed; more than <see cref="MOST_LINKS"/> are full. Two connections between the same
    /// two devices at once, one each way, keep the one dialled by the lower device id, which both sides work out alike.
    /// The listener's place is taken before it says yes and only raised once the dialler said OK; the dialler's is
    /// taken and raised at once, just before it says OK.
    /// </summary>
    private Slot? Claim(string device, Socket socket, bool dialledByMe, out string refusal)
    {
        HexLinkConnection? dead = null;
        Socket? superseded = null;
        try
        {
            lock (gate)
            {
                if (slots.TryGetValue(device, out Slot? held))
                {
                    if (held.Up && Alive(held.Socket))
                    {
                        refusal = "busy";
                        return null;
                    }

                    if (!held.Up)
                    {
                        // Waiting on its dialler's OK: a dial of ours beats it when ours is the lower id's, anything else waits its turn
                        bool oursWins = dialledByMe && string.CompareOrdinal(identity.Id, device) < 0;
                        if (!oursWins)
                        {
                            refusal = "busy";
                            return null;
                        }

                        superseded = held.Socket;
                    }
                    else
                    {
                        dead = held.Connection;
                    }

                    slots.Remove(device);
                }

                if (slots.Count >= MOST_LINKS)
                {
                    refusal = "full";
                    return null;
                }

                var slot = new Slot(socket, dialledByMe, device) { Up = dialledByMe };
                slots[device] = slot;
                refusal = "";
                return slot;
            }
        }
        finally
        {
            dead?.Dispose();
            superseded?.Dispose();
        }
    }

    // The listener's place made a link, false when it was taken by a winning dial meanwhile
    private bool Raise(Slot slot)
    {
        lock (gate)
        {
            if (!slots.TryGetValue(slot.Device, out Slot? held) || held != slot)
                return false;
            slot.Up = true;
            return true;
        }
    }

    private void Release(Slot slot)
    {
        lock (gate)
        {
            if (slots.TryGetValue(slot.Device, out Slot? held) && held == slot)
                slots.Remove(slot.Device);
        }
    }

    // The connection for a place that's up, which gives its place back when it's disposed
    private HexLinkConnection Up(Slot slot, SslStream ssl, HexLinkDevice said, string address, int port, bool dialled, HexLinkBulkSupport bulk)
    {
        string id = said.Id;
        try { trust.Seen(id, said, address, port); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Audit?.Write("link metadata", id, $"Can't save the address for {said.Name}: {error.Message}");
        }
        HexLinkDevice device = trust.Find(id)?.Device ?? said;
        HexLinkConnection? connection = null;
        connection = new HexLinkConnection(ssl, device, address, dialled, () => trust.Find(id)?.Rights ?? HexLinkRights.None, () =>
        {
            bool gone = false;
            lock (gate)
            {
                if (slots.TryGetValue(id, out Slot? held) && held == slot)
                {
                    slots.Remove(id);
                    gone = true;
                }
            }

            if (gone)
            {
                Audit?.Write("link down", id, address);
                Changed?.Invoke();
            }
        }, bulk, Audit);

        bool cut;
        lock (gate)
        {
            slot.Connection = connection;
            cut = !slots.TryGetValue(id, out Slot? held) || held != slot;
        }

        // Stopped or forgotten while it was coming up
        if (cut)
            connection.Dispose();
        else
        {
            Audit?.Write("link up", id, $"{address}, {(dialled ? "dialled" : "came in")}, {ssl.SslProtocol}");
            Changed?.Invoke();
        }

        return connection;
    }

    // Whether a socket's other end is still there, as far as can be told without reading: readable with nothing to read is closed
    private static bool Alive(Socket socket)
    {
        try
        {
            return !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    private void Track(Socket socket, bool add)
    {
        lock (gate)
        {
            if (add)
                handshaking.Add(socket);
            else
                handshaking.Remove(socket);
        }
    }

    // A device forgotten is cut the moment it is, whatever it was doing
    private void TrustChanged(string id)
    {
        if (trust.Find(id) is not null)
            return;

        Slot? slot;
        lock (gate)
        {
            if (slots.Remove(id, out slot) is false)
                return;
        }

        Audit?.Write("forgotten", id, "its link cut");
        if (slot.Connection is { } connection)
            connection.Dispose();
        else
            slot.Socket.Dispose();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        trust.Changed -= TrustChanged;
        Stop();
        handshakes.Dispose();
    }
}
