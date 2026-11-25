using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// A local TCP port this machine shares over Link. <see cref="Devices"/> empty is every linked device, otherwise those
/// ids. The target is loopback unless the person explicitly allowed another host (<see cref="AllowLan"/>).
/// </summary>
internal sealed record HexLinkShare(string Id, string Name, int Port, string Host, bool AllowLan, IReadOnlyList<string> Devices)
{
    /// <summary>Whether the target is this machine's own loopback.</summary>
    public bool IsLoopback => HexLinkPorts.IsLoopbackHost(Host);

    public bool SharedWith(string deviceId) => Devices.Count == 0 || Devices.Contains(deviceId, StringComparer.OrdinalIgnoreCase);

    /// <summary>The hosts to try connecting to. The default loopback tries IPv4 and then IPv6, as dev servers bind either.</summary>
    public IEnumerable<string> TargetHosts() => Host == "127.0.0.1" ? ["127.0.0.1", "::1"] : [Host];
}

/// <summary>A share of another device as it lists it.</summary>
internal sealed record HexLinkRemoteShare(string Id, string Name, int Port, bool Lan);

/// <summary>A remote share opened here: the listener on this machine's loopback tunnelling to it.</summary>
internal sealed record HexLinkOpenedPort(string DeviceId, string DeviceName, string ShareId, string Name, int RemotePort, int LocalPort, int Connections)
{
    public string Address => $"127.0.0.1:{LocalPort}";
}

/// <summary>What this editor remembers wanting open on a device, so the port comes back when the link does.</summary>
internal sealed record HexLinkWantedPort(string DeviceId, string ShareId, string Name, int LocalPort);

/// <summary>
/// Shared ports (docs/hex-link-plan.md): this machine's shares, which another device with the Ports right may open, and
/// the ports of other devices opened here, each a listener on 127.0.0.1 that tunnels its connections over the link
/// (<see cref="HexLinkTunnels"/>). Shares and the opens wanted are kept in a file, so they survive a restart. At most
/// <see cref="MOST_SHARES"/> shares.
/// </summary>
internal sealed class HexLinkPorts : IDisposable
{
    public const int MOST_SHARES = 16;

    private sealed class Opened(HexLinkPeer peer, HexLinkRemoteShare share, TcpListener listener)
    {
        public HexLinkPeer Peer { get; } = peer;
        public HexLinkRemoteShare Share { get; } = share;
        public TcpListener Listener { get; } = listener;
        public CancellationTokenSource Stop { get; } = new();
        public int LocalPort => ((IPEndPoint)Listener.LocalEndpoint).Port;
    }

    private readonly Lock gate = new();
    private readonly string? path;
    private readonly List<HexLinkShare> shares = [];
    private readonly List<HexLinkWantedPort> wanted = [];
    private readonly Dictionary<(string Device, string Share), Opened> opened = [];
    private readonly Dictionary<string, IReadOnlyList<HexLinkRemoteShare>> remote = new(StringComparer.OrdinalIgnoreCase);

    public HexLinkPorts(string? path = null)
    {
        this.path = path;
        Load();
    }

    /// <summary>What goes to the audit log (the link sets it to the editor's), what was done, by or for which device, and on what.</summary>
    public Action<string, HexLinkDevice, string>? Audit { get; set; }

    /// <summary>Shares or opens moved, on whatever thread it happened on.</summary>
    public event Action? Changed;

    /// <summary>This machine's shares moved, for the links to tell the devices and close what's no longer shared.</summary>
    public event Action? SharesChanged;

    // ---------------- sharing ----------------

    public IReadOnlyList<HexLinkShare> Shares
    {
        get { lock (gate) return [.. shares]; }
    }

    public HexLinkShare? Find(string idOrName)
    {
        lock (gate)
            return shares.FirstOrDefault(share => share.Id == idOrName);
    }

