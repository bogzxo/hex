using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkPrivateWorktree
{
    // After a receipt, the base is what the authority published. If the draft changed during a merge,
    // retain the exact earlier draft input so the next publish can separate later edits from that merge.
    private readonly Dictionary<string, string> draftMergeBases = new(StringComparer.Ordinal);
    private string DraftMergeDirectory => Path.Combine(data, "draft-merge-bases");

    private void RetainDraftMergeBase(string wire, string hash, byte[] bytes)
    {
        if (HexLinkFiles.Hash(bytes) != hash)
            throw new IOException($"The checked draft input changed for {wire}.");
        Directory.CreateDirectory(DraftMergeDirectory);
        HexLinkFiles.WriteAtomic(Path.Combine(DraftMergeDirectory, hash), bytes);
        draftMergeBases[wire] = hash;
    }

    private async Task<byte[]> ReadDraftMergeBase(string wire, string hash, CancellationToken cancel)
    {
        string path = Path.Combine(DraftMergeDirectory, hash);
        RequirePublishSize(wire, new FileInfo(path).Length);
        byte[] bytes = await HexLinkFiles.ReadSharedAsync(path, cancel);
        if (HexLinkFiles.Hash(bytes) != hash)
            throw new IOException($"The checked draft input was changed on disk for {wire}.");
        return bytes;
    }

    private void LoadDraftMergeBases(JsonObject state)
    {
        if (state["draft_merge_bases"] is not JsonObject saved)
            return;
        foreach (var (wire, node) in saved)
        {
            if (node is not JsonValue value || !value.TryGetValue(out string? hash) ||
                hash is not { Length: 64 } || !hash.All(char.IsAsciiHexDigit))
                throw new IOException("Invalid checked inputs in private worktree metadata.");
            draftMergeBases.Add(wire, hash);
        }
    }

    private void PruneDraftMergeBases()
    {
        if (!Directory.Exists(DraftMergeDirectory))
            return;
        try
        {
            var keep = new HashSet<string>(draftMergeBases.Values, PlatformPaths.Comparer);
            foreach (string file in Directory.EnumerateFiles(DraftMergeDirectory))
            {
                if (!keep.Contains(Path.GetFileName(file)))
                    File.Delete(file);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[Hex] Private merge input cleanup is pending: {error.Message}");
        }
    }
}
