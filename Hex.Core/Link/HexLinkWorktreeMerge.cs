namespace Hex.Core;

internal sealed record HexLinkWorktreeMerge(bool Conflict, byte[]? Bytes)
{
    public static async Task<HexLinkWorktreeMerge> Merge(byte[]? basis, byte[]? draft, byte[]? shared, CancellationToken cancel)
    {
        if (Same(draft, shared) || Same(basis, shared))
            return new(false, draft);
        if (Same(basis, draft))
            return new(false, shared);
        if (basis is null || draft is null || shared is null)
            return new(true, draft);

        HexLinkMergeResult? merged = await HexLinkMerge.TryTextAsync(basis, draft, shared, cancel);
        return merged is null ? new(true, draft) : new(!merged.Clean, merged.Bytes);
    }

    private static bool Same(byte[]? a, byte[]? b) =>
        a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
}
