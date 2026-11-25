using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// The shared-port tunnels over one link (docs/hex-link-plan.md, shared ports). One half serves: the device at the other end
/// opened a port this machine shares, and each TCP connection it makes is connected here to the shared port. The other half
/// uses: a listener of this machine accepted a connection and tunnels it to the device that shares the port. A tunnel's
/// bytes travel as ordinary link requests, <c>ports/data</c> each way, so they ride the link's one authenticated
/// connection with its admission, rate limit and liveness. A chunk is answered once the other end has written it to its
/// socket, and the next isn't sent before then, which is the backpressure: a slow reader slows its writer to its own pace.
/// </summary>
internal sealed class HexLinkTunnels(HexLinkSession session, IHexLinkServices services) : IDisposable
{
    /// <summary>The most a chunk of a tunnel carries, in bytes before base64.</summary>
    public const int CHUNK = 96 * 1024;

    /// <summary>The most connections tunnelled at once over one link, serving and using together.</summary>
    public const int MOST_TUNNELS = 64;

    /// <summary>The most bytes a second one link moves for tunnels, both directions together.</summary>
    public const long BYTES_A_SECOND = 24L * 1024 * 1024;

    private sealed class Tunnel(long id, string shareId, string name, TcpClient client)
    {
        public long Id { get; } = id;
        public string ShareId { get; } = shareId;
        public string Name { get; } = name;
        public TcpClient Client { get; } = client;
        public NetworkStream Stream { get; } = client.GetStream();
        public CancellationTokenSource Stop { get; } = new();
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool LocalDone;
        public volatile bool RemoteDone;
        public int Dropped;
        public long In;
        public long Out;
    }

    private readonly ConcurrentDictionary<long, Tunnel> serving = new();
    private readonly ConcurrentDictionary<long, Tunnel> used = new();
    private readonly Lock gate = new();
    private long last;
    private long windowStart;
    private long windowBytes;
    private int disposed;

    /// <summary>How many connections this machine is serving to the other end now.</summary>
    public int Serving => serving.Count;

    /// <summary>How many connections of this machine are being tunnelled to the other end now.</summary>
    public int Using => used.Count;

    private HexLinkDevice Device => session.Connection.Device;

    private readonly HashSet<string> refusedAudited = [];

    private void Require(string what)
    {
        if (HexLinkAllowed.Has(session.Connection.Rights, HexLinkRights.Ports))
            return;
        // Said once for each kind of ask until the rights move: a device polling the list isn't written down on every poll
        bool first;
        lock (refusedAudited)
            first = refusedAudited.Add(what);
        if (first)
            services.Audit("ports refused", Device, $"{what}, it needs \"{HexLinkData.Readable("ports")}\"");
        throw HexLinkException.NotAllowed("ports");
    }

    /// <summary>The shares of this machine the device may open.</summary>
    public JsonObject List()
    {
        Require("ports/list");
        var shares = new JsonArray();
        foreach (HexLinkShare share in services.Ports?.Visible(Device.Id) ?? [])
            shares.Add((JsonNode)new JsonObject { ["id"] = share.Id, ["name"] = share.Name, ["port"] = share.Port, ["lan"] = !share.IsLoopback });
        return new JsonObject { ["shares"] = shares };
    }

    // ---------------- serving ----------------

    /// <summary>The device asks for a connection to a shared port, made here and answered with its tunnel's id.</summary>
    public async Task<JsonNode?> Connect(JsonObject parameters, CancellationToken cancel)
    {
        Require("ports/connect");
        string id = HexLinkData.Need(parameters, "share", 80);
        if (services.Ports?.Find(id) is not { } share || !share.SharedWith(Device.Id))
        {
            services.Audit("ports refused", Device, $"connect to share {Cut(id)}, it is not shared with this device");
            throw HexLinkException.NotFound("Nothing is shared with this device under that id.");
        }

        if (serving.Count >= MOST_TUNNELS)
        {
            services.Audit("ports refused", Device, $"connect to {share.Name}, {MOST_TUNNELS} connections are open already");
            throw new HexLinkException(HexLinkErrors.BUSY, $"This device already has {MOST_TUNNELS} tunnelled connections open.");
        }

        TcpClient? client = null;
        foreach (string host in share.TargetHosts())
        {
            var attempt = new TcpClient { NoDelay = true };
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await attempt.ConnectAsync(host, share.Port, timeout.Token);
                client = attempt;
                break;
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException or IOException)
            {
                attempt.Dispose();
                cancel.ThrowIfCancellationRequested();
            }
        }

