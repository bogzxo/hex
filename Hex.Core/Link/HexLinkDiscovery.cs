using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Hex.Core;

/// <summary>A Hex found on the LAN by mDNS.</summary>
/// <param name="Name">What it calls itself.</param>
/// <param name="Id8">The first 8 characters of its device id, enough to tell it in the list of known devices, never enough to trust it by.</param>
/// <param name="Addresses">Where it said it is (its A records), or where its answer came from when it said nothing.</param>
/// <param name="Port">The port it listens on.</param>
/// <param name="Seen">When it was last heard, UTC.</param>
internal sealed record HexLinkNearby(string Name, string Id8, IReadOnlyList<IPAddress> Addresses, int Port, DateTime Seen);

/// <summary>What a Hex says of itself on the LAN, the records the responder answers with.</summary>
/// <param name="Instance">The service instance's label, the device's name with the start of its id so two of the same name differ.</param>
/// <param name="Host">The host label its A records are under.</param>
internal sealed record HexLinkAnnouncement(string Instance, string Host, ushort Port, string Id8, IReadOnlyList<IPAddress> Addresses)
{
    /// <summary>The announcement for a device, its labels made safe (no dots, at most 63 bytes).</summary>
    public static HexLinkAnnouncement Of(string name, string id, int port, IReadOnlyList<IPAddress> addresses)
    {
        string id8 = id.Length >= 8 ? id[..8] : id;
        string label = new([.. name.Where(c => c != '.' && !char.IsControl(c))]);
        if (label.Length > 40)
            label = label[..40];
        return new HexLinkAnnouncement($"{label.Trim()} ({id8})", $"hex-{id8.ToLowerInvariant()}", (ushort)port, id8, addresses);
    }
}

/// <summary>
/// Hex's own small mDNS / DNS-SD (docs/hex-link-plan.md, 3.5 and 7): it answers for <c>_hex-link._tcp.local</c> while
/// Link is discoverable and finds the other Hexes on the LAN that do. It's a socket on UDP 5353 with address reuse,
/// beside whatever responder the system runs (mDNSResponder, Avahi, Windows' own), joined to 224.0.0.251 on every
/// interface that does multicast. IPv4 only: every LAN this is for has it, and the address typed in is there for the rest.
/// <para>
/// What's said is the device's name, the first 8 characters of its id in the TXT record (<c>id=</c>, with <c>v=1</c>)
/// and the port, nothing of the workspace. Discovery is a convenience: a socket that can't be bound or joined makes it
/// off, <see cref="Off"/> says why, and an address typed in still works.
/// </para>
/// </summary>
internal sealed class HexLinkDiscovery : IDisposable
{
    /// <summary>The DNS-SD service type.</summary>
    public const string SERVICE = "_hex-link._tcp.local";

    // DNS-SD's list of the service types there are
    private const string SERVICES = "_services._dns-sd._udp.local";

    private const int MDNS_PORT = 5353;

    // How long a PTR and TXT are good for, and an SRV and A, as RFC 6762 suggests
    private const uint LONG_TTL = 4500;
    private const uint SHORT_TTL = 120;

    // How often a browser asks again once it's settled, and how many it keeps
    private const int BROWSE_EVERY_MS = 60000;
    private const int MOST_FOUND = 64;

    private static readonly IPAddress GROUP = IPAddress.Parse("224.0.0.251");

    private readonly Lock gate = new();
    private readonly Dictionary<string, Heard> heard = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (List<IPAddress> Addresses, DateTime Until)> hosts = new(StringComparer.OrdinalIgnoreCase);
    private Socket? socket;
    private CancellationTokenSource? receiving;
    private List<IPAddress> interfaces = [];
    private HexLinkAnnouncement? announcing;
    private Timer? browsing;
    private Timer? sweeping;
    private int browseRound;
    private bool disposed;

    // What's been heard of an instance, each part with when it runs out
    private sealed class Heard
    {
        public DateTime PtrUntil;
        public DateTime SrvUntil;
        public DateTime TxtUntil;
        public string Host = "";
        public int Port;
        public string Id8 = "";
        public IPAddress? From;
        public DateTime Seen;
    }

    /// <summary>Why discovery is off, null while it's on or hasn't been started.</summary>
    public string? Off { get; private set; }

    /// <summary>What was found changed, on the receiving thread.</summary>
    public Action? Changed { get; set; }

