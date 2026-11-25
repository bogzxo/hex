using System.Text;
using Hex.Core;
using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

/// <summary>A shared document's save writes every line's own ending, on the authority and on the device it relays to.</summary>
public sealed class HexLinkDocumentEolTests
{
    private const string WIRE = "work/note.txt";

    [Theory]
    [InlineData("a\r\nb\nc\r\nd\n", "a\nB\nc\nd\n", "a\r\nB\nc\r\nd\n")]
    [InlineData("a\r\nb\nc\r\nd\n", "a\nb\nnew\nc\nd\n", "a\r\nb\nnew\nc\r\nd\n")]
    [InlineData("a\r\nb\nc\r\nd\n", "first\na\nb\nc\nd\n", "first\r\na\r\nb\nc\r\nd\n")]
    [InlineData("a\r\nb\nc\r\nd\n", "a\nb\nc\nd\nlast\n", "a\r\nb\nc\r\nd\nlast\n")]
    [InlineData("a\r\nb\r\nc\r\n", "a\nX\nc\n", "a\r\nX\r\nc\r\n")]
    [InlineData("a\nb\nc\n", "a\nX\nnew\nc\n", "a\nX\nnew\nc\n")]
    public async Task Both_disks_are_byte_identical_and_keep_each_lines_ending(string original, string edited, string expected)
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View | HexLinkRights.Edit);
        string local = Path.Combine(pair.A.Work, "note.txt"), there = Path.Combine(pair.B.Work, "note.txt");
        byte[] before = Encoding.UTF8.GetBytes(original);
        await File.WriteAllBytesAsync(local, before);
        await File.WriteAllBytesAsync(there, before);

        HexLinkDocumentRoom room = pair.A.Documents.Rooms.Open(WIRE, original.ReplaceLineEndings("\n"),
            HexDocFileBasis.FromRead(HexLinkFiles.Read(local, WIRE), original));
        var owner = new object();
        room.Join(DeviceA.Id, owner, DeviceA);
        var remote = new HexLinkDocumentReplica(await pair.PeerA.OpenDocument(WIRE));
        pair.BOnA |= HexLinkRights.Edit;
        remote.Capture(remote.View, edited);
        remote.Receive(await pair.PeerA.ExchangeDocument(WIRE, remote.Epoch, remote.Revision, remote.Batch()));

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(WAIT_MS));
        HexDocSaved receipt = await room.Save(DeviceA.Id, owner, local, remote.Epoch, remote.Revision, timeout.Token);
        byte[] saved = await File.ReadAllBytesAsync(local);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), saved);
        Assert.Equal(HexLinkFiles.Hash(saved), receipt.Hash);

        // The other device renders the receipt's text against its own copy of the old file, as the relayed save does.
        HexDocSavedSnapshot snapshot = (await room.ReadSaved(null, includeText: true, timeout.Token))!;
        var theirs = HexDocFileBasis.FromRead(HexLinkFiles.Read(there, WIRE), original);
        string raw = theirs.Render(snapshot.Text!);
        Assert.Equal(saved, theirs.Bytes(raw));

        // A second edit on top of the first keeps what the first save wrote.
        string again = edited + "tail\n";
        remote.Capture(remote.View, again);
        remote.Receive(await pair.PeerA.ExchangeDocument(WIRE, remote.Epoch, remote.Revision, remote.Batch()));
        await room.Save(DeviceA.Id, owner, local, remote.Epoch, remote.Revision, timeout.Token);
        string second = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(local));
        Assert.Equal(expected, second[..expected.Length]);
        Assert.StartsWith("tail", second[expected.Length..]);
    }
}