    /// <summary>The shares a device may open.</summary>
    public IReadOnlyList<HexLinkShare> Visible(string deviceId)
    {
        lock (gate)
            return [.. shares.Where(share => share.SharedWith(deviceId))];
    }

    public static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out IPAddress? ip) && IPAddress.IsLoopback(ip));

    /// <summary>
    /// Shares a port. Only loopback targets unless <paramref name="allowLan"/>; names are 1 to 40 characters. Throws
    /// <see cref="ArgumentException"/> with what's wrong.
    /// </summary>
    public HexLinkShare Share(string name, int port, string? host = null, bool allowLan = false, IEnumerable<string>? devices = null)
    {
        string label = HexLinkNames.Clean(name) ?? throw new ArgumentException("A shared port needs a name.");
        if (port is < 1 or > 65535)
            throw new ArgumentException("A port is 1 to 65535.");
        host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        if (host.Length > 253 || host.Any(char.IsWhiteSpace))
            throw new ArgumentException("That is not a host.");
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            host = "127.0.0.1";
        if (IPAddress.TryParse(host, out IPAddress? ip) && IPAddress.IsLoopback(ip))
            host = ip.AddressFamily == AddressFamily.InterNetworkV6 ? "::1" : "127.0.0.1";
        else if (!allowLan)
            throw new ArgumentException($"{host} is not this machine. Only loopback can be shared unless sharing a LAN host is explicitly allowed.");

        HexLinkShare share;
        lock (gate)
        {
            if (shares.Count >= MOST_SHARES)
                throw new ArgumentException($"At most {MOST_SHARES} ports can be shared.");
            share = new HexLinkShare(Guid.NewGuid().ToString("N")[..12], label, port, host, allowLan && !IsLoopbackHost(host),
                [.. (devices ?? []).Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)]);
            shares.Add(share);
        }

        Save();
        SharesChanged?.Invoke();
        Changed?.Invoke();
        return share;
    }

    /// <summary>Stops sharing, by id or name. Its tunnels close and the devices that opened it are told. False when none.</summary>
    public bool Unshare(string idOrName)
    {
        lock (gate)
        {
            HexLinkShare? share = shares.FirstOrDefault(each => each.Id == idOrName)
                ?? shares.FirstOrDefault(each => each.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
            if (share is null || !shares.Remove(share))
                return false;
        }

        Save();
        SharesChanged?.Invoke();
        Changed?.Invoke();
        return true;
    }

    // ---------------- using ----------------

    /// <summary>The shares a device last listed.</summary>
    public IReadOnlyList<HexLinkRemoteShare> RemoteShares(string deviceId)
    {
        lock (gate)
            return remote.GetValueOrDefault(deviceId) ?? [];
    }

    public IReadOnlyList<HexLinkOpenedPort> Open
    {
        get
        {
            lock (gate)
                return [.. opened.Values.Select(Describe)];
        }
    }

    private static HexLinkOpenedPort Describe(Opened each) => new(each.Peer.Device.Id, each.Peer.Device.Name, each.Share.Id, each.Share.Name,
        each.Share.Port, each.LocalPort, each.Peer.Tunnels?.Using ?? 0);

    /// <summary>Lists a linked device's shares, and keeps what it said. Throws what the device refused with.</summary>
    public async Task<IReadOnlyList<HexLinkRemoteShare>> ListAsync(HexLinkPeer peer, CancellationToken cancel = default)
    {
        IReadOnlyList<HexLinkRemoteShare> listed = await peer.ListPorts(cancel);
        lock (gate)
            remote[peer.Device.Id] = listed;
        Changed?.Invoke();
        return listed;
    }

    /// <summary>
    /// Opens a device's shared port here: a listener on 127.0.0.1, the share's own port when that's free, else the next free
    /// one, and every connection to it tunnelled to the share. Opening what's open returns it.
    /// </summary>
    public async Task<HexLinkOpenedPort> OpenAsync(HexLinkPeer peer, string idOrName, int preferred = 0, CancellationToken cancel = default)
    {
        IReadOnlyList<HexLinkRemoteShare> listed = await ListAsync(peer, cancel);
        HexLinkRemoteShare share = listed.FirstOrDefault(each => each.Id == idOrName)
            ?? listed.FirstOrDefault(each => each.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"{peer.Device.Name} shares nothing called {idOrName} with this device.");
        var key = (peer.Device.Id, share.Id);
        Opened made;
        lock (gate)
        {
            if (opened.TryGetValue(key, out Opened? already) && !already.Peer.IsClosed)
                return Describe(already);
            TcpListener listener = Listen(preferred > 0 ? preferred : share.Port);
            made = new Opened(peer, share, listener);
            opened[key] = made;
            wanted.RemoveAll(each => each.DeviceId == key.Item1 && each.ShareId == key.Item2);
            wanted.Add(new HexLinkWantedPort(key.Item1, key.Item2, share.Name, made.LocalPort));
        }

        Save();
        _ = Task.Run(() => Accept(made));
        Audit?.Invoke("ports open", peer.Device, $"{share.Name} (port {share.Port}) on 127.0.0.1:{made.LocalPort}");
        Changed?.Invoke();
        return Describe(made);
    }

    // The preferred port when it's free, else the next free after it, else any
    private static TcpListener Listen(int port)
    {
        for (int at = port; at <= Math.Min(65535, port + 100); at++)
        {
            var listener = new TcpListener(IPAddress.Loopback, at);
            try
            {
                listener.Start(64);
                return listener;
            }
            catch (SocketException)
            {
                listener.Stop();
            }
        }

        var any = new TcpListener(IPAddress.Loopback, 0);
        any.Start(64);
        return any;
    }

    private async Task Accept(Opened open)
    {
        try
        {
            while (!open.Stop.IsCancellationRequested)
            {
                TcpClient client = await open.Listener.AcceptTcpClientAsync(open.Stop.Token);
                if (open.Peer.Tunnels is not { } tunnels)
                {
                    client.Dispose();
                    continue;
                }

                _ = tunnels.Use(client, open.Share.Id, open.Share.Name);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private void Stop(Opened open)
    {
        try { open.Stop.Cancel(); } catch (Exception) { }
        try { open.Listener.Stop(); } catch (Exception) { }
    }

    /// <summary>Closes an opened port (by share id or name) and forgets wanting it back. False when it wasn't open.</summary>
    public bool Close(string deviceId, string idOrName)
    {
        Opened? found;
        lock (gate)
        {
            found = opened.Values.FirstOrDefault(each => each.Peer.Device.Id == deviceId && (each.Share.Id == idOrName || each.Share.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase)));
            if (found is null)
                return false;
            opened.Remove((deviceId, found.Share.Id));
            wanted.RemoveAll(each => each.DeviceId == deviceId && each.ShareId == found.Share.Id);
        }

        Stop(found);
        Save();
        Audit?.Invoke("ports close", found.Peer.Device, $"{found.Share.Name} on 127.0.0.1:{found.LocalPort}");
        Changed?.Invoke();
        return true;
    }

    /// <summary>The device went: its ports are no longer listened on here, but wanted still, to come back with the link.</summary>
    public void PeerDown(string deviceId)
    {
        List<Opened> gone;
        lock (gate)
        {
            gone = [.. opened.Values.Where(each => each.Peer.Device.Id == deviceId)];
            foreach (Opened each in gone)
                opened.Remove((deviceId, each.Share.Id));
            remote.Remove(deviceId);
        }

        foreach (Opened each in gone)
            Stop(each);
        if (gone.Count > 0)
            Changed?.Invoke();
    }

    /// <summary>A device linked: what it shares is listed and what was open on it is opened again.</summary>
    public void PeerUp(HexLinkPeer peer) => _ = Task.Run(async () =>
    {
        try
        {
            await Refresh(peer);
        }
        catch (Exception)
        {
        }
    });

    /// <summary>
    /// What a device shares listed again: what it no longer shares is closed and forgotten, what was wanted and is shared
    /// is opened. A device that won't list (its right gone) closes what's open on it but keeps what's wanted.
    /// </summary>
    public async Task Refresh(HexLinkPeer peer)
    {
        // Not asked what the device doesn't let this machine open: the refusal would be audited on every link-up
        if (!HexLinkAllowed.Has(peer.Granted, HexLinkRights.Ports))
        {
            PeerDown(peer.Device.Id);
            return;
        }

        IReadOnlyList<HexLinkRemoteShare> listed;
        try
        {
            listed = await ListAsync(peer);
        }
        catch (HexLinkException)
        {
            PeerDown(peer.Device.Id);
            return;
        }

        List<HexLinkWantedPort> mine;
        lock (gate)
            mine = [.. wanted.Where(each => each.DeviceId == peer.Device.Id)];
        foreach (HexLinkWantedPort want in mine)
        {
            HexLinkRemoteShare? share = listed.FirstOrDefault(each => each.Id == want.ShareId);
            if (share is null)
            {
                Close(peer.Device.Id, want.ShareId);
                lock (gate)
                    wanted.RemoveAll(each => each.DeviceId == want.DeviceId && each.ShareId == want.ShareId);
                Save();
                continue;
            }

            bool isOpen;
            lock (gate)
                isOpen = opened.TryGetValue((want.DeviceId, want.ShareId), out Opened? open) && !open.Peer.IsClosed;
            if (!isOpen)
            {
                try
                {
                    await OpenAsync(peer, want.ShareId, want.LocalPort);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    // ---------------- keeping ----------------

    private void Load()
    {
        if (path is null || !File.Exists(path))
            return;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
                return;
            foreach (JsonObject each in HexLinkData.Objects(root, "shares", MOST_SHARES))
            {
                if (HexLinkData.String(each, "id", 40) is { Length: > 0 } id && HexLinkData.String(each, "name", 40) is { Length: > 0 } name
                    && HexLinkData.Int(each, "port") is >= 1 and <= 65535 and { } port && HexLinkData.String(each, "host", 253) is { Length: > 0 } host)
                    shares.Add(new HexLinkShare(id, name, port, host, HexLinkData.Bool(each, "lan") == true, [.. HexLinkData.Strings(each, "devices", 64, 80)]));
            }

            foreach (JsonObject each in HexLinkData.Objects(root, "wanted", 256))
            {
                if (HexLinkData.String(each, "device", 80) is { Length: > 0 } device && HexLinkData.String(each, "share", 80) is { Length: > 0 } share)
                    wanted.Add(new HexLinkWantedPort(device, share, HexLinkData.String(each, "name", 40) ?? share, HexLinkData.Int(each, "port") ?? 0));
            }
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
        }
    }

    private void Save()
    {
        if (path is null)
            return;
        JsonObject root;
        lock (gate)
        {
            root = new JsonObject
            {
                ["shares"] = new JsonArray([.. shares.Select(share => (JsonNode)new JsonObject
                {
                    ["id"] = share.Id, ["name"] = share.Name, ["port"] = share.Port, ["host"] = share.Host, ["lan"] = share.AllowLan,
                    ["devices"] = new JsonArray([.. share.Devices.Select(id => (JsonNode)id)])
                })]),
                ["wanted"] = new JsonArray([.. wanted.Select(want => (JsonNode)new JsonObject
                {
                    ["device"] = want.DeviceId, ["share"] = want.ShareId, ["name"] = want.Name, ["port"] = want.LocalPort
                })])
            };
        }

        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
                Directory.CreateDirectory(folder);
            string temp = path + ".tmp";
            File.WriteAllText(temp, root.ToJsonString());
            File.Move(temp, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        List<Opened> all;
        lock (gate)
        {
            all = [.. opened.Values];
            opened.Clear();
        }

        foreach (Opened each in all)
            Stop(each);
    }
}
