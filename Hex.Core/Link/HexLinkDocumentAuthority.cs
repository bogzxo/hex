namespace Hex.Core;

internal sealed record HexDocCheckpoint(Guid Epoch, long Revision, string Text);
internal sealed record HexDocReplay(Guid Epoch, long Since, long Head, HexDocCheckpoint? Checkpoint, HexDocOperation[] Operations);
internal readonly record struct HexDocReceipt(Guid Epoch, long Revision, HexDocApply Outcome);

/// <summary>
/// Serializes accepted operations for one room. A reconnect can fetch ordered operations since its last revision;
/// a new or obsolete replica starts with the checkpoint and fetches the tail in bounded pages.
/// </summary>
internal sealed class HexLinkDocumentAuthority
{
    private const int MaxParticipants = 97;
    private const int MaxReplayOperations = 256;
    private readonly Lock gate = new();
    private readonly Dictionary<int, Participant> participants = [];
    private readonly List<HexDocOperation> tail = [];
    private HexDocCheckpoint checkpoint;
    private HexLinkDocumentSequence sequence;

    private sealed class Participant
    {
        public bool Connected;
        public long Acknowledged;
        public bool CheckpointReady;
    }

    public HexLinkDocumentAuthority(string seed)
    {
        sequence = new HexLinkDocumentSequence(seed);
        checkpoint = new HexDocCheckpoint(Guid.NewGuid(), 0, seed);
    }

    private long Head => checkpoint.Revision + tail.Count;
    public long Clock { get { lock (gate) return sequence.Clock; } }

    public bool CanOfferCheckpoint
    {
        get { lock (gate) return (tail.Count >= 2048 || sequence.StoredUnits >= 6 * 1024 * 1024) && participants.Values.All(each => each.Connected); }
    }

    public HexDocCheckpoint Checkpoint
    {
        get { lock (gate) return checkpoint; }
    }

    public HexDocView View()
    {
        lock (gate)
            return sequence.View();
    }

    public void Reconnect(int author, bool resetAcknowledgement = false)
    {
        if (author <= 0)
            throw new ArgumentOutOfRangeException(nameof(author));
        lock (gate)
        {
            if (!participants.TryGetValue(author, out var participant))
            {
                if (participants.Count == MaxParticipants)
                    throw new InvalidOperationException("The document participant limit has been reached.");
                participants.Add(author, participant = new Participant { Acknowledged = checkpoint.Revision });
            }
            if (resetAcknowledgement)
                participant.Acknowledged = checkpoint.Revision;
            participant.Connected = true;
            participant.CheckpointReady = false;
        }
    }

    public void Disconnect(int author)
    {
        lock (gate)
        {
            if (participants.TryGetValue(author, out var participant))
            {
                participant.Connected = false;
                participant.CheckpointReady = false;
            }
        }
    }

    public void Leave(int author)
    {
        lock (gate)
            participants.Remove(author);
    }

    public HexDocReceipt Submit(Guid epoch, int author, HexDocOperation operation)
    {
        lock (gate)
        {
            RequireEpoch(epoch);
            if (!participants.TryGetValue(author, out var participant) || !participant.Connected || operation.Id.Author != author)
                throw new InvalidOperationException("Only the connected author can submit its document operation.");
            if (operation.Id.Clock > sequence.Clock + HexLinkDocumentSequence.MaxOperations)
                throw new ArgumentException("The document clock is too far ahead.", nameof(operation));
            // The caller may reuse its decoded list. Keep an immutable copy for replay.
            if (operation is HexDocEdit edit)
                operation = edit with { Removed = (HexDocSpan[])edit.Removed.Clone() };
            HexDocApply result = sequence.Apply(operation);
            if (result == HexDocApply.Accepted)
                tail.Add(operation);
            return new HexDocReceipt(checkpoint.Epoch, Head, result);
        }
    }

    public HexDocReplay Replay(Guid? epoch, long revision, int limit = MaxReplayOperations, int budget = int.MaxValue)
    {
        lock (gate)
        {
            bool reset = epoch != checkpoint.Epoch || revision < checkpoint.Revision || revision > Head;
            long since = reset ? checkpoint.Revision : revision;
            int at = checked((int)(since - checkpoint.Revision));
            int maximum = Math.Min(tail.Count - at, Math.Clamp(limit, 1, MaxReplayOperations));
            long size = reset ? checkpoint.Text.Length * 6L : 0;
            int count = 0;
            while (count < maximum)
            {
                size += Estimate(tail[at + count]);
                if (size > budget)
                    break;
                count++;
            }
            HexDocOperation[] operations = new HexDocOperation[count];
            for (int i = 0; i < operations.Length; i++)
            {
                HexDocOperation operation = tail[at + i];
                operations[i] = operation is HexDocEdit edit ? edit with { Removed = (HexDocSpan[])edit.Removed.Clone() } : operation;
            }
            return new HexDocReplay(checkpoint.Epoch, since, Head, reset ? checkpoint : null, operations);
        }
    }

    // A conservative upper bound for the default JSON encoder and numeric delimited operation fields.
    internal static long Estimate(HexDocOperation operation) => operation is HexDocEdit edit
        ? 160 + edit.Inserted.Length * 6L + edit.Removed.Length * 64L : 160;

    /// <summary>
    /// The replica may permit a checkpoint only after draining local operations and releasing undo/anchor references
    /// to the old epoch. Acknowledging ordinary updates does not permit compaction.
    /// </summary>
    public void Acknowledge(Guid epoch, int author, long revision, bool checkpointReady)
    {
        lock (gate)
        {
            RequireEpoch(epoch);
            if (!participants.TryGetValue(author, out var participant) || !participant.Connected)
                throw new InvalidOperationException("This author is not connected to the document.");
            if (revision < participant.Acknowledged || revision > Head)
                throw new ArgumentOutOfRangeException(nameof(revision));
            participant.Acknowledged = revision;
            participant.CheckpointReady = checkpointReady;
        }
    }

    /// <summary>Folds the acknowledged frontier into one run and releases old atom IDs and deletion markers.</summary>
    public HexDocCheckpoint? Compact()
    {
        lock (gate)
        {
            long head = Head;
            if (tail.Count == 0 || participants.Values.Any(p => !p.Connected || !p.CheckpointReady || p.Acknowledged != head))
                return null;

            string text = sequence.View().Text;
            sequence = new HexLinkDocumentSequence(text);
            checkpoint = new HexDocCheckpoint(Guid.NewGuid(), head, text);
            tail.Clear();
            foreach (Participant participant in participants.Values)
                participant.CheckpointReady = false;
            return checkpoint;
        }
    }

    private void RequireEpoch(Guid epoch)
    {
        if (epoch != checkpoint.Epoch)
            throw new InvalidOperationException("The document checkpoint changed. Keep edits from the previous session as a draft.");
    }
}
