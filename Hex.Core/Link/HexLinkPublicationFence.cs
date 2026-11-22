using System.Collections.Concurrent;

namespace Hex.Core;

/// <summary>Lets live reads and writes run concurrently between exclusive publication decisions for one root.</summary>
internal sealed class HexLinkPublicationFence
{
    private static readonly ConcurrentDictionary<string, HexLinkPublicationFence> Folders = new(PlatformPaths.Comparer);

    private readonly SemaphoreSlim turn = new(1), room = new(1), counting = new(1);
    private int readers, recoveryPending;

    public static HexLinkPublicationFence ForRoot(string root) =>
        Folders.GetOrAdd(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), static _ => new HexLinkPublicationFence());

    public void BlockLiveForRecovery(bool pending) => Volatile.Write(ref recoveryPending, pending ? 1 : 0);

    public async ValueTask<LiveLease> EnterLive(CancellationToken cancel)
    {
        await turn.WaitAsync(cancel);
        turn.Release();
        await counting.WaitAsync(cancel);
        try
        {
            if (readers == 0)
                await room.WaitAsync(cancel);
            if (Volatile.Read(ref recoveryPending) != 0)
            {
                if (readers == 0)
                    room.Release();
                throw new HexLinkException(HexLinkErrors.BUSY, "The folder is recovering an interrupted publication.");
            }
            readers++;
            return new LiveLease(this);
        }
        finally
        {
            counting.Release();
        }
    }

    public async ValueTask<IAsyncDisposable> EnterPublish(CancellationToken cancel)
    {
        await turn.WaitAsync(cancel);
        try
        {
            await room.WaitAsync(cancel);
            return new PublishLease(room);
        }
        finally
        {
            turn.Release();
        }
    }

    private async ValueTask LeaveLive()
    {
        await counting.WaitAsync();
        try
        {
            if (--readers == 0)
                room.Release();
        }
        finally
        {
            counting.Release();
        }
    }

    internal readonly struct LiveLease(HexLinkPublicationFence owner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => owner.LeaveLive();
    }

    private sealed class PublishLease(SemaphoreSlim room) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            room.Release();
            return ValueTask.CompletedTask;
        }
    }
}