    /// <summary>The Hexes found and not yet run out, by name, this one left out.</summary>
    public IReadOnlyList<HexLinkNearby> Found
    {
        get
        {
            DateTime now = DateTime.UtcNow;
            lock (gate)
            {
                var found = new List<HexLinkNearby>();
                foreach ((string instance, Heard one) in heard)
                {
                    if (one.PtrUntil < now || one.SrvUntil < now || one.TxtUntil < now || one.Port == 0)
                        continue;
                    if (announcing is not null && string.Equals(one.Id8, announcing.Id8, StringComparison.OrdinalIgnoreCase))
                        continue;

                    List<IPAddress> addresses = hosts.TryGetValue(one.Host, out var host) && host.Until >= now ? host.Addresses
                        : one.From is not null ? [one.From] : [];
                    found.Add(new HexLinkNearby(NameOf(instance, one.Id8), one.Id8, addresses, one.Port, one.Seen));
                }

                return [.. found.OrderBy(nearby => nearby.Name, StringComparer.OrdinalIgnoreCase)];
            }
        }
    }

    /// <summary>
    /// Answers for this device until <see cref="StopAnnouncing"/>, and says so on the LAN at once (twice, a second apart,
    /// as RFC 6762 asks). False when discovery is off.
    /// </summary>
    public bool Announce(string name, string id, int port)
    {
        if (!Open())
            return false;

        HexLinkAnnouncement announcement;
        lock (gate)
        {
            announcement = HexLinkAnnouncement.Of(name, id, port, [.. interfaces.Where(HexLinkAddresses.IsPrivate)]);
            announcing = announcement;
        }

        SendAll(Unsolicited(announcement, LONG_TTL, SHORT_TTL));
        _ = Task.Delay(1000).ContinueWith(_ =>
        {
            lock (gate)
                if (announcing != announcement || disposed)
                    return;
            SendAll(Unsolicited(announcement, LONG_TTL, SHORT_TTL));
        }, TaskScheduler.Default);
        return true;
    }

    /// <summary>Stops answering for this device and says goodbye, its records sent again with no time to live.</summary>
    public void StopAnnouncing()
    {
        HexLinkAnnouncement? was;
        lock (gate)
        {
            was = announcing;
            announcing = null;
        }

        if (was is not null)
            SendAll(Unsolicited(was, 0, 0));
    }

    /// <summary>Starts looking for Hexes on the LAN: asks now, after a second, after three, then every minute. False when discovery is off.</summary>
    public bool Browse()
    {
        if (!Open())
            return false;

        lock (gate)
        {
            if (browsing is not null)
                return true;
            browseRound = 0;
            browsing = new Timer(_ => Ask(), null, 0, Timeout.Infinite);
            sweeping = new Timer(_ => Sweep(), null, 5000, 5000);
        }

        return true;
    }

