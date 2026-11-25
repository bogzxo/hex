using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hex.Core;

public enum HexLinkRedialStatus { Connecting, Retrying }

/// <summary>
/// Where a device's redials are. <c>LastFailure</c> is the one-word reason (unreachable, timeout, refused, pin...) the row
/// recognises, <c>Detail</c> what the failure said in full, <c>Route</c> the address last being tried, and
/// <c>Reconnecting</c> whether the device was linked this session and dropped, so it's being got back and not just offline.
/// </summary>
public sealed record HexLinkRedialState(HexLinkRedialStatus Status, DateTime? RetryAtUtc, string? LastFailure,
    string? Detail = null, string? Route = null, bool Reconnecting = false)
{
    /// <summary>
    /// Whether a row says a device is reconnecting: it dropped, and a dial is on or waited for. Not when it was held off
    /// by hand, nor once the last dial was answered with a refusal that waiting won't mend (the other end has forgotten
    /// this one, speaks another version, or sits behind another route), which the row says as it is.
    /// </summary>
    public static bool IsReconnecting(HexLinkRedialState? state, bool heldOff) =>
        state is { Reconnecting: true, LastFailure: not ("version" or "refused" or "pin" or "unknown" or "route") } && !heldOff;
}

internal readonly record struct HexLinkEndpoint(string Host, int Port);

/// <summary>
/// When a known device that isn't linked is dialled again. The first try is at once, a failed one waits five seconds,
/// then twice that each time up to a minute, so a machine that's asleep or gone home isn't hammered and one that wakes is
/// found within the minute. A device heard on the LAN again (mDNS) is tried at once whatever it was waiting for, and a
/// link made puts it back to the start. Only the editor's thread uses it.
/// </summary>
internal sealed class HexLinkRedial
{
    /// <summary>The wait after the first failure, and the longest a wait gets.</summary>
    public static readonly TimeSpan FIRST = TimeSpan.FromSeconds(5), MOST = TimeSpan.FromSeconds(60);

    /// <summary>How long a redial's TCP connect may take, so a dead address among the remembered ones costs this and not the handshake's whole ten seconds.</summary>
    public static readonly TimeSpan CONNECT = TimeSpan.FromSeconds(3);

    /// <summary>How long the editor's clock may jump between two looks at it before it's taken as the machine having slept.</summary>
    public static readonly TimeSpan SLEPT = TimeSpan.FromSeconds(30);

    internal const int MOST_KEPT_ENDPOINTS = 16;
    private const int MOST_ENDPOINTS = 16;
    private const string ENDPOINTS_FILE = "redial-addresses.json", LAST_LINKED = "last_linked";
    private readonly Dictionary<string, (DateTime Due, TimeSpan Gap, string? Failure, string? Detail)> devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HexLinkEndpoint[]> addresses = new(StringComparer.OrdinalIgnoreCase);

    // The endpoint each device last linked through, tried before the rest whatever kind of address it is
    private readonly Dictionary<string, HexLinkEndpoint> linked = new(StringComparer.OrdinalIgnoreCase);

    // The devices that were linked this session and dropped, and the address a dial of each is on
    private readonly HashSet<string> dropped = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> routes = new(StringComparer.OrdinalIgnoreCase);
    private string? addressStore;

    /// <summary>Whether a device is to be dialled now, true for one never tried.</summary>
    public bool Due(string id, DateTime now) => !devices.TryGetValue(id, out var state) || now >= state.Due;

    /// <summary>When a device is next due, null for at once.</summary>
    public DateTime? Next(string id) => devices.TryGetValue(id, out var state) ? state.Due : null;

    /// <summary>A dial that failed, the next one put off by the wait, which doubles for the one after.</summary>
    public void Failed(string id, DateTime now, string? reason = null, string? detail = null)
    {
        TimeSpan gap = devices.TryGetValue(id, out var state) ? state.Gap : FIRST;
        devices[id] = (now + gap, gap * 2 > MOST ? MOST : gap * 2, reason, detail);
    }

    /// <summary>A device to be tried at once, heard on the LAN or linked, the waits started over.</summary>
    public void Soon(string id) => devices.Remove(id);

    /// <summary>A link that was up gone: the device is to be tried at once, and is shown as being reconnected until it links again or is given up on.</summary>
    public void Dropped(string id)
    {
        Soon(id);
        dropped.Add(id);
    }

    /// <summary>The address a dial of a device is trying now, for the row to say.</summary>
    public void Trying(string id, HexLinkEndpoint endpoint) => routes[id] = $"{endpoint.Host}:{endpoint.Port}";

    /// <summary>Every device's waits forgotten, Link turned off.</summary>
    public void Clear()
    {
        devices.Clear();
        addresses.Clear();
        linked.Clear();
        dropped.Clear();
        routes.Clear();
        addressStore = null;
    }

    public HexLinkRedialState? State(string id, bool dialling)
    {
        bool waited = devices.TryGetValue(id, out var state);
        bool reconnecting = dropped.Contains(id);
        string? route = routes.GetValueOrDefault(id);
        return dialling ? new HexLinkRedialState(HexLinkRedialStatus.Connecting, null, state.Failure, state.Detail, route, reconnecting)
            : waited ? new HexLinkRedialState(HexLinkRedialStatus.Retrying, state.Due, state.Failure, state.Detail, route, reconnecting)
            : reconnecting ? new HexLinkRedialState(HexLinkRedialStatus.Retrying, null, null, null, route, true) : null;
    }

    /// <summary>The endpoint a device last linked through, the one to try first.</summary>
    public HexLinkEndpoint? LastLinked(string id) => linked.TryGetValue(id, out var endpoint) ? endpoint : null;