        if (client is null)
        {
            services.Audit("ports failed", Device, $"connect to {share.Name} (port {share.Port}), nothing answered");
            throw HexLinkException.NotFound($"Nothing answers on {share.Name}'s port.");
        }

        var tunnel = new Tunnel(Interlocked.Increment(ref last), share.Id, share.Name, client);
        bool added;
        lock (gate)
            added = Volatile.Read(ref disposed) == 0 && serving.Count < MOST_TUNNELS && serving.TryAdd(tunnel.Id, tunnel);
        if (!added)
        {
            client.Dispose();
            throw new HexLinkException(HexLinkErrors.BUSY, $"This device already has {MOST_TUNNELS} tunnelled connections open.");
        }

        services.Audit("ports connect", Device, $"{share.Name} (port {share.Port}), connection {tunnel.Id}");
        _ = Task.Run(async () =>
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(tunnel.Stop.Token);
            wait.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await tunnel.Ready.Task.WaitAsync(wait.Token);
            }
            catch (OperationCanceledException)
            {
                Drop(tunnel, serving, "the device never started it");
                return;
            }

            await Pump(tunnel, "opener", serving);
        });
        return new JsonObject { ["conn"] = tunnel.Id };
    }

    /// <summary>The using end has registered a tunnel and is ready for the shared port's first bytes.</summary>
    public JsonNode? Ready(JsonObject parameters)
    {
        Require("ports/ready");
        if (HexLinkData.Long(parameters, "conn") is { } id && serving.TryGetValue(id, out Tunnel? tunnel))
            tunnel.Ready.TrySetResult();
        return new JsonObject();
    }

    // ---------------- using ----------------

    /// <summary>
    /// A connection this machine's listener accepted, tunnelled to a port the device at the other end shares: its connection
    /// opened there, then the two sockets copied into each other until either closes.
    /// </summary>
    public async Task Use(TcpClient client, string shareId, string name)
    {
        Tunnel? tunnel = null;
        try
        {
            client.NoDelay = true;
            if (used.Count >= MOST_TUNNELS)
            {
                services.Audit("ports refused", Device, $"connection to {name}, {MOST_TUNNELS} are open already");
                client.Dispose();
                return;
            }

            JsonNode? reply = await AskPatiently("ports/connect", () => new JsonObject { ["share"] = shareId }, session.Closing, null);
            long id = HexLinkData.Long(reply as JsonObject, "conn") ?? throw HexLinkException.Invalid("The device gave no connection.");
            tunnel = new Tunnel(id, shareId, name, client);
            if (Volatile.Read(ref disposed) != 0 || !used.TryAdd(id, tunnel))
                throw new HexLinkException(HexLinkErrors.CLOSED, "The link closed.");
            services.Audit("ports use", Device, $"{name}, connection {id}");
            await AskPatiently("ports/ready", () => new JsonObject { ["conn"] = id }, session.Closing, null);
        }
        catch (Exception)
        {
            if (tunnel is not null)
                Drop(tunnel, used, null);
            else
                client.Dispose();
            return;
        }

        await Pump(tunnel, "sharer", used);
    }

    // ---------------- both ----------------

    // Copies a socket into the link until it ends. A chunk is awaited (answered once written at the other end) before the next
    private async Task Pump(Tunnel tunnel, string to, ConcurrentDictionary<long, Tunnel> table)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CHUNK);
        try
        {
            CancellationToken stop = tunnel.Stop.Token;
            while (true)
            {
                int read = await tunnel.Stream.ReadAsync(buffer.AsMemory(0, CHUNK), stop);
                if (read == 0)
                    break;
                await Throttle(read, stop);
                string data = Convert.ToBase64String(buffer, 0, read);
                await Send("ports/data", () => new JsonObject { ["to"] = to, ["conn"] = tunnel.Id, ["data"] = data }, stop);
                Interlocked.Add(ref tunnel.Out, read);
            }

            tunnel.LocalDone = true;
            await Send("ports/close", () => new JsonObject { ["to"] = to, ["conn"] = tunnel.Id, ["half"] = true }, tunnel.Stop.Token);
            if (tunnel.RemoteDone)
                Drop(tunnel, table, "closed");
        }
        catch (Exception) when (Volatile.Read(ref tunnel.Dropped) != 0)
        {
            // Dropped from the other end or here already, nothing left to say
        }
        catch (Exception)
        {
            await Abort(tunnel, to, table);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // Tells the other end this tunnel is over, as well as it can, and lets go of it
    private async Task Abort(Tunnel tunnel, string to, ConcurrentDictionary<long, Tunnel> table)
    {
        if (Volatile.Read(ref tunnel.Dropped) == 0 && !session.IsClosed)
        {
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await session.Ask("ports/close", new JsonObject { ["to"] = to, ["conn"] = tunnel.Id, ["half"] = false }, limit.Token, TimeSpan.FromSeconds(3));
            }
            catch (Exception)
            {
            }
        }

        Drop(tunnel, table, "closed");
    }

    private void Drop(Tunnel tunnel, ConcurrentDictionary<long, Tunnel> table, string? why)
    {
        if (Interlocked.Exchange(ref tunnel.Dropped, 1) != 0)
            return;
        table.TryRemove(tunnel.Id, out _);
        try { tunnel.Stop.Cancel(); } catch (Exception) { }
        try { tunnel.Client.Dispose(); } catch (Exception) { }
        if (why is not null && ReferenceEquals(table, serving))
            services.Audit("ports close", Device, $"{tunnel.Name}, connection {tunnel.Id}, {why}, {tunnel.In} bytes in and {Interlocked.Read(ref tunnel.Out)} out");
    }

    private async Task Send(string method, Func<JsonObject> parameters, CancellationToken cancel) =>
        await AskPatiently(method, parameters, cancel, TimeSpan.FromSeconds(60));

    // Asks, and when the link is over its requests a second waits for the next rather than failing the tunnel
    private async Task<JsonNode?> AskPatiently(string method, Func<JsonObject> parameters, CancellationToken cancel, TimeSpan? timeout)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await session.Ask(method, parameters(), cancel, timeout);
            }
            catch (HexLinkException e) when (e.Code == HexLinkErrors.BUSY && attempt < 400)
            {
                // The link takes only so many requests a second, so wait for the next
                await Task.Delay(25, cancel);
            }
        }
    }

    // The bytes a second the link spends on tunnels, past it a chunk waits for the next second
    private async Task Throttle(int bytes, CancellationToken cancel)
    {
        while (true)
        {
            int wait;
            lock (gate)
            {
                long now = Environment.TickCount64;
                if (now - windowStart >= 1000)
                {
                    windowStart = now;
                    windowBytes = 0;
                }

                if (windowBytes == 0 || windowBytes + bytes <= BYTES_A_SECOND)
                {
                    windowBytes += bytes;
                    return;
                }

                wait = (int)Math.Max(1, 1000 - (now - windowStart));
            }

            await Task.Delay(wait, cancel);
        }
    }

    private Tunnel Find(JsonObject parameters)
    {
        string? to = HexLinkData.String(parameters, "to", 10);
        if (to is not ("sharer" or "opener") || HexLinkData.Long(parameters, "conn") is not { } id)
            throw HexLinkException.Invalid("A tunnel message names who it is for and its connection.");
        if (to == "sharer")
            Require("ports/data");
        if (!(to == "sharer" ? serving : used).TryGetValue(id, out Tunnel? tunnel))
            throw HexLinkException.NotFound("There is no such tunnelled connection.");
        return tunnel;
    }

    /// <summary>Bytes from the other end of a tunnel, written to its socket before the answer.</summary>
    public async Task<JsonNode?> Data(JsonObject parameters, CancellationToken cancel)
    {
        Tunnel tunnel = Find(parameters);
        string text = HexLinkData.String(parameters, "data", CHUNK / 3 * 4 + 16) ?? throw HexLinkException.Invalid("A tunnel's data is base64.");
        if (text.Length > CHUNK / 3 * 4 + 8)
            throw HexLinkException.Invalid("A tunnel's chunk is too big.");
        // Decoded into a pooled buffer: a full chunk is 96 KiB, which would otherwise be a large-object-heap array each time
        byte[] bytes = ArrayPool<byte>.Shared.Rent(CHUNK + 16);
        try
        {
            if (!Convert.TryFromBase64Chars(text, bytes, out int length))
                throw HexLinkException.Invalid("A tunnel's data is base64.");

            if (tunnel.RemoteDone || Volatile.Read(ref tunnel.Dropped) != 0)
                throw HexLinkException.NotFound("That tunnelled connection is closed.");
            await Throttle(length, cancel);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, tunnel.Stop.Token);
            try
            {
                await tunnel.Stream.WriteAsync(bytes.AsMemory(0, length), linked.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                throw HexLinkException.NotFound("That tunnelled connection is closed.");
            }

            Interlocked.Add(ref tunnel.In, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }

        return new JsonObject();
    }

    /// <summary>The other end finished its side of a tunnel (half) or dropped it.</summary>
    public JsonNode? Close(JsonObject parameters)
    {
        string? to = HexLinkData.String(parameters, "to", 10);
        if (to is not ("sharer" or "opener") || HexLinkData.Long(parameters, "conn") is not { } id)
            throw HexLinkException.Invalid("A tunnel message names who it is for and its connection.");
        ConcurrentDictionary<long, Tunnel> table = to == "sharer" ? serving : used;
        if (!table.TryGetValue(id, out Tunnel? tunnel))
            return new JsonObject();
        if (HexLinkData.Bool(parameters, "half") == true)
        {
            tunnel.RemoteDone = true;
            try { tunnel.Client.Client.Shutdown(SocketShutdown.Send); } catch (Exception) { }
            if (tunnel.LocalDone)
                Drop(tunnel, table, "closed");
        }
        else
        {
            Drop(tunnel, table, "closed by the device");
        }

        return new JsonObject();
    }

    /// <summary>The device's rights changed: what it may no longer open is closed.</summary>
    public void RightsMoved()
    {
        lock (refusedAudited)
            refusedAudited.Clear();
        if (!HexLinkAllowed.Has(session.Connection.Rights, HexLinkRights.Ports))
            CloseServing(_ => true, "its ports right was taken away");
    }

    /// <summary>Shares changed: the tunnels of shares the device no longer may open are closed, and it is told.</summary>
    public void Revalidate()
    {
        HexLinkPorts? ports = services.Ports;
        bool right = HexLinkAllowed.Has(session.Connection.Rights, HexLinkRights.Ports);
        CloseServing(tunnel => !right || ports?.Find(tunnel.ShareId) is not { } share || !share.SharedWith(Device.Id), "it was unshared");
        if (right)
            _ = session.TellAsync("ports/changed");
    }

    private void CloseServing(Func<Tunnel, bool> pick, string why)
    {
        foreach (Tunnel tunnel in serving.Values.Where(pick))
        {
            if (Volatile.Read(ref tunnel.Dropped) != 0)
                continue;
            long id = tunnel.Id;
            _ = Task.Run(async () =>
            {
                await Abort(tunnel, "opener", serving);
                services.Audit("ports close", Device, $"connection {id}, {why}");
            });
        }
    }

    /// <summary>Every tunnel closed, when the link goes.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        foreach (Tunnel tunnel in serving.Values)
            Drop(tunnel, serving, "the link went");
        foreach (Tunnel tunnel in used.Values)
            Drop(tunnel, used, null);
    }

    private static string Cut(string text) => text.Length > 40 ? text[..40] + "..." : text;
}
