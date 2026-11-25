namespace Hex.Core;

/// <summary>Where a suggestion for somewhere else in a file came from (docs/next-edit-plan.md, 2.1).</summary>
internal enum NextEditSource
{
    Model,
    Rename,
    Instruction,
}

/// <summary>
/// An edit expected somewhere other than at the caret, a "jump": its edits in offsets of the file's text (lines ended with
/// '\n') as it was at a version, the span they cover (the anchor), and the text that span held then so that it can be told
/// from a text that has moved on under it.
/// </summary>
internal sealed record NextEdit(
    string Path,
    int Start,
    int End,
    int Version,
    IReadOnlyList<PredictionEdit> Edits,
    NextEditSource Source,
    string Reason,
    double Confidence,
    string Expected);

/// <summary>One change made to a text: Start, the length of what it replaced and the length of what it put there.</summary>
internal readonly record struct NextEditChange(int Start, int OldLength, int NewLength)
{
    public int End => Start + OldLength;

    public int Delta => NewLength - OldLength;
}
