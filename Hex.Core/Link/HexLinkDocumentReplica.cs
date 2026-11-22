namespace Hex.Core;

internal readonly record struct HexDocSplice(int Start, int Length, string Inserted)
{
    public static HexDocSplice Between(string before, string after)
    {
        int limit = Math.Min(before.Length, after.Length);
        int start = 0;
        while (start < limit && before[start] == after[start])
            start++;
        if (start != 0 && (!HexLinkDocumentSequence.Boundary(before, start)
            || !HexLinkDocumentSequence.Boundary(after, start)))
            start--;

        int suffix = 0;
        while (suffix < limit - start && before[^(suffix + 1)] == after[^(suffix + 1)])
            suffix++;
        if (suffix != 0 && (!HexLinkDocumentSequence.Boundary(before, before.Length - suffix)
            || !HexLinkDocumentSequence.Boundary(after, after.Length - suffix)))
            suffix--;

        return new(start, before.Length - start - suffix, after[start..(after.Length - suffix)]);
    }
}

/// <summary>
/// An optimistic participant. Deliberately keeps the authority's ordered frontier separate from unsent edits.
/// A sent operation stays pending until it arrives in an ordered replay, even when the response was lost.
/// </summary>
internal sealed class HexLinkDocumentReplica
{
    private readonly List<HexDocOperation> pending = [];
    private HexLinkDocumentSequence sequence = null!;

    public Guid Epoch { get; private set; }
    public int Author { get; private set; }
    public bool CanEdit { get; private set; }
    public long Revision { get; private set; }
    public long Head { get; private set; }
    public IReadOnlyList<HexDocParticipant> Participants { get; private set; } = [];
    public bool HasPending => pending.Count != 0;
    public HexDocView View => sequence.View();

    public HexLinkDocumentReplica(HexDocExchange opened) => Receive(opened);

    public HexDocView Capture(HexDocView presented, string current)
    {
        if (presented.Text == current)
            return presented;

        HexDocSplice change = HexDocSplice.Between(presented.Text, current);
        var operation = presented.Edit(new HexDocId(sequence.Clock + 1, Author), change.Start, change.Length, change.Inserted);
        if (HexLinkDocumentAuthority.Estimate(operation) > HexLinkDocumentWire.Budget || operation.Removed.Length > 8192)
            throw new InvalidOperationException("The local edit is too large to send as one shared operation.");
        Queue(operation);
        return presented.WithEdit(change, operation.Id, current);
    }

    public HexDocOperation[] Batch()
    {
        int count = 0;
        long bytes = 0;
        while (count < pending.Count && count < HexLinkDocumentWire.MaxBatch)
        {
            bytes += HexLinkDocumentAuthority.Estimate(pending[count]);
            if (bytes > HexLinkDocumentWire.Budget)
                break;
            count++;
        }
        return pending.GetRange(0, count).ToArray();
    }

    public void Receive(HexDocExchange response)
    {
        HexDocReplay replay = response.Replay;
        if (replay.Checkpoint is { } checkpoint &&
            (sequence is null || Epoch != replay.Epoch || Author != response.Author || Revision != replay.Since))
        {
            if (HasPending)
                throw new InvalidOperationException("The shared document changed its checkpoint. Local edits remain in a draft.");
            Epoch = checkpoint.Epoch;
            Revision = checkpoint.Revision;
            Author = response.Author;
            sequence = new HexLinkDocumentSequence(checkpoint.Text);
        }

        if (Epoch != replay.Epoch || Author != response.Author || Revision != replay.Since)
            throw new InvalidOperationException("The shared document reply does not continue the local revision.");
        foreach (HexDocOperation operation in replay.Operations)
        {
            if (sequence.Apply(operation) == HexDocApply.MissingDependency)
                throw new InvalidOperationException("The shared document reply omitted an earlier operation.");
            if (pending.Count > 0 && pending[0].Id == operation.Id)
                pending.RemoveAt(0);
            Revision++;
        }
        Head = replay.Head;
        Participants = response.Participants;
        CanEdit = response.CanEdit;
    }

    private void Queue(HexDocOperation operation)
    {
        if (sequence.Apply(operation) != HexDocApply.Accepted)
            throw new InvalidOperationException("A local edit needs an unavailable shared document position.");
        pending.Add(operation);
    }
}
