using System.Text.Json.Nodes;

namespace Hex.Core;

internal enum HexLinkClaimKind { Editing, Planned, Private }

internal sealed record HexLinkClaimScope(string Folder, string Identity);

internal sealed record HexLinkClaimKey(string Path, bool Prefix, int? ChatId, HexLinkClaimKind Kind);

internal sealed record HexLinkClaimDraft(HexLinkClaimKey Key, string Holder, string? PlanStep = null, string? PlanState = null,
    long? EtaTick = null);

internal sealed record HexLinkClaim(HexLinkClaimScope Scope, HexLinkDevice Device, HexLinkClaimDraft Draft,
    long SinceTick, long ExpiresTick, string Via)
{
    public string DeviceId => Device.Id;
    public string DeviceName => Device.Name;
    public int? ChatId => Draft.Key.ChatId;
    public string Holder => Draft.Holder;
    public string Folder => Scope.Folder;
    public string Path => Draft.Key.Path;
    public string Wire => Path.Length > 0 ? Folder + "/" + Path : Folder;
    public bool Prefix => Draft.Key.Prefix;
    public HexLinkClaimKind Kind => Draft.Key.Kind;
    public string? PlanStep => Draft.PlanStep;
    public string? PlanState => Draft.PlanState;
    public DateTimeOffset Since => At(SinceTick);
    public DateTimeOffset Expires => At(ExpiresTick);
    public DateTimeOffset? Eta => Draft.EtaTick is { } tick ? At(tick) : null;

    private static DateTimeOffset At(long tick) => DateTimeOffset.UtcNow.AddMilliseconds(tick - Environment.TickCount64);
}

internal sealed record HexLinkClaimRow(HexLinkClaimDraft Draft, long SinceTick);

internal sealed record HexLinkClaimFrame(HexLinkClaimScope Scope, HexLinkDevice Origin, string Epoch, long Sequence,
    bool Snapshot, int Lease, IReadOnlyList<HexLinkClaimRow> Add, IReadOnlyList<HexLinkClaimKey> Drop, bool Heartbeat = false);

internal enum HexLinkClaimReceipt { Unchanged, Changed, Gap, Overflow }

/// <summary>Receives ordered, lease-bound claim feeds. Filesystem edits only query the local path index.</summary>
internal sealed class HexLinkClaims
{
    internal const int DEVICE_LIMIT = 2048, FOLDER_LIMIT = 8192, LEASE_MS = 35_000, GRACE_MS = 15_000, HEARTBEAT_MS = 10_000;
    private const int MAX_AGE_MS = 15 * 24 * 60 * 60 * 1000;

    private sealed class Feed(HexLinkDevice origin, string epoch, string via)
    {
        public readonly HexLinkDevice Origin = origin;
        public readonly string Epoch = epoch, Via = via;
        public long Sequence, Expires;
        public Dictionary<HexLinkClaimKey, HexLinkClaimRow> Rows { get; } = [];
        public HashSet<string> Retired { get; } = new(StringComparer.Ordinal);
    }

    private readonly Lock gate = new();
    private readonly Dictionary<(HexLinkClaimScope Scope, string Origin), Feed> feeds = [];
    private readonly Dictionary<HexLinkClaimScope, Dictionary<string, HexLinkClaim[]>> byPath = [];
    private long revision;
    internal long Revision => Volatile.Read(ref revision);

