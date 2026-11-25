using System.Net;

namespace Hex.Dawn;

// Hexes on a tailnet in the Nearby list. mDNS is multicast and Tailscale carries none, so two Hexes on one tailnet never
// find each other the way two on a LAN do, and the address had to be typed in. HexLinkTailscale asks Tailscale's own
// command line who is on the tailnet and which of them listen on Link's port; what that finds is shown beside what mDNS
// found, as a candidate with no device id (an unpaired probe can't know it). A known device is told by its address, an
// unknown one gets Add, which is the pairing every typed address goes through, fingerprint and all.
internal sealed partial class DawnScene
{
    private HexLinkTailnetBrowser? linkTailnet;

    // Whether the tailnet's answer is wanted now (the Devices window is open, or a paired device isn't linked), set on the
    // editor's thread each second and read by the browser's own
    private volatile bool linkTailnetWanted;

    private void LinkTailnetLook() =>
        linkTailnetWanted = linkWindow is not null || link is not null && LinkKnown.Any(known => link.PeerOf(known.Device.Id) is null);

    // Looking at the tailnet starts with looking at the LAN, nothing happens where Tailscale isn't installed
    private void LinkTailnetStart()
    {
        if (linkTailnet is not null)
            return;
        var tailnet = new HexLinkTailnetBrowser { Changed = () => PostLink(LinkTailnetMoved), Wanted = () => linkTailnetWanted };
        if (tailnet.Start(HexSettings.DefaultLinkPort))
            linkTailnet = tailnet;
        else
            tailnet.Dispose();
    }

    // What the tailnet has moved: the list is drawn again, and a paired device that isn't linked and has just turned up there
    // (a Tailscale that reconnected, a machine woken) is dialled now rather than when its wait runs out, as LinkNearbyMoved
    // does for one heard on the LAN
    private void LinkTailnetMoved()
    {
        linkDirty = true;
        if (linkTailnet is not { } tailnet || link is null)
            return;
        foreach (HexLinkNearby heard in tailnet.Found)
        {
            if (LinkKnownOf(heard) is { } known && link.PeerOf(known.Device.Id) is null && !linkHeldOff.Contains(known.Device.Id))
                linkRedial.Soon(known.Device.Id);
        }
    }

    // And ends with it
    private void LinkTailnetStop()
    {
        linkTailnet?.Dispose();
        linkTailnet = null;
    }

    /// <summary>The Hexes heard on the LAN and the ones found on the tailnet, one of the tailnet's dropped when mDNS has an address of it already.</summary>
    private IReadOnlyList<HexLinkNearby> LinkNearbyAll()
    {
        IReadOnlyList<HexLinkNearby> lan = linkDiscovery?.Found ?? [];
        IReadOnlyList<HexLinkNearby> tailnet = linkTailnet?.Found ?? [];
        if (tailnet.Count == 0)
            return lan;

        var known = new HashSet<string>(lan.SelectMany(heard => heard.Addresses).Select(HexLinkAddresses.Show));
        return [.. lan, .. tailnet.Where(heard => !heard.Addresses.Any(address => known.Contains(HexLinkAddresses.Show(address))))];
    }

    /// <summary>Whether a candidate came from the tailnet, which has no device id to tell it by.</summary>
    internal static bool LinkIsTailnet(HexLinkNearby heard) => heard.Id8.Length == 0;

    /// <summary>
    /// The device this one has paired with that a candidate is: by the start of its id when mDNS heard it, and by address
    /// when it came from the tailnet (the address it was last linked at or dialled at, or any it has been dialled at since).
    /// </summary>
    private HexLinkKnown? LinkKnownOf(HexLinkNearby heard)
    {
        if (!LinkIsTailnet(heard))
            return heard.Id8.Length >= 8 ? LinkKnown.FirstOrDefault(each => each.Device.Id.StartsWith(heard.Id8, StringComparison.OrdinalIgnoreCase)) : null;

        var addresses = new HashSet<string>(heard.Addresses.Select(HexLinkAddresses.Show), StringComparer.OrdinalIgnoreCase);
        foreach (HexLinkKnown each in LinkKnown)
        {
            if (each.Address.Length > 0 && addresses.Contains(each.Address))
                return each;
            if (linkRedial.Addresses(each.Device.Id).Any(endpoint => addresses.Contains(endpoint.Host)))
                return each;
        }

        return null;
    }
}

/// <summary>
/// The Hexes found on the tailnet, kept like <see cref="HexLinkDiscovery"/> keeps those on the LAN: <see cref="Found"/> is what's
/// there, <see cref="Changed"/> says it moved. While it's started it asks Tailscale who is on the tailnet and which of them
/// listen on Link's port at once, then every minute, and on <see cref="Refresh"/>. All of it runs off the caller's thread. It
/// lives here and not beside <see cref="HexLinkTailscale"/> because <see cref="HexLinkNearby"/> and the transport are in a
/// project of their own that Hex.Core doesn't see.
/// </summary>
internal sealed class HexLinkTailnetBrowser : IDisposable
{
    private const int EVERY_MS = 60000, IDLE_MS = 10 * 60000;

