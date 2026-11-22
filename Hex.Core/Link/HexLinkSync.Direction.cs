namespace Hex.Core;

internal sealed partial class HexLinkSync
{
    private const string VIEW_ONLY_REPLICA = "not sent: view only";
    private CancellationTokenSource? receiving;
    private Task followPoll = Task.CompletedTask;
    private TaskCompletionSource? stoppingWork;

    internal bool UsesPeer(HexLinkPeer candidate) => ReferenceEquals(peer, candidate);

    private bool CanReceive => !BoundAuthorityEngine ||
        HexLinkAllowed.Has(peer.Session.Connection.Rights, HexLinkRights.Edit);

    private CancellationTokenSource? ReceiveLease(CancellationToken cancel)
    {
        if (!BoundAuthorityEngine)
            return null;

        lock (gate)
        {
            if (!CanReceive)
                throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "The replica has View only.");
            receiving ??= new();
            return CancellationTokenSource.CreateLinkedTokenSource(cancel, stopping.Token, receiving.Token);
        }
    }

    internal void RefreshReceiveRights()
    {
        if (!BoundAuthorityEngine)
            return;
        if (!HexLinkAllowed.Has(peer.Session.Connection.Rights, HexLinkRights.View))
        {
            Stop();
            return;
        }

        CancellationTokenSource? revoke = null;
        lock (gate)
        {
            if (CanReceive)
            {
                foreach (string path in skipped.Where(row => row.Value.Why == VIEW_ONLY_REPLICA).Select(row => row.Key))
                    Dirty(path);
                WakeGitFollow();
            }
            else
            {
                revoke = receiving;
                receiving = null;
            }
        }

        if (revoke is not null)
        {
            revoke.Cancel();
            revoke.Dispose();
        }
    }

    private async Task<bool> ReconcileWithoutReplicaEdits(string wire, string full, HexLinkTreeFile? mine,
        HexLinkTreeFile? theirs, HexLinkSyncAgreed? agreed, byte[]? localBytes, CancellationToken cancel)
    {
        string? m = mine?.Hash, t = theirs?.Hash;
        bool remoteModeChanged = agreed?.Executable is { } mode && theirs?.Executable is { } replicaMode && mode != replicaMode;
        if (m == t && (mine?.Executable == theirs?.Executable || (!remoteModeChanged && agreed?.Executable is not null)))
            return false;
        if (t != agreed?.Remote || remoteModeChanged || (m == agreed?.Local && mine?.Executable == agreed?.Executable)
            || (agreed is null && staleTracked.Contains(wire)))
        {
            Skip(wire, VIEW_ONLY_REPLICA);
            return true;
        }

        if ((mine?.Size ?? 0) > HexLinkFiles.MOST_READ || (theirs?.Size ?? 0) > HexLinkFiles.MOST_READ)
            await PushStreamed(wire, full, mine, t, cancel);
        else
            await Push(wire, full, agreed, localBytes, m, t, cancel);
        return true;
    }

    internal async Task DrainStoppedEngine(CancellationToken cancel)
    {
        Task work;
        lock (gate)
        {
            work = running.Count == 0 && taking == 0 ? Task.CompletedTask :
                (stoppingWork ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        await Task.WhenAll(work, followPoll).WaitAsync(cancel);
        Save();
    }
}
