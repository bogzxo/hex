namespace Hex.Core;

internal sealed partial class HexLinkSync
{
    private const string VIEW_ONLY_REPLICA = "not sent: view only";

    /// <summary>
    /// The stable marker a refusal carries when the file is open in a shared document that hasn't saved the write, so a
    /// window can offer to close the tab and resolve again (<see cref="IsSharedOpenRefusal"/>).
    /// </summary>
    public const string SHARED_OPEN_MARKER = "[shared_document_open]";

    /// <summary>The refusal's whole sentence for a file open in a shared document.</summary>
    public static string SharedOpenRefusal(string wire) =>
        $"{wire} is open in a shared document. Close its tab, then resolve again. {SHARED_OPEN_MARKER}";

    /// <summary>Whether a refusal said why is the file being open in a shared document.</summary>
    public static bool IsSharedOpenRefusal(string? why) => why?.Contains(SHARED_OPEN_MARKER, StringComparison.Ordinal) == true;
    private CancellationTokenSource? receiving;
    private Task followPoll = Task.CompletedTask;
    private TaskCompletionSource? stoppingWork;

    internal bool UsesPeer(HexLinkPeer candidate) => ReferenceEquals(peer, candidate);

    // What the other device writes here is applied only while this device lets it Edit (what this side grants, not what it
    // was granted): taking Edit away from a device stops its saves landing here, while this side still sends its own.
    // A pull mirror applies whatever the authority has.
    private bool CanReceive => BoundPullMirror ||
        HexLinkAllowed.Has(peer.Session.Connection.Rights, HexLinkRights.Edit);

    /// <summary>Whether what the other device changes is left alone here because this device no longer lets it Edit.</summary>
    public bool ReceiveBlocked => !CanReceive;

    private CancellationTokenSource? ReceiveLease(CancellationToken cancel)
    {
        if (BoundPullMirror)
            return null;

        lock (gate)
        {
            if (!CanReceive)
                throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, $"{peer.Device.Name} has no Edit here.");
            receiving ??= new();
            return CancellationTokenSource.CreateLinkedTokenSource(cancel, stopping.Token, receiving.Token);
        }
    }

    internal void RefreshReceiveRights()
    {
        if (BoundPullMirror)
            return;
        if (BoundAuthorityEngine && !HexLinkAllowed.Has(peer.Session.Connection.Rights, HexLinkRights.View))
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