    public HexLinkClaimReceipt Receive(HexLinkClaimFrame frame, string via, long now)
    {
        lock (gate)
        {
            var key = (frame.Scope, frame.Origin.Id);
            feeds.TryGetValue(key, out Feed? feed);
            if (feed is null && feeds.Count >= (HexLink.MOST + 1) * HexLinkHost.MOST_TREES)
                return HexLinkClaimReceipt.Overflow;
            if (feed is null || feed.Epoch != frame.Epoch)
            {
                if (feed?.Retired.Contains(frame.Epoch) == true || (feed is { } direct && direct.Via == frame.Origin.Id && via != frame.Origin.Id))
                    return HexLinkClaimReceipt.Unchanged;
                if (!frame.Snapshot && frame.Sequence != 1)
                    return HexLinkClaimReceipt.Gap;
            }
            else
            {
                if (feed.Via == frame.Origin.Id && via != frame.Origin.Id)
                    return HexLinkClaimReceipt.Unchanged;
                if (frame.Sequence < feed.Sequence || (frame.Sequence == feed.Sequence && frame.Add.Count + frame.Drop.Count > 0 && !frame.Snapshot))
                    return HexLinkClaimReceipt.Unchanged;
                if (!frame.Snapshot && frame.Sequence > feed.Sequence + 1 || !frame.Snapshot && frame.Sequence > feed.Sequence && frame.Heartbeat)
                    return HexLinkClaimReceipt.Gap;
            }

            bool replace = feed is null || feed.Epoch != frame.Epoch || feed.Via != via;
            if (replace && feed is not null && feed.Via != via && !frame.Snapshot)
                return HexLinkClaimReceipt.Gap;
            bool changed = replace || frame.Snapshot || frame.Add.Count + frame.Drop.Count > 0;
            if (!changed)
            {
                feed!.Expires = now + Math.Clamp(frame.Lease, 0, LEASE_MS);
                return HexLinkClaimReceipt.Unchanged;
            }

            var rows = frame.Snapshot || feed is null || feed.Epoch != frame.Epoch
                ? new Dictionary<HexLinkClaimKey, HexLinkClaimRow>() : new(feed.Rows);
            foreach (HexLinkClaimKey drop in frame.Drop)
                rows.Remove(drop);
            foreach (HexLinkClaimRow add in frame.Add)
                rows[add.Draft.Key] = add;
            int deviceCount = feeds.Where(pair => pair.Key.Origin == key.Id && pair.Key.Scope != frame.Scope).Sum(pair => pair.Value.Rows.Count);
            int folderCount = feeds.Where(pair => pair.Key.Scope == frame.Scope && pair.Key.Origin != key.Id).Sum(pair => pair.Value.Rows.Count);
            if (deviceCount + rows.Count > DEVICE_LIMIT || folderCount + rows.Count > FOLDER_LIMIT)
                return HexLinkClaimReceipt.Overflow;

            if (replace)
            {
                var next = new Feed(frame.Origin, frame.Epoch, via);
                if (feed is not null && feed.Epoch != frame.Epoch)
                {
                    next.Retired.UnionWith(feed.Retired.Take(7));
                    next.Retired.Add(feed.Epoch);
                }
                feed = feeds[key] = next;
            }

            if (changed)
            {
                feed!.Rows.Clear();
                foreach (var row in rows)
                    feed.Rows.Add(row.Key, row.Value);
            }
            feed!.Sequence = frame.Sequence;
            feed.Expires = now + Math.Clamp(frame.Lease, 0, LEASE_MS);
            return changed ? Invalidate(frame.Scope) : HexLinkClaimReceipt.Unchanged;
        }
    }

    public void Disconnect(string via, long now)
    {
        lock (gate)
        {
            foreach (var (key, feed) in feeds)
            {
                if (feed.Via != via)
                    continue;
                feed.Expires = Math.Min(feed.Expires, now + GRACE_MS);
                byPath.Remove(key.Scope);
            }
        }
    }

    public bool Expire(long now)
    {
        lock (gate)
        {
            var expired = feeds.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray();
            foreach (var key in expired)
            {
                feeds.Remove(key);
                byPath.Remove(key.Scope);
            }
            if (expired.Length > 0)
                Interlocked.Increment(ref revision);
            return expired.Length > 0;
        }
    }

