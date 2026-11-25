using Hex.Core.Merge;

namespace Hex.Core;

internal sealed partial class HexLinkSync
{
    /// <summary>
    /// A conflict settled on the given text, whatever each side holds now: a side that already has it (line ends and a
    /// byte order mark aside) is left exactly as it is, the other is written with the text in its own encoding and line
    /// ends. This is what comes after the text went through a shared document, where the room saves the file itself and
    /// the editor refuses every other write; the sync then only finds both sides already agree. Null when it's done,
    /// why when it isn't (not a conflict, pull-only, a side moved again, a write the editor refused).
    /// </summary>
    public async Task<string?> ResolveToText(string wire, string resolved, CancellationToken cancel = default)
    {
        HexLinkSyncConflict? conflict;
        lock (gate)
        {
            if (!live || !ready)
                return Summary;
            if (!conflicts.TryGetValue(wire, out conflict))
                return $"{wire} isn't in conflict.";
        }

        try
        {
            string full = Jailed(wire);
            byte[]? local = await ReadLocal(full, cancel);
            if (local is null)
                return $"{wire} is missing here, keep one side instead.";
            HexLinkFile remoteFile = await Fetch(wire, cancel);

            // Each side is only written when its text isn't already the resolution, in its own line ends
            byte[]? localResolved = HexLinkMerge.EncodeLike(resolved, local);
            byte[]? remoteResolved = HexLinkMerge.EncodeLike(resolved, remoteFile.Bytes);
            if (localResolved is not { Length: <= HexLinkFiles.MOST_READ } || remoteResolved is not { Length: <= HexLinkFiles.MOST_READ })
                return "The resolved file is too large or its encoding changed.";

            bool localHolds = SameText(local, localResolved, ignoreUtf8Bom: true);
            bool remoteHolds = SameText(remoteFile.Bytes, remoteResolved, ignoreUtf8Bom: true);
            // Writing here is receiving, which View allows; writing there needs Edit and a peer that checks its writes
            if (!localHolds && !CanReceive)
                return "The replica currently has View only.";
            if (!remoteHolds && (pullOnly || !remoteCertified))
                return $"{peer.Device.Name} doesn't let this device write there, only theirs can be kept.";

            byte[] localNow = local, remoteNow = remoteFile.Bytes;
            if (!localHolds)
            {
                string? written = await ApplyLocal(full, wire, HexLinkFiles.Hash(local), localResolved, cancel, null);
                if (written != HexLinkFiles.Hash(localResolved))
                    return $"{wire} changed locally during the resolution.";
                localNow = localResolved;
                Interlocked.Increment(ref received);
            }

            if (!remoteHolds)
            {
                string written = await Asked(() => peer.Write(wire, remoteFile.Hash, remoteResolved, cancel), cancel);
                if (written != HexLinkFiles.Hash(remoteResolved))
                    return $"{wire} changed remotely during the resolution.";
                remoteNow = remoteResolved;
                Interlocked.Increment(ref sent);
            }

            lock (gate)
            {
                mergePreviews.Remove(wire);
                conflicts.Remove(wire);
                SetBase(wire, AgreedOf(localNow, remoteNow));
            }

            await DropCopy(conflict, cancel);
            Save();
            Notify();
            return null;
        }
        catch (Exception e) when (e is HexLinkException or IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    /// <summary>
    /// The text of one side of a conflict as it is now, this device's file or the other's, so it can be put into a shared
    /// document. Null when that side is missing, isn't text or the path isn't in conflict.
    /// </summary>
    public async Task<string?> ConflictSideText(string wire, HexLinkSyncKeep keep, CancellationToken cancel = default)
    {
        bool theirs;
        lock (gate)
        {
            if (!live || !ready || !conflicts.ContainsKey(wire))
                return null;
            theirs = remote.ContainsKey(wire);
        }

        try
        {
            byte[]? bytes = keep == HexLinkSyncKeep.Mine
                ? await ReadLocal(Jailed(wire), cancel)
                : theirs ? (await Fetch(wire, cancel)).Bytes : null;
            return bytes is null ? null : HexLinkFiles.Decode(bytes)?.Text;
        }
        catch (Exception e) when (e is HexLinkException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
