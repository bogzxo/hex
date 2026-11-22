using Hex.Core.Git;

namespace Hex.Core;

internal static class HexLinkHoldGit
{
    public static async Task<IReadOnlyList<(string Wire, byte[]? Bytes)>> Changes(HexLinkHoldStore? store,
        string repository, string before, string after, CancellationToken cancel)
    {
        if (store is null || store.InRepository(repository) is not { Count: > 0 } held || before == after)
            return [];

        GitResult changed = await Task.Run(() => GitProcess.Run(repository,
            ["diff", "--name-only", "--no-renames", "-z", before, after, "--", .. held.Select(each => ":(literal)" + each.Relative)],
            unattended: true, cancel: cancel), cancel);
        if (!changed.Ok)
            throw new IOException(changed.Why);
        var paths = new HashSet<string>(changed.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries), PlatformPaths.Comparer);
        var files = new List<(string Wire, byte[]? Bytes)>();
        foreach (var (wire, relative) in held)
            if (paths.Contains(relative))
                files.Add((wire, await HexLinkSync.ReadHeadBlob(repository, after, relative, cancel, HexLinkFileTransfer.MaximumLength)));
        return files;
    }

    public static void Take(HexLinkHoldStore? store, IReadOnlyList<(string Wire, byte[]? Bytes)> changes, string fromDevice)
    {
        foreach (var (wire, bytes) in changes)
        {
            if (bytes is null)
                store!.SetIncomingDeleted(wire, fromDevice);
            else
                store!.SetIncoming(wire, bytes, fromDevice);
        }
    }

    public static string HeldCommit(int count) => $"commit not sent: {count} {(count == 1 ? "file" : "files")} held";
}
