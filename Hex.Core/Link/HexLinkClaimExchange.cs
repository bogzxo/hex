using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>Remembers which authenticated device asked for a tool, without trusting arguments supplied by the caller.</summary>
internal static class HexLinkClaimCaller
{
    private static readonly ConditionalWeakTable<JsonObject, HexLinkDevice> callers = new();
    public static void Bind(JsonObject arguments, HexLinkDevice device) => callers.Add(arguments, device);
    public static HexLinkDevice? Of(JsonObject arguments) => callers.TryGetValue(arguments, out var device) ? device : null;
}

/// <summary>
/// A folder claims are exchanged for. One with a pinned authority is scoped by it; one without is kept in sync with
/// devices (the legacy pair, PairedWith the newline separated ids of those) and its claims are exchanged with exactly those.
/// </summary>
internal sealed record HexLinkClaimFolder(HexLinkFolder Folder, HexLinkClaimScope Scope, string AuthorityId, string PairedWith = "");

/// <summary>Batches claim traffic once per editor interval. Only authenticated direct origins are relayed.</summary>
internal sealed class HexLinkClaimExchange(IHexLinkServices services)
{
    private sealed class Route(HexLinkPeer peer)
    {
        public HexLinkPeer Peer { get; } = peer;
        public HashSet<HexLinkClaimScope> Outgoing { get; } = [];
        public HashSet<HexLinkClaimScope> Incoming { get; } = [];
        public HashSet<HexLinkClaimScope> PendingSnapshots { get; } = [];
        public long Retry;
        public bool Asking, NeedsSnapshot;
    }

    private sealed class LocalHead
    {
        public Dictionary<HexLinkClaimKey, HexLinkClaimRow> Rows { get; set; } = [];
        public Dictionary<HexLinkClaimKey, (long Since, long Dropped)> Recent { get; } = [];
        public long Sequence;
    }

    // How long a claim that was let go is remembered, so claiming the same thing again keeps the first time
    private const long REMEMBER_MS = 120_000;

    private readonly Lock gate = new();
    private readonly Dictionary<string, Route> routes = new(StringComparer.Ordinal);
    private readonly Dictionary<HexLinkClaimScope, LocalHead> local = [];
    private readonly Dictionary<(HexLinkClaimScope Scope, string Origin), HexLinkClaimFrame> pending = [];
    private readonly HashSet<(HexLinkClaimScope Scope, string Origin)> superseded = [];
    private readonly string epoch = Guid.NewGuid().ToString("N");
    private volatile HexLinkClaimFolder[] folders = [];
    private long nextHeartbeat;
    private int sending;
    public bool Overflow { get; private set; }
    public HexLinkClaims Board { get; } = new();
    public IReadOnlyList<HexLinkClaimFolder> Folders => folders;

    public IReadOnlyList<HexLinkClaim> List(long now) =>
        [.. Board.List(now).Where(claim => !Hidden(claim.Scope, claim.Draft.Key))];

    public IReadOnlyList<HexLinkClaim> At(HexLinkClaimScope scope, string path, bool prefix, long now) =>
        Hidden(scope, path, prefix) ? [] : [.. Board.At(scope, path, prefix, now).Where(claim => !Hidden(scope, claim.Draft.Key))];

    public void Join(HexLinkPeer peer)
    {
        lock (gate)
            routes[peer.Device.Id] = new Route(peer);
    }

    public void Left(HexLinkPeer peer)
    {
        lock (gate)
        {
            if (!routes.TryGetValue(peer.Device.Id, out Route? route) || route.Peer != peer)
                return;
            routes.Remove(peer.Device.Id);
            long now = Environment.TickCount64;
            Board.Disconnect(peer.Device.Id, now);
            foreach (HexLinkClaimFolder folder in folders.Where(folder => folder.AuthorityId == services.Device.Id))
            {
                foreach (HexLinkClaimFrame frame in Board.Snapshot(folder.Scope, null, now, directOnly: true).Where(frame => frame.Origin.Id == peer.Device.Id))
                    Queue(frame with { Snapshot = false, Add = [], Heartbeat = true });
            }
        }
    }