    /// <summary>Whether anybody needs the tailnet's answer now; without a yes it's asked every ten minutes and not every one.</summary>
    public Func<bool>? Wanted { get; set; }

    private readonly Lock gate = new();
    private readonly string? cli;
    private readonly Func<string, CancellationToken, Task<string?>> status;
    private readonly Func<IReadOnlyList<HexLinkTailnetPeer>, int, CancellationToken, Task<List<HexLinkTailnetPeer>>> probe;
    private List<HexLinkNearby> found = [];
    private CancellationTokenSource? running;
    private int port = HexSettings.DefaultLinkPort;

    public HexLinkTailnetBrowser(string? cli = null, Func<string, CancellationToken, Task<string?>>? status = null,
        Func<IReadOnlyList<HexLinkTailnetPeer>, int, CancellationToken, Task<List<HexLinkTailnetPeer>>>? probe = null)
    {
        this.cli = cli ?? HexLinkTailscale.FindCli();
        this.status = status ?? HexLinkTailscale.Status;
        this.probe = probe ?? HexLinkTailscale.Probe;
    }

    /// <summary>What was found changed, on a thread of the pool.</summary>
    public Action? Changed { get; set; }

    /// <summary>The Hexes found on the tailnet, by name.</summary>
    public IReadOnlyList<HexLinkNearby> Found
    {
        get
        {
            lock (gate)
                return found;
        }
    }

    /// <summary>Whether Tailscale's command line was found, without which there is nothing to look at.</summary>
    public bool Available => cli is not null;

    /// <summary>Starts looking on <paramref name="port"/>. False when Tailscale isn't installed or this is started already.</summary>
    public bool Start(int port)
    {
        if (cli is null)
            return false;
        lock (gate)
        {
            if (running is not null)
                return false;
            this.port = port;
            running = new CancellationTokenSource();
            CancellationToken stop = running.Token;
            _ = Task.Run(() => Loop(stop), stop);
            return true;
        }
    }

    /// <summary>Looks again now rather than when the minute is up, for a person who has just started Tailscale or the other Hex.</summary>
    public void Refresh()
    {
        CancellationToken stop;
        lock (gate)
        {
            if (running is null)
                return;
            stop = running.Token;
        }

        _ = Task.Run(() => Look(stop), stop);
    }

    /// <summary>Stops looking and forgets what was found, saying so when that was something.</summary>
    public void Stop()
    {
        CancellationTokenSource? was;
        bool had;
        lock (gate)
        {
            was = running;
            running = null;
            had = found.Count > 0;
            found = [];
        }

        if (was is null)
            return;
        was.Cancel();
        if (had)
            Raise();
    }

    public void Dispose() => Stop();

    private async Task Loop(CancellationToken stop)
    {
        try
        {
            // A minute while somebody needs the answer (the Devices window is open, or a paired device isn't linked), ten
            // minutes otherwise, and at once on a network change (Refresh) or when it's started; checked every half minute
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(EVERY_MS / 2));
            await Look(stop);
            long lookedAt = Environment.TickCount64;
            while (await timer.WaitForNextTickAsync(stop))
            {
                long idleFor = Environment.TickCount64 - lookedAt;
                if (idleFor < EVERY_MS - 1000 || idleFor < IDLE_MS - 1000 && Wanted?.Invoke() != true)
                    continue;
                await Look(stop);
                lookedAt = Environment.TickCount64;
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped
        }
    }

    // One look: who is on the tailnet and who of them listens. A look that fails leaves what was found as it was.
    private async Task Look(CancellationToken stop)
    {
        try
        {
            if (cli is null)
                return;
            string? text = await status(cli, stop);
            if (text is null)
                return;
            List<HexLinkTailnetPeer> up = await probe(HexLinkTailscale.ParseStatus(text), port, stop);
            DateTime when = DateTime.UtcNow;
            List<HexLinkNearby> next = [.. up
                .Select(peer => new HexLinkNearby(NameOf(peer), "", peer.Addresses, port, when))
                .OrderBy(nearby => nearby.Name, StringComparer.OrdinalIgnoreCase)];

            lock (gate)
            {
                if (stop.IsCancellationRequested || running is null || Same(found, next))
                    return;
                found = next;
            }

            Raise();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Tailscale is a convenience, a look that went wrong is the next one's to put right
        }
    }

    // The short MagicDNS name (the host label of it), or the host name
    private static string NameOf(HexLinkTailnetPeer peer) => peer.DnsName is { Length: > 0 } dns ? dns.Split('.')[0] : peer.Name;

    private static bool Same(List<HexLinkNearby> a, List<HexLinkNearby> b)
        => a.Count == b.Count && a.Zip(b).All(pair => pair.First.Name == pair.Second.Name && pair.First.Port == pair.Second.Port
            && pair.First.Addresses.SequenceEqual(pair.Second.Addresses));

    private void Raise()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // A callback that threw is its own to answer for
        }
    }
}