    public IReadOnlyList<HexLinkClaim> At(HexLinkClaimScope scope, string path, bool prefix, long now)
    {
        lock (gate)
        {
            if (!byPath.TryGetValue(scope, out var index))
            {
                index = feeds.Where(pair => pair.Key.Scope == scope)
                    .SelectMany(pair => pair.Value.Rows.Values.Select(row => Row(scope, pair.Value, row)))
                    .GroupBy(claim => claim.Path, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
                byPath[scope] = index;
            }

            var matches = new List<HexLinkClaim>();
            if (index.TryGetValue(path, out HexLinkClaim[]? exact))
                matches.AddRange(exact);
            for (string parent = path; parent.Length > 0;)
            {
                int slash = parent.LastIndexOf('/');
                parent = slash < 0 ? string.Empty : parent[..slash];
                if (index.TryGetValue(parent, out var ancestors))
                    matches.AddRange(ancestors.Where(claim => claim.Prefix));
            }
            if (prefix)
            {
                foreach (var (child, rows) in index)
                {
                    if (child != path && Beneath(child, path))
                        matches.AddRange(rows);
                }
            }

            // Renewals do not rebuild the path index. Look up the live feed for the current expiry.
            return [.. matches.Where(claim => feeds[(scope, claim.DeviceId)].Expires > now)
                .Select(claim => claim with { ExpiresTick = feeds[(scope, claim.DeviceId)].Expires })];
        }
    }

    public IReadOnlyList<HexLinkClaim> List(long now)
    {
        lock (gate)
            return [.. feeds.Where(pair => pair.Value.Expires > now)
                .SelectMany(pair => pair.Value.Rows.Values.Select(row => Row(pair.Key.Scope, pair.Value, row)))];
    }

    public IReadOnlyList<HexLinkClaimFrame> Snapshot(HexLinkClaimScope scope, string? via, long now, bool directOnly = false)
    {
        lock (gate)
            return [.. feeds.Where(pair => pair.Key.Scope == scope && pair.Value.Via != via && pair.Value.Expires > now
                    && (!directOnly || pair.Value.Via == pair.Key.Origin))
                .Select(pair => new HexLinkClaimFrame(scope, pair.Value.Origin, pair.Value.Epoch, pair.Value.Sequence, true,
                    (int)Math.Min(LEASE_MS, pair.Value.Expires - now), [.. pair.Value.Rows.Values], []))];
    }

    private HexLinkClaimReceipt Invalidate(HexLinkClaimScope scope)
    {
        byPath.Remove(scope);
        Interlocked.Increment(ref revision);
        return HexLinkClaimReceipt.Changed;
    }

    private static HexLinkClaim Row(HexLinkClaimScope scope, Feed feed, HexLinkClaimRow row) =>
        new(scope, feed.Origin, row.Draft, row.SinceTick, feed.Expires, feed.Via);

    internal static bool Beneath(string path, string prefix) => prefix.Length == 0
        || path.Length > prefix.Length && path[prefix.Length] == '/' && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    internal static bool ValidPath(string path, bool prefix)
    {
        if (path.Length == 0)
            return prefix;
        return path.Length <= HexLinkFiles.MOST_PATH && !path.Contains('\\') && !path.Contains(':') && !path.Any(char.IsControl)
            && path.Split('/').All(part => part.Length > 0 && part is not "." and not ".."
                && !part.TrimEnd('.', ' ').Equals(".git", StringComparison.OrdinalIgnoreCase) && part[^1] is not ('.' or ' '));
    }

    internal static JsonObject Json(HexLinkClaimFrame frame, long now, bool relay) => new()
    {
        ["folder"] = frame.Scope.Folder,
        ["identity"] = frame.Scope.Identity,
        ["origin"] = frame.Origin.Id,
        ["name"] = frame.Origin.Name,
        ["relay"] = relay,
        ["epoch"] = frame.Epoch,
        ["sequence"] = frame.Sequence,
        ["snapshot"] = frame.Snapshot,
        ["heartbeat"] = frame.Heartbeat,
        ["lease"] = frame.Lease,
        ["add"] = new JsonArray([.. frame.Add.Select(row => (JsonNode)WithDraft(KeyJson(row.Draft.Key), row, now))]),
        ["drop"] = new JsonArray([.. frame.Drop.Select(key => (JsonNode)KeyJson(key))])
    };

    internal static HexLinkClaimFrame? Of(JsonObject json, HexLinkDevice peer, long now)
    {
        long? sequence = HexLinkData.Long(json, "sequence");
        int? lease = HexLinkData.Int(json, "lease");
        if (HexLinkData.String(json, "folder", 201) is not { Length: > 0 and <= 200 } folder
            || !ValidPath(folder, false) || folder.Contains('/')
            || HexLinkData.String(json, "identity", 65) is not { Length: 64 } identity || !identity.All(char.IsAsciiHexDigit)
            || HexLinkData.String(json, "epoch", 33) is not { } epoch || !Guid.TryParseExact(epoch, "N", out _)
            || sequence is not > 0 || lease is not (>= 0 and <= LEASE_MS)
            || json["add"] is not JsonArray { Count: <= DEVICE_LIMIT } adds || json["drop"] is not JsonArray { Count: <= DEVICE_LIMIT } drops)
            return null;

        string? origin = HexLinkData.String(json, "origin", 129);
        bool relay = HexLinkData.Bool(json, "relay") == true;
        if (origin is not { Length: > 0 and <= 128 } || origin.Any(char.IsControl) || (!relay && origin != peer.Id) || (relay && origin == peer.Id))
            return null;

        var added = new List<HexLinkClaimRow>(adds.Count);
        var removed = new List<HexLinkClaimKey>(drops.Count);
        foreach (JsonNode? node in adds)
        {
            if (node is not JsonObject item || KeyOf(item) is not { } key)
                return null;
            string holder = HexLinkNames.Clean(HexLinkData.String(item, "holder", 80)) ?? (key.ChatId is { } chat ? $"Chat #{chat}" : peer.Name);
            int age = Math.Clamp(HexLinkData.Int(item, "age") ?? 0, 0, MAX_AGE_MS);
            long? eta = HexLinkData.Int(item, "eta") is >= 0 and <= MAX_AGE_MS and var wait ? now + wait : null;
            string? step = OneLine(HexLinkData.String(item, "step", 120)), state = OneLine(HexLinkData.String(item, "state", 24));
            added.Add(new HexLinkClaimRow(new HexLinkClaimDraft(key, holder, step, state, eta), now - age));
        }
        foreach (JsonNode? node in drops)
        {
            if (node is not JsonObject item || KeyOf(item) is not { } key)
                return null;
            removed.Add(key);
        }

        HexLinkDevice device = relay ? new HexLinkDevice(origin, HexLinkNames.Clean(HexLinkData.String(json, "name", 80)) ?? origin) : peer;
        return new HexLinkClaimFrame(new HexLinkClaimScope(folder, identity), device, epoch, sequence.Value, HexLinkData.Bool(json, "snapshot") == true,
            lease.Value, added, removed, HexLinkData.Bool(json, "heartbeat") == true);
    }

    private static JsonObject KeyJson(HexLinkClaimKey key) => new()
    {
        ["path"] = key.Path, ["prefix"] = key.Prefix, ["chat"] = key.ChatId, ["kind"] = (int)key.Kind
    };

    private static HexLinkClaimKey? KeyOf(JsonObject item)
    {
        string? path = HexLinkData.String(item, "path", HexLinkFiles.MOST_PATH + 1);
        bool prefix = HexLinkData.Bool(item, "prefix") == true;
        int? chat = HexLinkData.Int(item, "chat"), kind = HexLinkData.Int(item, "kind");
        if (path is null || !ValidPath(path, prefix) || chat is <= 0 || kind is null or < 0 or > 2)
            return null;
        return new HexLinkClaimKey(path, prefix, chat, (HexLinkClaimKind)kind.Value);
    }

    private static string? OneLine(string? text) => text?.ReplaceLineEndings(" ").Trim();

    private static JsonObject WithDraft(JsonObject json, HexLinkClaimRow row, long now)
    {
        json["holder"] = row.Draft.Holder;
        json["step"] = row.Draft.PlanStep;
        json["state"] = row.Draft.PlanState;
        json["age"] = Math.Clamp(now - row.SinceTick, 0, MAX_AGE_MS);
        json["eta"] = row.Draft.EtaTick is { } eta ? Math.Clamp(eta - now, 0, MAX_AGE_MS) : null;
        return json;
    }
}
