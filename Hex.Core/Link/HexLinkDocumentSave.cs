using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed record HexDocSaved(Guid Epoch, long Revision, string Hash)
{
    internal JsonObject Encode() => new() { ["epoch"] = Epoch.ToString("N"), ["revision"] = Revision, ["hash"] = Hash };

    internal static HexDocSaved Decode(JsonObject message) =>
        new(HexLinkDocumentWire.Epoch(message) ?? throw HexLinkException.Invalid("The save acknowledgement has no epoch."),
            HexLinkDocumentWire.Revision(message), HexLinkDocumentProtocol.Field(message, "hash", 64));
}

internal sealed record HexDocFileBasis(string? Text, Encoding Encoding, bool CrLf, string? KnownHash = null)
{
    public string? Expected => KnownHash ?? (Text is null ? null : HexLinkFiles.Hash(Bytes(Text)));

    public byte[] Bytes(string text)
    {
        ReadOnlySpan<byte> bom = Encoding.Preamble;
        var bytes = new byte[bom.Length + Encoding.GetByteCount(text)];
        bom.CopyTo(bytes);
        Encoding.GetBytes(text, bytes.AsSpan(bom.Length));
        return bytes;
    }

    public static HexDocFileBasis FromRead(JsonObject read, string text)
    {
        Encoding encoding = HexLinkData.String(read, "encoding", 32) switch
        {
            "utf-8-bom" => new UTF8Encoding(true, true),
            "utf-16le" => new UnicodeEncoding(false, true, true),
            "utf-16be" => new UnicodeEncoding(true, true, true),
            _ => new UTF8Encoding(false, true)
        };
        return new(text, encoding, text.Contains("\r\n", StringComparison.Ordinal),
            HexLinkDocumentProtocol.Field(read, "hash", 64));
    }
}

internal sealed partial class HexLinkDocumentRoom
{
    private readonly SemaphoreSlim saving = new(1);
    private HexDocSaved? saved;

    public async Task<HexDocSaved> Save(string scope, object connection, string full, Guid epoch, long minimumRevision, CancellationToken cancel)
    {
        await saving.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            string text;
            long revision;
            HexDocFileBasis basis;
            lock (gate)
            {
                if (online.GetValueOrDefault(scope) != connection)
                    throw HexLinkException.NotFound("Join the shared document before saving.");
                HexDocReplay current = authority.Replay(epoch, minimumRevision, limit: 1);
                if (current.Epoch != epoch || current.Head < minimumRevision)
                    throw new HexLinkException(HexLinkErrors.CONFLICT, "The shared document changed before the save.");
                revision = current.Head;
                if (saved is { } already && already.Epoch == epoch && already.Revision == revision)
                    return already;
                text = authority.View().Text;
                basis = fileBasis ?? throw HexLinkException.NotFound("The shared document has no file to save.");
            }

            string raw = DocumentFile.WithLineEndings(text, basis.Text, basis.CrLf, basis.CrLf);
            byte[] bytes = basis.Bytes(raw);
            string? expected = basis.Expected;
            string hash = await HexLinkFiles.CompareAndReplace(full, Path, expected, bytes, cancel).ConfigureAwait(false)
                ?? throw new IOException("The shared file was not published.");

            lock (gate)
            {
                fileBasis = basis with { Text = raw, KnownHash = hash };
                return saved = new HexDocSaved(epoch, revision, hash);
            }
        }
        finally { saving.Release(); }
    }
}