    private void Ask()
    {
        Timer? timer;
        int round;
        lock (gate)
        {
            if (disposed || browsing is null)
                return;
            timer = browsing;
            round = browseRound++;
        }

        SendAll(HexLinkDns.Encode(new HexLinkDnsMessage(0, false, [new HexLinkDnsQuestion(SERVICE, HexLinkDns.PTR)], [], [])));
        int next = round switch { 0 => 1000, 1 => 2000, _ => BROWSE_EVERY_MS };
        try
        {
            timer.Change(next, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Sweep()
    {
        DateTime now = DateTime.UtcNow;
        bool changed = false;
        lock (gate)
        {
            foreach (string instance in heard.Where(pair => pair.Value.PtrUntil < now && pair.Value.SrvUntil < now).Select(pair => pair.Key).ToList())
                changed |= heard.Remove(instance);
            foreach (string host in hosts.Where(pair => pair.Value.Until < now).Select(pair => pair.Key).ToList())
                hosts.Remove(host);
        }

        if (changed)
            Changed?.Invoke();
    }

    // The socket on 5353 joined to the group, made the first time it's wanted, false (and Off says why) when it can't be
    private bool Open()
    {
        lock (gate)
        {
            if (disposed)
                return false;
            if (socket is not null)
                return true;
            if (Off is not null)
                return false;

            var made = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                // On Unix .NET sets SO_REUSEPORT with this too, which is what lets it sit beside mDNSResponder and Avahi
                made.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                made.Bind(new IPEndPoint(IPAddress.Any, MDNS_PORT));
                made.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                made.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);

                interfaces = InterfaceAddresses();
                int joined = 0;
                foreach (IPAddress local in interfaces)
                {
                    try
                    {
                        made.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(GROUP, local));
                        joined++;
                    }
                    catch (SocketException)
                    {
                        // An interface that won't join, the others still do
                    }
                }

                if (joined == 0)
                {
                    made.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(GROUP));
                }
            }
            catch (SocketException e)
            {
                made.Dispose();
                Off = $"mDNS couldn't use UDP port {MDNS_PORT}, {e.Message}";
                Console.WriteLine($"[Hex] Link's discovery is off, {Off}");
                return false;
            }

            socket = made;
            receiving = new CancellationTokenSource();
            CancellationToken stop = receiving.Token;
            _ = Task.Run(() => Receive(made, stop));
            return true;
        }
    }

    // The IPv4 addresses of the interfaces that are up and do multicast, loopback left out
    private static List<IPAddress> InterfaceAddresses()
    {
        var found = new List<IPAddress>();
        try
        {
            foreach (NetworkInterface face in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (face.OperationalStatus != OperationalStatus.Up || face.NetworkInterfaceType == NetworkInterfaceType.Loopback || !face.SupportsMulticast)
                    continue;
                foreach (UnicastIPAddressInformation address in face.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                        found.Add(address.Address);
                }
            }
        }
        catch (NetworkInformationException)
        {
        }

        return found;
    }

    // Asynchronous on purpose: on macOS a socket can't be closed while a thread is blocked in a synchronous receive on it,
    // Dispose spins calling shutdown until the receive returns, which a quiet LAN never makes it do. An asynchronous
    // receive is cancelled and the socket closes at once, on every OS.
    private async Task Receive(Socket made, CancellationToken stop)
    {
        var buffer = new byte[9000];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!stop.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await made.ReceiveFromAsync(buffer, SocketFlags.None, any, stop);
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                continue;
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                return;
            }

            if (HexLinkDns.Decode(buffer.AsSpan(0, received.ReceivedBytes)) is not { } message || received.RemoteEndPoint is not IPEndPoint sender)
                continue;

            if (!message.Response)
                Answer(message, sender);
            else
                Hear(message, sender.Address);
        }
    }

    // A question for our names answered, to the group, or to the asker alone when it asked from another port than 5353
    private void Answer(HexLinkDnsMessage query, IPEndPoint asker)
    {
        HexLinkAnnouncement? me;
        lock (gate)
            me = announcing;
        if (me is null || AnswerTo(query, me) is not { } answer)
            return;

        if (asker.Port != MDNS_PORT)
        {
            Send(HexLinkDns.Encode(answer with { Id = query.Id, Questions = query.Questions }), asker);
            return;
        }

        SendAll(HexLinkDns.Encode(answer));
    }

    /// <summary>
    /// The response to a query for this device's names, null when it asked nothing of them. The service's PTR brings
    /// the SRV, TXT and A records along as additionals, so one round trip finds a device whole.
    /// </summary>
    public static HexLinkDnsMessage? AnswerTo(HexLinkDnsMessage query, HexLinkAnnouncement me)
    {
        string instance = $"{me.Instance}.{SERVICE}", host = $"{me.Host}.local";
        HexLinkDnsRecord ptr = new(SERVICE, HexLinkDns.PTR, LONG_TTL) { Target = instance };
        HexLinkDnsRecord srv = new(instance, HexLinkDns.SRV, SHORT_TTL, Flush: true) { Port = me.Port, Target = host };
        HexLinkDnsRecord txt = new(instance, HexLinkDns.TXT, LONG_TTL, Flush: true) { Texts = [$"id={me.Id8}", "v=1"] };
        List<HexLinkDnsRecord> addresses = [.. me.Addresses.Where(HexLinkDns.IsFour).Select(address => new HexLinkDnsRecord(host, HexLinkDns.A, SHORT_TTL, Flush: true) { Address = address })];

        var answers = new List<HexLinkDnsRecord>();
        var additionals = new List<HexLinkDnsRecord>();
        foreach (HexLinkDnsQuestion question in query.Questions)
        {
            bool any = question.Type == HexLinkDns.ANY;
            if (HexLinkDns.Same(question.Name, SERVICE) && (any || question.Type == HexLinkDns.PTR))
            {
                answers.Add(ptr);
                additionals.AddRange([srv, txt, .. addresses]);
            }
            else if (HexLinkDns.Same(question.Name, SERVICES) && (any || question.Type == HexLinkDns.PTR))
            {
                answers.Add(new HexLinkDnsRecord(SERVICES, HexLinkDns.PTR, LONG_TTL) { Target = SERVICE });
            }
            else if (HexLinkDns.Same(question.Name, instance))
            {
                if (any || question.Type == HexLinkDns.SRV)
                {
                    answers.Add(srv);
                    additionals.AddRange(addresses);
                }

                if (any || question.Type == HexLinkDns.TXT)
                    answers.Add(txt);
            }
            else if (HexLinkDns.Same(question.Name, host) && (any || question.Type == HexLinkDns.A))
            {
                answers.AddRange(addresses);
            }
        }

        if (answers.Count == 0)
            return null;
        List<HexLinkDnsRecord> answered = [.. answers.Distinct()];
        return new HexLinkDnsMessage(0, true, [], answered, [.. additionals.Distinct().Where(record => !answered.Contains(record))]);
    }

    // Every record of the announcement, as said unasked, or with no time to live as a goodbye
    private static byte[] Unsolicited(HexLinkAnnouncement me, uint longTtl, uint shortTtl)
    {
        HexLinkDnsMessage all = AnswerTo(new HexLinkDnsMessage(0, false, [new HexLinkDnsQuestion(SERVICE, HexLinkDns.PTR)], [], []), me)!;
        HexLinkDnsRecord[] records = [.. all.Answers, .. all.Additionals];
        return HexLinkDns.Encode(new HexLinkDnsMessage(0, true, [], [.. records.Select(record => record with { Ttl = record.Ttl == LONG_TTL ? longTtl : shortTtl })], []));
    }

    /// <summary>Takes in what a response says of Hexes, from which address it came.</summary>
    public void Hear(HexLinkDnsMessage response, IPAddress from)
    {
        DateTime now = DateTime.UtcNow;
        bool changed = false;
        lock (gate)
        {
            foreach (HexLinkDnsRecord record in response.Answers.Concat(response.Additionals))
            {
                DateTime until = now.AddSeconds(record.Ttl);
                if (record.Type == HexLinkDns.PTR && HexLinkDns.Same(record.Name, SERVICE))
                {
                    if (Instance(record.Target) is { } one)
                    {
                        one.PtrUntil = until;
                        one.From = from;
                        one.Seen = now;
                        changed = true;
                    }
                }
                else if (record.Type == HexLinkDns.SRV && Instance(record.Name) is { } srv)
                {
                    srv.SrvUntil = until;
                    srv.Port = record.Port;
                    srv.Host = record.Target;
                    srv.Seen = now;
                    changed = true;
                }
                else if (record.Type == HexLinkDns.TXT && Instance(record.Name) is { } txt)
                {
                    txt.TxtUntil = until;
                    txt.Id8 = record.Texts.FirstOrDefault(text => text.StartsWith("id=", StringComparison.Ordinal))?[3..] ?? "";
                    changed = true;
                }
                else if (record.Type == HexLinkDns.A && record.Address is not null)
                {
                    if (record.Ttl == 0)
                    {
                        changed |= hosts.Remove(record.Name);
                        continue;
                    }

                    if (!hosts.TryGetValue(record.Name, out var host) || host.Until < now)
                        host = ([], until);
                    if (!host.Addresses.Contains(record.Address))
                        host.Addresses.Add(record.Address);
                    hosts[record.Name] = (host.Addresses, until > host.Until ? until : host.Until);
                    changed = true;
                }
            }
        }

        if (changed)
            Changed?.Invoke();
    }

    // The instance a name is of our service, made when it's new, null when it's another service's or there are too many
    private Heard? Instance(string name)
    {
        if (!name.EndsWith("." + SERVICE, StringComparison.OrdinalIgnoreCase))
            return null;
        string instance = name[..^(SERVICE.Length + 1)];
        if (heard.TryGetValue(instance, out Heard? one))
            return one;
        if (heard.Count >= MOST_FOUND)
            return null;
        heard[instance] = one = new Heard();
        return one;
    }

    // The name as the person gave it, the id the announcement added taken back off
    private static string NameOf(string instance, string id8)
    {
        string added = $" ({id8})";
        return id8.Length > 0 && instance.EndsWith(added, StringComparison.OrdinalIgnoreCase) ? instance[..^added.Length] : instance;
    }

    // A packet to the group out of every interface, as multicast goes out of one interface a send
    private void SendAll(byte[] packet)
    {
        Socket? made;
        List<IPAddress> faces;
        lock (gate)
        {
            made = socket;
            faces = interfaces;
        }

        if (made is null)
            return;

        var group = new IPEndPoint(GROUP, MDNS_PORT);
        if (faces.Count == 0)
        {
            Send(packet, group);
            return;
        }

        lock (made)
        {
            foreach (IPAddress face in faces)
            {
                try
                {
                    made.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, face.GetAddressBytes());
                    made.SendTo(packet, group);
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException)
                {
                }
            }
        }
    }

    private void Send(byte[] packet, IPEndPoint to)
    {
        Socket? made;
        lock (gate)
            made = socket;
        if (made is null)
            return;
        lock (made)
        {
            try
            {
                made.SendTo(packet, to);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose()
    {
        StopAnnouncing();
        Socket? made;
        lock (gate)
        {
            disposed = true;
            browsing?.Dispose();
            sweeping?.Dispose();
            browsing = sweeping = null;
            made = socket;
            socket = null;
            receiving?.Cancel();
            receiving?.Dispose();
            receiving = null;
        }

        made?.Dispose();
    }
}
