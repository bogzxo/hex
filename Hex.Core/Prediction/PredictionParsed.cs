namespace Hex.Core;

/// <summary>
/// What an answer comes to so far: the edits, where the model put the caret after them (an offset into the text once
/// they're made, null when it didn't say), and whether the answer is settled, so that the rest of the stream needn't be
/// read. Until it is, the edits are empty. <paramref name="Note"/> says why a settled answer was thrown away, when it
/// was and the user should be told.
/// </summary>
internal sealed record PredictionParsed(IReadOnlyList<PredictionEdit> Edits, int? CaretAfter, bool Done, string? Note = null)
{
    /// <summary>An answer that isn't settled yet.</summary>
    public static readonly PredictionParsed Unsettled = new([], null, false);

    /// <summary>A settled answer that changes nothing.</summary>
    public static readonly PredictionParsed Nothing = new([], null, true);

    /// <summary>A settled answer thrown away for rewriting most of its region.</summary>
    public static readonly PredictionParsed Rewrote = new([], null, true, "The model rewrote most of the region, so its answer was ignored.");
}