    public void RefreshFolders()
    {
        HexLinkPeer[] peers;
        lock (gate)
            peers = [.. routes.Values.Select(route => route.Peer).Where(peer => !peer.IsClosed)];
        var chosen = peers.ToDictionary(peer => peer.Device.Id, peer => services.ChosenSyncFolders(peer.Device.Id));
        var now = services.Workspace.Folders.Where(folder => HexLinkClaims.ValidPath(folder.Name, false) && !folder.Name.Contains('/'))
            .Select(folder => folder.Authority is { } authority
                ? new HexLinkClaimFolder(folder, new HexLinkClaimScope(folder.Name,
                    HexLinkFiles.Hash(Encoding.UTF8.GetBytes($"{authority.DeviceId}\n{authority.Epoch}\n{folder.Name}"))), authority.DeviceId)
                : Paired(folder, peers, chosen))
            .OfType<HexLinkClaimFolder>().Take(HexLinkHost.MOST_TREES).ToArray();
        lock (gate)
        {
            if (folders.SequenceEqual(now))
                return;
            folders = now;
            foreach (Route route in routes.Values)
            {
                route.Incoming.RemoveWhere(scope => !MayExchange(scope, route));
                route.Outgoing.RemoveWhere(scope => !MayExchange(scope, route));
                route.Retry = 0;
            }
        }
    }

    // A folder without an authority that this device keeps in sync with a linked device, or that device with it, counts
    // for claims between the two: both compute the same scope from the folder's name, so each side's claims reach the other.
    private static HexLinkClaimFolder? Paired(HexLinkFolder folder, HexLinkPeer[] peers, Dictionary<string, IReadOnlyList<string>> chosen)
    {
        string[] with = [.. peers.Where(peer => peer.Workspace.Folders.FirstOrDefault(other => PlatformPaths.Comparer.Equals(other.Name, folder.Name))
                is { Authority: null }
            && (chosen[peer.Device.Id].Contains(folder.Name, PlatformPaths.Comparer)
                || peer.ChosenSyncFolders.Contains(folder.Name, PlatformPaths.Comparer)))
            .Select(peer => peer.Device.Id).Order(StringComparer.Ordinal)];
        if (with.Length == 0)
            return null;
        string identity = HexLinkFiles.Hash(Encoding.UTF8.GetBytes($"sync\n{folder.Name}"));
        return new HexLinkClaimFolder(folder, new HexLinkClaimScope(folder.Name, identity), string.Empty, string.Join('\n', with));
    }

    public bool Subscribed(string deviceId, HexLinkClaimScope scope)
    {
        lock (gate)
            return routes.TryGetValue(deviceId, out Route? route) && route.Incoming.Contains(scope)
                && HexLinkAllowed.Has(route.Peer.Session.Connection.Rights, HexLinkRights.View);
    }

    public JsonObject Watch(HexLinkSession session, JsonObject arguments)
    {
        lock (gate)
        {
            if (!routes.TryGetValue(session.Connection.Device.Id, out Route? route) || route.Peer.Session != session)
                throw new HexLinkException(HexLinkErrors.BUSY, "The claim link is still joining.");
            HexLinkClaimScope[] requested = ScopesOf(arguments, "folders");
            var accepted = new HashSet<HexLinkClaimScope>(requested.Where(scope => MayExchange(scope, route)));
            route.Incoming.Clear();
            route.Incoming.UnionWith(accepted);
            route.PendingSnapshots.IntersectWith(accepted);
            route.PendingSnapshots.UnionWith(accepted);
            return new JsonObject { ["accepted"] = ScopesJson(accepted) };
        }
    }

    public bool Receive(HexLinkSession session, JsonObject arguments)
    {
        long now = Environment.TickCount64;
        lock (gate)
        {
            if (!routes.TryGetValue(session.Connection.Device.Id, out Route? route) || route.Peer.Session != session
                || !HexLinkAllowed.Has(route.Peer.Granted, HexLinkRights.View))
                return false;
            if (arguments["frames"] is not JsonArray { Count: <= 128 } frames)
                return false;

            bool changed = false;
            foreach (JsonNode? node in frames)
            {
                if (node is not JsonObject json || HexLinkClaims.Of(json, route.Peer.Device, now) is not { } frame
                    || !route.Outgoing.Contains(frame.Scope) || !MayExchange(frame.Scope, route) || frame.Origin.Id == services.Device.Id
                    || frame.Origin.Id != route.Peer.Device.Id && Owner(frame.Scope) != route.Peer.Device.Id)
                    continue;
                frame = Filter(frame);
                HexLinkClaimReceipt receipt = Board.Receive(frame, route.Peer.Device.Id, now);
                if (receipt == HexLinkClaimReceipt.Gap)
                {
                    route.Retry = 0;
                    route.NeedsSnapshot = true;
                    continue;
                }
                if (receipt == HexLinkClaimReceipt.Overflow)
                    Overflow = true;
                if (receipt != HexLinkClaimReceipt.Changed)
                    continue;
                changed = true;
                if (frame.Origin.Id == route.Peer.Device.Id && Owner(frame.Scope) == services.Device.Id)
                    Queue(frame);
            }
            return changed;
        }
    }

