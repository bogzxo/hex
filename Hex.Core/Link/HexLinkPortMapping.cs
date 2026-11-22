using System.Net;

namespace Hex.Core;

/// <summary>Owns one router lease and releases it before acquiring a replacement.</summary>
internal sealed class HexLinkPortMapping(IHexRouterPortMapper mapper, Action changed)
{
    private readonly Lock gate = new();
    private CancellationTokenSource? current;
    private Task running = Task.CompletedTask;
    private int port;
    private IPEndPoint? endpoint;
    private bool unavailable;

    public (IPEndPoint? Endpoint, bool Unavailable) Status
    {
        get { lock (gate) return (endpoint, unavailable); }
    }

    public Task SetPort(int next)
    {
        lock (gate)
        {
            if (port == next)
                return running;
            port = next;
            var previous = current;
            previous?.Cancel();
            current = next == 0 ? null : new CancellationTokenSource();
            var owner = current;
            Task prior = running;
            endpoint = null;
            unavailable = false;
            running = Task.Run(async () =>
            {
                await prior.ConfigureAwait(false);
                previous?.Dispose();
                if (owner is not null)
                    await Maintain(next, owner.Token).ConfigureAwait(false);
            });
            return running;
        }
    }

    private async Task Maintain(int localPort, CancellationToken stop)
    {
        try
        {
            stop.ThrowIfCancellationRequested();
            await using var lease = await mapper.TryMapAsync(localPort, stop).ConfigureAwait(false);
            if (lease is null)
                return;
            do
            {
                stop.ThrowIfCancellationRequested();
                lock (gate)
                {
                    if (stop.IsCancellationRequested)
                        return;
                    endpoint = lease.PublicEndPoint;
                }
                changed();
                TimeSpan remaining = lease.ExpiresAt - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    return;
                await Task.Delay(remaining / 2, stop).ConfigureAwait(false);
            }
            while (await lease.RenewAsync(stop).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            Console.WriteLine($"[Hex] Link router mapping failed: {error.Message}");
        }
        finally
        {
            lock (gate)
            {
                if (!stop.IsCancellationRequested)
                {
                    endpoint = null;
                    unavailable = true;
                }
            }
            if (!stop.IsCancellationRequested)
                changed();
        }
    }
}
