using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>A checked publication carried inside Link's existing bulk stream.</summary>
internal static class HexLinkPublicationPack
{
    public const string Purpose = "publish/pack";
    // The publisher and receiver use the same bound before staging or allocating the decoded set.
    public const long MaximumBytes = 128L * 1024 * 1024;
    private const int MaximumHeader = 16 * 1024 * 1024;

    public static async Task Send(HexLinkSession session, HexLinkCommittedSet set, IReadOnlyList<HexLinkPublishEdit> edits,
        bool replica, bool bypass, CancellationToken cancel)
    {
        JsonObject metadata = new()
        {
            ["id"] = set.Id.ToString("N"), ["folder"] = set.Folder, ["epoch"] = set.Epoch,
            ["replica"] = replica, ["bypass"] = bypass
        };
        var packet = Encode(set, edits);
        await using var upload = await session.OpenBulkAsync(Purpose, packet.Length, metadata, cancel);
        foreach (byte[] part in packet.Parts)
            await upload.WriteAsync(part, cancel);
        await upload.CompleteAsync(cancel);
    }

    internal static (long Length, IReadOnlyList<byte[]> Parts) Encode(HexLinkCommittedSet set, IReadOnlyList<HexLinkPublishEdit> edits)
    {
        JsonObject manifest = set.ToJson();
        var checkedSet = HexLinkCommittedSet.Read(manifest);
        byte[] header = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        if (header.Length > MaximumHeader || edits.Count != checkedSet.Files.Count)
            throw HexLinkException.Invalid("The publication pack manifest is too large or incomplete.");

        var editByPath = edits.ToDictionary(edit => edit.Path, StringComparer.Ordinal);
        var parts = new List<byte[]> { Size(header.Length), header };
        long length = header.Length + sizeof(int);
        foreach (var file in checkedSet.Files)
        {
            if (!editByPath.TryGetValue(file.Path, out var edit) || edit.Base != file.Base ||
                Digest(edit.Bytes) != file.Hash || edit.Bytes?.Length > HexLinkFiles.MOST_READ)
                throw HexLinkException.Invalid("Publication bytes do not match the checked manifest.");
            if (edit.Bytes is not { } bytes)
                continue;
            length += sizeof(int) + bytes.Length;
            if (length > MaximumBytes)
                throw new HexLinkException(HexLinkErrors.TOO_BIG, "This publication exceeds the bulk pack size limit.");
            parts.Add(Size(bytes.Length));
            parts.Add(bytes);
        }
        return (length, parts);
    }

    internal static async Task<(HexLinkCommittedSet Set, IReadOnlyList<HexLinkPublishEdit> Edits)> Read(Stream stream,
        long length, CancellationToken cancel)
    {
        if (length is < sizeof(int) or > MaximumBytes)
            throw HexLinkException.Invalid("Invalid publication pack length.");
        var size = new byte[sizeof(int)];
        long left = length;

        async Task<byte[]> Blob(int maximum)
        {
            if (left < size.Length)
                throw HexLinkException.Invalid("The publication pack is incomplete.");
            await stream.ReadExactlyAsync(size, cancel);
            left -= size.Length;
            int count = BinaryPrimitives.ReadInt32LittleEndian(size);
            if (count < 0 || count > maximum || count > left)
                throw HexLinkException.Invalid("Invalid publication pack content length.");
            var bytes = new byte[count];
            await stream.ReadExactlyAsync(bytes, cancel);
            left -= count;
            return bytes;
        }

        byte[] header = await Blob(MaximumHeader);
        if (JsonNode.Parse(header) is not JsonObject said)
            throw HexLinkException.Invalid("The publication pack has no manifest.");
        var set = HexLinkCommittedSet.Read(said);
        var edits = new List<HexLinkPublishEdit>(set.Files.Count);
        foreach (var file in set.Files)
        {
            byte[]? bytes = file.Hash is null ? null : await Blob(HexLinkFiles.MOST_READ);
            if (Digest(bytes) != file.Hash)
                throw HexLinkException.Invalid("A publication blob has a different content hash.");
            edits.Add(new(file.Path, file.Base, bytes));
        }
        if (left != 0)
            throw HexLinkException.Invalid("The publication pack has trailing bytes.");
        return (set, edits);
    }

    private static string? Digest(byte[]? bytes) => bytes is null ? null : HexLinkFiles.Hash(bytes);

    private static byte[] Size(int length)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        return bytes;
    }
}