    public bool Update(IReadOnlyDictionary<HexLinkClaimScope, Dictionary<HexLinkClaimKey, HexLinkClaimDraft>> drafts)
    {
        long now = Environment.TickCount64;
        bool changed;
        lock (gate)
        {
            changed = Board.Expire(now);
            int count = 0;
            foreach (HexLinkClaimScope scope in local.Keys.Union(drafts.Keys).ToArray())
            {
                if (!local.TryGetValue(scope, out LocalHead? head))
                    local[scope] = head = new LocalHead();
                var rows = new Dictionary<HexLinkClaimKey, HexLinkClaimRow>();
                if (Known(scope) && drafts.TryGetValue(scope, out var shown))
                {
                    foreach (HexLinkClaimDraft draft in shown.Values)
                    {
                        if (Hidden(scope, draft.Key))
                            continue;
                        if (++count > HexLinkClaims.DEVICE_LIMIT)
                        {
                            Overflow = true;
                            break;
                        }
                        rows.Add(draft.Key, new HexLinkClaimRow(draft,
                            head.Rows.TryGetValue(draft.Key, out HexLinkClaimRow? existing) ? existing.SinceTick
                            : head.Recent.TryGetValue(draft.Key, out var gone) && now - gone.Dropped < REMEMBER_MS ? gone.Since : now));
                    }
                }

                // A claim that drops out for a moment (a saved editor) and comes back is still the one claimed first
                foreach (var (key, row) in head.Rows)
                {
                    if (!rows.ContainsKey(key))
                        head.Recent[key] = (row.SinceTick, now);
                }
                foreach (var key in head.Recent.Where(pair => now - pair.Value.Dropped >= REMEMBER_MS).Select(pair => pair.Key).ToArray())
                    head.Recent.Remove(key);

                HexLinkClaimRow[] add = [.. rows.Values.Where(row => !head.Rows.TryGetValue(row.Draft.Key, out var before) || before.Draft != row.Draft)];
                HexLinkClaimKey[] drop = [.. head.Rows.Keys.Where(key => !rows.ContainsKey(key))];
                if (add.Length + drop.Length == 0 && (rows.Count == 0 || now < nextHeartbeat))
                    continue;
                bool delta = add.Length + drop.Length > 0;
                head.Sequence += delta ? 1 : 0;
                var frame = new HexLinkClaimFrame(scope, services.Device, epoch, head.Sequence, false, HexLinkClaims.LEASE_MS, add, drop, Heartbeat: !delta);
                HexLinkClaimReceipt receipt = Board.Receive(frame, services.Device.Id, now);
                if (receipt == HexLinkClaimReceipt.Gap)
                {
                    frame = frame with { Snapshot = true, Heartbeat = false, Add = [.. rows.Values], Drop = [] };
                    receipt = Board.Receive(frame, services.Device.Id, now);
                    delta = true;
                }
                if (receipt == HexLinkClaimReceipt.Overflow)
                {
                    Overflow = true;
                    if (add.Length + drop.Length > 0)
                        head.Sequence--;
                    continue;
                }
                head.Rows = rows;
                if (delta)
                {
                    Queue(frame);
                    changed = true;
                }
            }
            if (Interlocked.CompareExchange(ref sending, 1, 0) == 0)
                _ = Task.Run(() => Send(now));
        }
        return changed;
    }

    private void Queue(HexLinkClaimFrame frame)
    {
        var key = (frame.Scope, frame.Origin.Id);
        if (pending.ContainsKey(key))
            superseded.Add(key);
        pending[key] = frame;
    }

