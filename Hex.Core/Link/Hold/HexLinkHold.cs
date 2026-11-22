namespace Hex.Core;

internal enum HexLinkHoldBaseSource { Agreed, Git, Local }

internal sealed record HexLinkHold(string Path, DateTimeOffset HeldAt, string BaseHash, HexLinkHoldBaseSource BaseSource,
    string? IncomingHash = null, bool IncomingDeleted = false, string? IncomingFrom = null, DateTimeOffset? IncomingAt = null,
    bool NoticeShown = false, bool BaseDeleted = false, bool? BaseExecutable = null)
{
    public bool HasIncoming => IncomingHash is not null || IncomingDeleted;
}

internal sealed record HexLinkHoldSnapshot(HexLinkHold Hold, byte[] BaseBytes, byte[]? IncomingBytes);