    /// <summary>A link made through an endpoint (dialled, or the one the device dialled from): tried first from now on, the waits and the dropped mark gone.</summary>
    public void Linked(string id, HexLinkEndpoint endpoint)
    {
        Soon(id);
        dropped.Remove(id);
        routes.Remove(id);
        if (linked.TryGetValue(id, out var was) && was == endpoint)
            return;
        linked[id] = endpoint;
        Save();
    }

    public IReadOnlyList<HexLinkEndpoint> Addresses(string id) => addresses.GetValueOrDefault(id) ?? [];

    public void Remember(string id, IReadOnlyList<HexLinkEndpoint> seen, HexLinkEndpoint? reached = null)
    {
        if (seen.Count == 0 && reached is null)
            return;
        IReadOnlyList<HexLinkEndpoint> was = Addresses(id);
        IEnumerable<HexLinkEndpoint> latest = reached is { } direct ? seen.Prepend(direct) : seen;

        // What last linked leads, then what was just seen or reached, then the old ones; only so many are kept, so the
        // oldest go (a LAN address from another network, a VPN one that changed) rather than each costing a dial for ever
        if (LastLinked(id) is { } worked)
            latest = latest.Prepend(worked);
        HexLinkEndpoint[] next = [.. latest.Concat(was).Distinct().Take(MOST_KEPT_ENDPOINTS)];
        if (next.SequenceEqual(was))
            return;
        addresses[id] = next;
        Save();
    }

    public void Forget(string id)
    {
        Soon(id);
        dropped.Remove(id);
        routes.Remove(id);
        bool had = addresses.Remove(id);
        if (linked.Remove(id) || had)
            Save();
    }

    public void Open(string folder, IReadOnlyCollection<string> trusted)
    {
        addressStore = Path.Combine(folder, ENDPOINTS_FILE);
        addresses.Clear();
        linked.Clear();
        try
        {
            if (new FileInfo(addressStore) is not { Exists: true, Length: > 0 and <= 1024 * 1024 }
                || JsonNode.Parse(File.ReadAllText(addressStore)) is not JsonObject stored)
                return;
            foreach (string id in trusted)
            {
                if (stored[id] is JsonArray list)
                    addresses[id] = [.. list.OfType<JsonObject>().Take(MOST_KEPT_ENDPOINTS)
                        .Select(each => Endpoint(HexLinkData.String(each, "host", 100), HexLinkData.Int(each, "port") ?? 0))
                        .OfType<HexLinkEndpoint>().Distinct()];
                if (stored[LAST_LINKED] is JsonObject worked && worked[id] is JsonObject last
                    && Endpoint(HexLinkData.String(last, "host", 100), HexLinkData.Int(last, "port") ?? 0) is { } endpoint)
                    linked[id] = endpoint;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.WriteLine($"[Hex] Link couldn't read remembered addresses, {e.Message}");
        }
    }

    private void Save()
    {
        if (addressStore is null)
            return;
        var stored = new JsonObject();
        foreach (var (id, endpoints) in addresses)
            stored[id] = new JsonArray([.. endpoints.Select(endpoint => (JsonNode)new JsonObject { ["host"] = endpoint.Host, ["port"] = endpoint.Port })]);
        var worked = new JsonObject();
        foreach (var (id, endpoint) in linked)
            worked[id] = new JsonObject { ["host"] = endpoint.Host, ["port"] = endpoint.Port };
        if (worked.Count > 0)
            stored[LAST_LINKED] = worked;
        try
        {
            HexLinkIdentity.WriteUserOnly(addressStore, Encoding.UTF8.GetBytes(stored.ToJsonString()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[Hex] Link couldn't keep remembered addresses, {e.Message}");
        }
    }

    public static JsonObject Advertisement(int port, IReadOnlyList<string> local) => new()
    {
        ["listen_port"] = port,
        ["addresses"] = new JsonArray([.. local.Take(MOST_ENDPOINTS).Select(host => (JsonNode)host)])
    };

    public static HexLinkEndpoint[] AdvertisedBy(JsonObject said, string observed)
    {
        int port = HexLinkData.Int(said, "listen_port") ?? 0;
        bool allowLoopback = IPAddress.TryParse(observed, out IPAddress? remote) && IPAddress.IsLoopback(remote);
        return [.. HexLinkData.Strings(said, "addresses", MOST_ENDPOINTS, 100).Prepend(observed)
            .Select(host => Endpoint(host, port)).OfType<HexLinkEndpoint>().Distinct()
            .Where(endpoint => allowLoopback || !IPAddress.IsLoopback(IPAddress.Parse(endpoint.Host)))];
    }

    internal static HexLinkEndpoint? Endpoint(string? host, int port) =>
        port is > 0 and <= 65535 && IPAddress.TryParse(host, out var address) && HexLinkAddresses.IsPrivate(address)
            ? new HexLinkEndpoint(HexLinkAddresses.Show(address), port) : null;

    public static IReadOnlyList<string> LocalAddresses(bool loopbackOnly)
    {
        if (loopbackOnly)
            return ["127.0.0.1"];
        try
        {
            return [.. HexLinkAddresses.Preferred(NetworkInterface.GetAllNetworkInterfaces()
                .Where(face => face.OperationalStatus == OperationalStatus.Up)
                .SelectMany(face => face.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address)
                .Where(address => !IPAddress.IsLoopback(address) && !address.IsIPv6LinkLocal))
                .Take(MOST_ENDPOINTS).Select(HexLinkAddresses.Show)];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }
}