    private async Task Send(long now)
    {
        try
        {
            List<(Route Route, HexLinkClaimFrame[] Frames)> send = [];
            List<(Route Route, HexLinkClaimScope[] Scopes)> watch = [];
            lock (gate)
            {
                bool heartbeat = now >= nextHeartbeat;
                if (heartbeat)
                    nextHeartbeat = now + HexLinkClaims.HEARTBEAT_MS;

                var snapshots = new Dictionary<HexLinkClaimScope, IReadOnlyList<HexLinkClaimFrame>>();
                foreach (Route route in routes.Values)
                {
                    if (route.Peer.IsClosed)
                        continue;
                    if (HexLinkAllowed.Has(route.Peer.Granted, HexLinkRights.View))
                    {
                        HexLinkClaimScope[] common = [.. folders.Where(folder => MayExchange(folder.Scope, route)
                                && route.Peer.Workspace.Folders.Any(other => other.Name == folder.Scope.Folder))
                            .Select(folder => folder.Scope)];
                        if (!route.Asking && (route.NeedsSnapshot || !route.Outgoing.SetEquals(common)) && now >= route.Retry)
                        {
                            route.Retry = now + 5_000;
                            route.Asking = true;
                            route.Outgoing.UnionWith(common);
                            watch.Add((route, common));
                        }
                    }
                    else if (route.Outgoing.Count > 0)
                    {
                        route.Outgoing.Clear();
                        Board.Disconnect(route.Peer.Device.Id, now);
                    }

                    if (!HexLinkAllowed.Has(route.Peer.Session.Connection.Rights, HexLinkRights.View))
                    {
                        route.Incoming.Clear();
                        route.PendingSnapshots.Clear();
                        continue;
                    }

                    var frames = new List<HexLinkClaimFrame>();
                    foreach (HexLinkClaimScope scope in route.Incoming)
                    {
                        if (!heartbeat && !route.PendingSnapshots.Contains(scope) && !pending.Keys.Any(key => key.Scope == scope))
                            continue;
                        if (!snapshots.TryGetValue(scope, out var feeds))
                            snapshots[scope] = feeds = Board.Snapshot(scope, null, now, directOnly: true);
                        foreach (HexLinkClaimFrame snap in feeds)
                        {
                            if (snap.Origin.Id == route.Peer.Device.Id)
                                continue;
                            var key = (scope, snap.Origin.Id);
                            if (route.PendingSnapshots.Contains(scope) || superseded.Contains(key))
                                frames.Add(snap);
                            else if (pending.TryGetValue(key, out var delta))
                                frames.Add(delta with { Lease = snap.Lease });
                            else if (heartbeat && snap.Add.Count > 0)
                                frames.Add(snap with { Snapshot = false, Add = [], Heartbeat = true });
                        }
                    }
                    route.PendingSnapshots.Clear();
                    if (frames.Count > 0)
                        send.Add((route, [.. frames]));
                }
                pending.Clear();
                superseded.Clear();
            }

            var jobs = new List<Task>();
            foreach (var (route, scopes) in watch)
                jobs.Add(WatchRemote(route, scopes));
            foreach (var (route, frames) in send)
                jobs.Add(Task.Run(() => SendTo(route, frames, now)));
            await Task.WhenAll(jobs);
        }
        finally
        {
            Volatile.Write(ref sending, 0);
        }
    }

    private void SendTo(Route route, IReadOnlyList<HexLinkClaimFrame> frames, long now)
    {
        // Heartbeats fit in one small notification. Larger snapshots page by feed and never exceed the wire frame limit.
        var batch = new JsonArray();
        int rows = 0;
        foreach (HexLinkClaimFrame outgoing in frames)
        {
            HexLinkClaimFrame frame = Filter(outgoing);
            int needed = frame.Add.Count + frame.Drop.Count;
            if (batch.Count == 128 || rows > 0 && rows + needed > 512)
            {
                if (!Tell())
                    return;
                batch = [];
                rows = 0;
            }
            batch.Add((JsonNode)HexLinkClaims.Json(frame, now, frame.Origin.Id != services.Device.Id));
            rows += needed;
        }
        if (batch.Count > 0)
            Tell();

        bool Tell() => HexLinkAllowed.Has(route.Peer.Session.Connection.Rights, HexLinkRights.View)
            && route.Peer.Session.Tell("claims/delta", new JsonObject { ["frames"] = batch });
    }

