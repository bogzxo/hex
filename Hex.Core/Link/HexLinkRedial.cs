using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hex.Core;

public enum HexLinkRedialStatus { Connecting, Retrying }

public sealed record HexLinkRedialState(HexLinkRedialStatus Status, DateTime? RetryAtUtc, string? LastFailure);

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

    private const int MOST_ENDPOINTS = 16, MOST_KEPT_ENDPOINTS = 32;
    private const string ENDPOINTS_FILE = "redial-addresses.json";
    private readonly Dictionary<string, (DateTime Due, TimeSpan Gap, string? Failure)> devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HexLinkEndpoint[]> addresses = new(StringComparer.OrdinalIgnoreCase);
    private string? addressStore;

    /// <summary>Whether a device is to be dialled now, true for one never tried.</summary>
    public bool Due(string id, DateTime now) => !devices.TryGetValue(id, out var state) || now >= state.Due;

    /// <summary>When a device is next due, null for at once.</summary>
    public DateTime? Next(string id) => devices.TryGetValue(id, out var state) ? state.Due : null;

    /// <summary>A dial that failed, the next one put off by the wait, which doubles for the one after.</summary>
    public void Failed(string id, DateTime now, string? reason = null)
    {
        TimeSpan gap = devices.TryGetValue(id, out var state) ? state.Gap : FIRST;
        devices[id] = (now + gap, gap * 2 > MOST ? MOST : gap * 2, reason);
    }

    /// <summary>A device to be tried at once, heard on the LAN or linked, the waits started over.</summary>
    public void Soon(string id) => devices.Remove(id);

    /// <summary>Every device's waits forgotten, Link turned off.</summary>
    public void Clear()
    {
        devices.Clear();
        addresses.Clear();
        addressStore = null;
    }

    public HexLinkRedialState? State(string id, bool dialling)
    {
        bool waited = devices.TryGetValue(id, out var state);
        return dialling ? new HexLinkRedialState(HexLinkRedialStatus.Connecting, null, state.Failure)
            : waited ? new HexLinkRedialState(HexLinkRedialStatus.Retrying, state.Due, state.Failure) : null;
    }

    public IReadOnlyList<HexLinkEndpoint> Addresses(string id) => addresses.GetValueOrDefault(id) ?? [];

    public void Remember(string id, IReadOnlyList<HexLinkEndpoint> seen, HexLinkEndpoint? reached = null)
    {
        if (seen.Count == 0 && reached is null)
            return;
        IReadOnlyList<HexLinkEndpoint> was = Addresses(id);
        IEnumerable<HexLinkEndpoint> latest = reached is { } direct ? seen.Prepend(direct) : seen;
        HexLinkEndpoint[] next = [.. latest.Concat(was).Distinct().Take(MOST_KEPT_ENDPOINTS)];
        if (next.SequenceEqual(was))
            return;
        addresses[id] = next;
        Save();
    }

    public void Forget(string id)
    {
        Soon(id);
        if (addresses.Remove(id))
            Save();
    }

    public void Open(string folder, IReadOnlyCollection<string> trusted)
    {
        addressStore = Path.Combine(folder, ENDPOINTS_FILE);
        addresses.Clear();
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
