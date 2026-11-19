namespace Hex.Dawn;

/// <summary>
/// Whether what's typed has stood still for a moment, for work done again once the typing stops (the outline, the
/// hints, the semantic colours, the lenses, the folds). The first time a new state is seen it isn't settled, and it is
/// once it has been the same for the time given. Kept in a field and asked every tick: a struct, so it must not be
/// readonly or copied, the field itself is what remembers.
/// </summary>
/// <typeparam name="T">What tells one state from the next, a document's version, or the document and its version.</typeparam>
internal struct HexSettle<T>(long milliseconds)
{
    private T? seen;
    private bool any;
    private long changedAt;

    /// <summary>Whether <paramref name="state"/> has stood still for the time given, as of <paramref name="now"/> (Environment.TickCount64).</summary>
    public bool Settled(T state, long now)
    {
        if (!any || !EqualityComparer<T>.Default.Equals(seen, state))
        {
            seen = state;
            any = true;
            changedAt = now;
            return false;
        }

        return now - changedAt >= milliseconds;
    }

    /// <summary>Nothing seen, the next state asked about starts the wait.</summary>
    public void Reset()
    {
        any = false;
        // A document it held goes with it, a closed one wasn't kept by this
        seen = default;
    }
}