    private async Task WatchRemote(Route route, HexLinkClaimScope[] scopes)
    {
        try
        {
            JsonNode? response = await route.Peer.Session.Ask("claims/watch", new JsonObject { ["folders"] = ScopesJson(scopes) },
                route.Peer.Session.Closing, TimeSpan.FromSeconds(3));
            lock (gate)
            {
                route.Outgoing.IntersectWith(ScopesOf(response as JsonObject, "accepted"));
                route.NeedsSnapshot = false;
            }
        }
        catch (HexLinkException e)
        {
            lock (gate)
            {
                route.Outgoing.Clear();
                if (e.Code == HexLinkErrors.METHOD_NOT_FOUND)
                    route.Retry = long.MaxValue;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (gate)
                route.Asking = false;
        }
    }

    private string? Owner(HexLinkClaimScope scope) => folders.FirstOrDefault(folder => folder.Scope == scope)?.AuthorityId;

    private bool Known(HexLinkClaimScope scope) => Owner(scope) is not null;

    private bool MayExchange(HexLinkClaimScope scope, Route route) =>
        folders.FirstOrDefault(folder => folder.Scope == scope) is { } known
        && (known.PairedWith.Length > 0 ? known.PairedWith.Split('\n').Contains(route.Peer.Device.Id)
            : known.AuthorityId == services.Device.Id || known.AuthorityId == route.Peer.Device.Id);

    // Drop notices must still reach the board when a path becomes hidden after its claim was accepted.
    private HexLinkClaimFrame Filter(HexLinkClaimFrame frame)
    {
        if (frame.Add.Count == 0)
            return frame;
        var visible = frame.Add.Where(row => !Hidden(frame.Scope, row.Draft.Key)).ToArray();
        return visible.Length == frame.Add.Count ? frame : frame with { Add = visible };
    }

    private bool Hidden(HexLinkClaimScope scope, HexLinkClaimKey key) => Hidden(scope, key.Path, key.Prefix);

    private bool Hidden(HexLinkClaimScope scope, string path, bool prefix)
    {
        if (path.Length == 0)
            return false;
        HexLinkFolder? folder = folders.FirstOrDefault(item => item.Scope == scope)?.Folder;
        if (folder?.IncludeSystemFiles != true && HexLinkSystemFiles.Matches(path))
            return true;
        if (folder is not null)
        {
            try
            {
                if (HexLinkIgnore.Matches(HexLinkIgnore.ForRoot(folder.Path).Read(), path, prefix))
                    return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }
        HexExcludes excludes = services.Excludes;
        if (excludes.Hidden(path, prefix))
            return true;
        for (int slash = path.LastIndexOf('/'); slash >= 0; slash = path.LastIndexOf('/', slash - 1))
        {
            if (excludes.Hidden(path[..slash], folder: true))
                return true;
            if (slash == 0)
                break;
        }
        return false;
    }

    private static JsonArray ScopesJson(IEnumerable<HexLinkClaimScope> scopes) =>
        new([.. scopes.Select(scope => (JsonNode)new JsonObject { ["folder"] = scope.Folder, ["identity"] = scope.Identity })]);

    private static HexLinkClaimScope[] ScopesOf(JsonObject? arguments, string key) =>
        [.. HexLinkData.Objects(arguments, key, HexLinkHost.MOST_TREES)
            .Where(scope => HexLinkData.String(scope, "folder", 201) is { Length: > 0 and <= 200 }
                && HexLinkData.String(scope, "identity", 65) is { Length: 64 })
            .Select(scope => new HexLinkClaimScope(HexLinkData.String(scope, "folder", 200)!, HexLinkData.String(scope, "identity", 64)!))];
}

internal sealed partial class HexLinkHost
{
    internal HexLinkClaimExchange? ClaimExchange { get; init; }

    private Task<JsonNode?> ClaimsWatch(JsonObject parameters)
    {
        Need(HexLinkRights.View, "claims/watch");
        return Task.FromResult<JsonNode?>(ClaimExchange?.Watch(session, parameters)
            ?? throw new HexLinkException(HexLinkErrors.METHOD_NOT_FOUND, "This editor has no claim exchange."));
    }

    private bool ClaimsNotice(JsonObject parameters)
    {
        if (ClaimExchange?.Receive(session, parameters) == true)
            ClaimChanged?.Invoke();
        return true;
    }

    internal Action? ClaimChanged { get; init; }
}
