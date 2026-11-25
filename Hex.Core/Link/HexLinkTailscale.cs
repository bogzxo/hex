using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Hex.Core;

/// <summary>A machine on the tailnet that is up now, as <c>tailscale status</c> says of it.</summary>
/// <param name="Name">Its host name, what the machine calls itself.</param>
/// <param name="DnsName">Its MagicDNS name with the trailing dot taken off, null when the tailnet has none for it.</param>
/// <param name="Addresses">Its tailnet addresses, the IPv4 one first.</param>
/// <param name="Os">The system it says it runs, null when it doesn't say.</param>
internal sealed record HexLinkTailnetPeer(string Name, string? DnsName, IReadOnlyList<IPAddress> Addresses, string? Os);

/// <summary>
/// Hex Link's way of finding the other Hexes on a tailnet (docs/hex-link-plan.md, 3.5). mDNS is multicast and Tailscale is a
/// layer-3 overlay that carries none, so a Hex on the other side of one never shows up under Nearby on its own. What does know
/// every machine of the tailnet is Tailscale itself: <c>tailscale status --json</c> lists the peers and their addresses, and
/// a plain TCP connect to Link's port says which of them are listening. That is all this does, there is no new package and no
/// account or key of Tailscale's touched, only its own command line asked what it already shows. No Tailscale installed (the
/// command line isn't found) makes everything here do nothing.
/// <para>
/// The probe sends nothing, not even a TLS hello: the other Hex sees a connection that closed before it said a word, which
/// it takes for no request at all and doesn't audit. A machine that accepts is a candidate for the Nearby list, and joining
/// it goes through the same pairing, fingerprint and all, as any address typed in.
/// </para>
/// </summary>
internal static class HexLinkTailscale
{
    // How long the command line has to answer, and how long a peer has to accept a connection
    private const int STATUS_MS = 5000;
    private const int PROBE_MS = 1500;

    // How many peers are asked at once
    private const int PROBES_AT_ONCE = 8;

    // How many peers are looked at at most, a tailnet of thousands isn't a list to show
    private const int MOST_PEERS = 256;

