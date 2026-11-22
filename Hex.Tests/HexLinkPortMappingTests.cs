using System.Net;
using Hex.Core;

namespace Hex.Tests;

public sealed class HexLinkPortMappingTests
{
    [Fact]
    public async Task Port_change_releases_a_late_grant_before_requesting_its_replacement()
    {
        var mapper = new DelayedMapper();
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mapping = new HexLinkPortMapping(mapper, () => changed.TrySetResult());
        _ = mapping.SetPort(40000);
        await mapper.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _ = mapping.SetPort(40001);
        mapper.FirstGranted.SetResult(mapper.First);
        try
        {
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(mapper.ReleasedBeforeReplacement);
            Assert.Same(mapper.Second.PublicEndPoint, mapping.Status.Endpoint);
        }
        finally
        {
            await mapping.SetPort(0).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Null(mapping.Status.Endpoint);
        Assert.True(mapper.Second.Released);
    }

    private sealed class DelayedMapper : IHexRouterPortMapper
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IHexRouterPortLease?> FirstGranted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Lease First { get; } = new(41000);
        public Lease Second { get; } = new(41001);
        public bool ReleasedBeforeReplacement { get; private set; }

        public Task<IHexRouterPortLease?> TryMapAsync(int localTcpPort, CancellationToken cancellationToken = default)
        {
            if (localTcpPort == 40000)
            {
                Started.SetResult();
                return FirstGranted.Task;
            }
            ReleasedBeforeReplacement = First.Released;
            return Task.FromResult<IHexRouterPortLease?>(Second);
        }
    }

    private sealed class Lease(int port) : IHexRouterPortLease
    {
        public IPEndPoint PublicEndPoint { get; } = new(IPAddress.Parse("203.0.113.10"), port);
        public DateTimeOffset ExpiresAt => DateTimeOffset.UtcNow.AddHours(1);
        public bool Released { get; private set; }
        public Task<bool> RenewAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public ValueTask DisposeAsync()
        {
            Released = true;
            return ValueTask.CompletedTask;
        }
    }
}
