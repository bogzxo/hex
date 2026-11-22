namespace Hex.Core;

internal static partial class HexLinkProvision
{
    internal static bool DiskFull(Exception error) =>
        error is IOException && (error.HResult & 0xffff) is 28 or 39 or 112
        || error.Message.Contains("No space left on device", StringComparison.OrdinalIgnoreCase)
        || error.Message.Contains("not enough space on the disk", StringComparison.OrdinalIgnoreCase);

    // A connection or destination failure is shared by all files. An individual path or Git checkout can be left alone.
    private static bool ItemFailure(Exception error, HexLinkPeer? peer = null) => !DiskFull(error) && (error switch
    {
        HexLinkException failure => failure.Code != HexLinkErrors.CLOSED
            && (failure.Code != HexLinkErrors.NOT_ALLOWED || peer is not null && HexLinkAllowed.Has(peer.Granted, HexLinkRights.View)
                && !string.Equals(HexLinkData.String(failure.Detail as System.Text.Json.Nodes.JsonObject, "needs", 16), "view", StringComparison.OrdinalIgnoreCase)),
        IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or NotSupportedException => true,
        _ => false
    });

    private static IReadOnlyList<HexLinkProvisionSkip> SortedSkips(IEnumerable<HexLinkProvisionSkip> skipped) =>
        [.. skipped.DistinctBy(entry => (entry.Path, entry.File)).OrderBy(entry => entry.Path, StringComparer.Ordinal)];
}