    /// <summary>
    /// Where Tailscale's command line is, null when it isn't installed. The places are the ones each system's installer puts it in
    /// (on a Mac the App Store and the standalone app both keep it inside the bundle), then the PATH. Everything it needs of
    /// the machine is passed in so it can be told what a machine looks like.
    /// </summary>
    /// <param name="os">The system: "windows", "macos" or anything else for the Unix ones.</param>
    /// <param name="environment">Looks up an environment variable, null when it isn't set.</param>
    /// <param name="exists">Whether a file is there.</param>
    internal static string? FindCli(string os, Func<string, string?> environment, Func<string, bool> exists)
    {
        var places = new List<string>();
        string exe = "tailscale";
        if (os == "windows")
        {
            exe = "tailscale.exe";
            foreach (string variable in new[] { "ProgramFiles", "ProgramFiles(x86)" })
                if (environment(variable) is { Length: > 0 } root)
                    places.Add(Path.Combine(root, "Tailscale", exe));
        }
        else if (os == "macos")
        {
            places.Add("/Applications/Tailscale.app/Contents/MacOS/Tailscale");
            places.Add("/usr/local/bin/tailscale");
            places.Add("/opt/homebrew/bin/tailscale");
        }
        else
        {
            places.Add("/usr/bin/tailscale");
            places.Add("/usr/sbin/tailscale");
            places.Add("/usr/local/bin/tailscale");
        }

        foreach (string place in places)
            if (exists(place))
                return place;

        char separator = os == "windows" ? ';' : ':';
        foreach (string folder in (environment("PATH") ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(folder.Trim('"'), exe);
                if (exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // A PATH entry that isn't a path, the next
            }
        }

        return null;
    }

    /// <summary>Where Tailscale's command line is on this machine, null when it isn't installed.</summary>
    public static string? FindCli() => FindCli(
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "unix",
        Environment.GetEnvironmentVariable, File.Exists);

    /// <summary>
    /// What <c>tailscale status --json</c> says, null when it can't be run or doesn't answer in time. The process is run
    /// without a shell or a window and killed when it's late or <paramref name="cancel"/> is.
    /// </summary>
    public static async Task<string?> Status(string cli, CancellationToken cancel)
    {
        try
        {
            var start = new ProcessStartInfo(cli)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("status");
            start.ArgumentList.Add("--json");

            using var process = Process.Start(start);
            if (process is null)
                return null;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(STATUS_MS);
            try
            {
                Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
                string text = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                _ = errors.ContinueWith(static task => task.Exception, TaskScheduler.Default);
                return process.ExitCode == 0 ? text : null;
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // It had already gone
                }

                return null;
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The peers that are up in what <c>tailscale status --json</c> printed: nothing unless the backend is <c>Running</c>,
    /// and then each of "Peer" that is online and has an address, the IPv4 one first. This machine ("Self") is never among
    /// them. A field that's missing, or text that isn't what Tailscale prints, makes less or nothing, never an exception.
    /// </summary>
    internal static List<HexLinkTailnetPeer> ParseStatus(string? json)
    {
        var peers = new List<HexLinkTailnetPeer>();
        if (string.IsNullOrWhiteSpace(json))
            return peers;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || StringOf(root, "BackendState") != "Running"
                || !root.TryGetProperty("Peer", out JsonElement all)
                || all.ValueKind != JsonValueKind.Object)
                return peers;

            foreach (JsonProperty entry in all.EnumerateObject())
            {
                JsonElement peer = entry.Value;
                if (peers.Count >= MOST_PEERS || peer.ValueKind != JsonValueKind.Object)
                    continue;
                if (!peer.TryGetProperty("Online", out JsonElement online) || online.ValueKind != JsonValueKind.True)
                    continue;

                var addresses = new List<IPAddress>();
                if (peer.TryGetProperty("TailscaleIPs", out JsonElement ips) && ips.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement ip in ips.EnumerateArray())
                        if (ip.ValueKind == JsonValueKind.String && IPAddress.TryParse(ip.GetString(), out IPAddress? address))
                            addresses.Add(address);
                }

                if (addresses.Count == 0)
                    continue;

                string? dns = StringOf(peer, "DNSName")?.TrimEnd('.');
                string name = StringOf(peer, "HostName") is { Length: > 0 } host ? host : dns is { Length: > 0 } ? dns.Split('.')[0] : "";
                if (name.Length == 0)
                    continue;
                peers.Add(new HexLinkTailnetPeer(name, dns is { Length: > 0 } ? dns : null,
                    [.. addresses.OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)],
                    StringOf(peer, "OS") is { Length: > 0 } os ? os : null));
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        {
            return [];
        }

        return peers;
    }

    private static string? StringOf(JsonElement from, string property)
        => from.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// Whether something listens at <paramref name="address"/> on <paramref name="port"/>: a TCP connect that's closed the
    /// moment it's made, with nothing sent. False when it's refused, late, or can't be made.
    /// </summary>
    public static async Task<bool> Listens(IPAddress address, int port, CancellationToken cancel)
    {
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(PROBE_MS);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token);
            return true;
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>The peers that listen on <paramref name="port"/> (each at its first address), the others dropped, at most a few asked at once.</summary>
    public static async Task<List<HexLinkTailnetPeer>> Probe(IReadOnlyList<HexLinkTailnetPeer> peers, int port, CancellationToken cancel)
    {
        using var gate = new SemaphoreSlim(PROBES_AT_ONCE);
        bool[] listening = await Task.WhenAll(peers.Select(async peer =>
        {
            try
            {
                await gate.WaitAsync(cancel);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            try
            {
                return await Listens(peer.Addresses[0], port, cancel);
            }
            finally
            {
                gate.Release();
            }
        }));
        return [.. peers.Where((_, at) => listening[at])];
    }
}
