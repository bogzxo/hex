using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Hex.Core;

public interface IHexRouterPortMapper
{
    Task<IHexRouterPortLease?> TryMapAsync(int localTcpPort, CancellationToken cancellationToken = default);
}

public interface IHexRouterPortLease : IAsyncDisposable
{
    IPEndPoint PublicEndPoint { get; }
    DateTimeOffset ExpiresAt { get; }
    Task<bool> RenewAsync(CancellationToken cancellationToken = default);
}

/// <summary>Maps an already listening Hex Link TCP port on the IPv4 default gateway.</summary>
public sealed partial class HexRouterPortMapper : IHexRouterPortMapper
{
    private const uint Lifetime = 900;
    private const uint MaximumLifetime = 3600;
    private readonly Func<RouterRoute?> route;
    private readonly TimeSpan timeout;

    public HexRouterPortMapper() : this(DefaultRoute, TimeSpan.FromSeconds(2)) { }

    internal HexRouterPortMapper(Func<RouterRoute?> route, TimeSpan timeout)
    {
        this.route = route;
        this.timeout = timeout;
    }

    internal sealed record RouterRoute(IPAddress Local, IPEndPoint Control, IPEndPoint Discovery);
    private sealed record Grant(IPAddress Address, int Port, DateTimeOffset ExpiresAt);

    private interface IMapping
    {
        Task<Grant?> MapAsync(CancellationToken cancellationToken);
        Task DeleteAsync();
    }

    public async Task<IHexRouterPortLease?> TryMapAsync(int localTcpPort, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(localTcpPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(localTcpPort, ushort.MaxValue);
        cancellationToken.ThrowIfCancellationRequested();

        var gateway = route();
        cancellationToken.ThrowIfCancellationRequested();
        if (gateway is null)
            return null;

        foreach (IMapping mapping in new IMapping[] {
            new PcpMapping(gateway, localTcpPort, timeout), new NatPmpMapping(gateway, localTcpPort, timeout),
            new UpnpMapping(gateway, localTcpPort, timeout) })
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Wait out the bounded request even if consent is withdrawn while a lease is being allocated.
            var grant = await mapping.MapAsync(cancellationToken).ConfigureAwait(false);
            if (grant is not null)
            {
                if (!cancellationToken.IsCancellationRequested)
                    return new Lease(mapping, grant);
                await mapping.DeleteAsync().ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        return null;
    }

    private sealed class Lease(IMapping mapping, Grant initial) : IHexRouterPortLease
    {
        private readonly SemaphoreSlim operations = new(1);
        private volatile Grant grant = initial;
        private bool disposed;

        public IPEndPoint PublicEndPoint
        {
            get
            {
                var current = grant;
                // IPEndPoint is mutable, so keep the stored grant independent of what a caller is handed.
                return new(new IPAddress(current.Address.GetAddressBytes()), current.Port);
            }
        }

        public DateTimeOffset ExpiresAt => grant.ExpiresAt;

        public async Task<bool> RenewAsync(CancellationToken cancellationToken = default)
        {
            await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (disposed)
                    return false;
                var renewed = await mapping.MapAsync(cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    disposed = true;
                    await mapping.DeleteAsync().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (renewed is null)
                    return false;
                grant = renewed;
                return true;
            }
            finally
            {
                operations.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await operations.WaitAsync().ConfigureAwait(false);
            try
            {
                if (disposed)
                    return;
                disposed = true;
                await mapping.DeleteAsync().ConfigureAwait(false);
            }
            finally
            {
                operations.Release();
            }
        }
    }

    private static RouterRoute? DefaultRoute()
    {
        try
        {
            // Connecting a UDP socket without sending asks the OS which IPv4 source would use the default route.
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9));
            if (probe.LocalEndPoint is not IPEndPoint source)
                return null;

            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                var properties = adapter.GetIPProperties();
                if (!properties.UnicastAddresses.Any(item => item.Address.Equals(source.Address)))
                    continue;
                var gateway = properties.GatewayAddresses.Select(item => item.Address).FirstOrDefault(address =>
                    address.AddressFamily == AddressFamily.InterNetwork && !address.Equals(IPAddress.Any) && !IPAddress.IsLoopback(address));
                if (gateway is not null)
                    return new(source.Address, new(gateway, 5351), new(IPAddress.Parse("239.255.255.250"), 1900));
            }
        }
        catch (Exception error) when (error is SocketException or NetworkInformationException)
        {
        }
        return null;
    }

    private static Grant? PublicGrant(IPAddress address, int port, uint seconds, DateTimeOffset sent) =>
        port > 0 && seconds is > 0 and <= MaximumLifetime && IsPublicIpv4(address)
            ? new(address, port, sent.AddSeconds(seconds)) : null;

    internal static bool IsPublicIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] is > 0 and < 224 and not (10 or 127)
            && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            && !(bytes[0] == 169 && bytes[1] == 254)
            && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            && !(bytes[0] == 192 && (bytes[1] == 168 || (bytes[1] == 0 && bytes[2] is 0 or 2) || (bytes[1] == 88 && bytes[2] == 99)))
            && !(bytes[0] == 198 && (bytes[1] is 18 or 19 || (bytes[1] == 51 && bytes[2] == 100)))
            && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
    }
}
