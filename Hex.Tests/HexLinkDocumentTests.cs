using System.Text.Json.Nodes;
using Hex.Core;
using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

public sealed class HexLinkDocumentTests
{
    private const string Path = "work/example.txt";

    [Fact]
    public async Task Only_shared_buffers_are_visible_and_each_request_checks_current_rights()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        Assert.Equal(HexLinkErrors.NOT_FOUND, await CodeOf(pair.PeerA.JoinDocument(Path)));
        var start = pair.A.Documents.Share(Path, "unsaved");
        Assert.Equal(start, await pair.PeerA.JoinDocument(Path));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(pair.PeerA.JoinDocument("work/../example.txt")));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(pair.PeerA.EditDocument(Path, start.Session, 0, "changed")));
        pair.BOnA |= HexLinkRights.Edit;
        var edited = await pair.PeerA.EditDocument(Path, start.Session, 0, "changed");
        Assert.Equal(DeviceB.Id, edited.Author);
        Assert.Equal(1, edited.Revision);
        pair.BOnA = HexLinkRights.Edit;
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(pair.PeerA.EditDocument(Path, start.Session, 1, "lost")));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(pair.PeerA.JoinDocument(Path)));
        Assert.Equal(edited, pair.A.Documents.Snapshot(Path));
    }

    [Fact]
    public async Task Disjoint_edits_rebase_and_a_retried_request_does_not_insert_twice()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View | HexLinkRights.Edit);
        var start = pair.A.Documents.Share(Path, "abc");
        pair.A.Documents.Apply(Path, start.Session, 0, "(abc", DeviceA);
        var merged = await pair.PeerA.EditDocument(Path, start.Session, 0, "abc)");
        Assert.False(merged.Conflict);
        Assert.Equal("(abc)", merged.Text);
        Assert.Equal(merged, await pair.PeerA.EditDocument(Path, start.Session, 0, "abc)"));
        Assert.Equal(merged, await pair.PeerA.JoinDocument(Path));
        Assert.Null(await pair.PeerA.PollDocument(Path, merged.Session, merged.Revision));
        Assert.Equal(merged, await pair.PeerA.PollDocument(Path, start.Session, 0));
    }

    [Fact]
    public async Task Two_requests_in_flight_settle_on_one_revision_with_both_edits()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View | HexLinkRights.Edit);
        var start = pair.A.Documents.Share(Path, "middle");
        var first = pair.PeerA.EditDocument(Path, start.Session, 0, "first middle");
        var second = pair.PeerA.EditDocument(Path, start.Session, 0, "middle last");
        var replies = await Task.WhenAll(first, second);
        Assert.All(replies, reply => Assert.False(reply.Conflict));
        var final = await pair.PeerA.JoinDocument(Path);
        Assert.Equal(2, final.Revision);
        Assert.Equal("first middle last", final.Text);
    }

    [Fact]
    public void Same_offset_insertions_follow_host_order_and_overlap_keeps_the_draft()
    {
        var docs = new HexLinkDocuments();
        var start = docs.Share(Path, "abc");
        docs.Apply(Path, start.Session, 0, "aXbc", DeviceA);
        Assert.Equal("aXYbc", docs.Apply(Path, start.Session, 0, "aYbc", DeviceB).Text);
        Assert.Equal("aQbc", docs.Apply(Path, start.Session, 2, "aQbc", DeviceA).Text);
        var conflict = docs.Apply(Path, start.Session, 2, "aRbc", DeviceB);
        Assert.True(conflict.Conflict);
        Assert.Equal("aQbc", conflict.Text);
        Assert.False(HexLinkDocuments.TryMerge("aXYbc", "aRbc", conflict.Text, out string draft));
        Assert.Equal("aRbc", draft);
    }

    [Fact]
    public async Task A_new_connection_recovers_authority_and_old_session_edits_cannot_change_a_reshare()
    {
        using var services = new LinkServices(DeviceA);
        var start = services.Documents.Share(Path, "zero");
        async Task<HexLinkDocSnapshot> Connect(bool edit)
        {
            var (client, server) = Sessions(rightsOfA: () => HexLinkRights.View | HexLinkRights.Edit);
            using var clientEnd = client;
            using var serverEnd = server;
            using var host = new HexLinkHost(server, services);
            server.Requested = host.Handle;
            client.Start();
            server.Start();
            var args = new JsonObject { ["path"] = Path, ["author"] = "forged", ["revision"] = 0L, ["session"] = start.Session, ["text"] = "one" };
            return HexLinkDocumentProtocol.Decode((JsonObject)(await client.Ask(edit ? "doc/edit" : "doc/join", args))!, Path);
        }

        var written = await Connect(true);
        Assert.Equal(DeviceA.Id, written.Author);
        Assert.Equal(written, await Connect(false));
        services.Documents.Close(Path);
        var reopened = services.Documents.Share(Path, "another");
        var conflict = await Connect(true);
        Assert.True(conflict.Conflict);
        Assert.Equal(reopened.Session, conflict.Session);
        Assert.Equal("another", conflict.Text);
    }

    [Fact]
    public void Oversized_fields_and_merged_text_are_rejected_without_changing_the_buffer()
    {
        var docs = new HexLinkDocuments();
        string basis = new('x', HexLinkDocuments.MOST_TEXT - 1);
        var start = docs.Share(Path, basis);
        var accepted = docs.Apply(Path, start.Session, 0, "a" + basis, DeviceA);
        Assert.Equal(HexLinkErrors.TOO_BIG, Assert.Throws<HexLinkException>(() => docs.Apply(Path, start.Session, 0, basis + "b", DeviceB)).Code);
        Assert.Equal(accepted, docs.Snapshot(Path));
        Assert.Throws<HexLinkException>(() => HexLinkDocumentProtocol.Field(new JsonObject { ["text"] = basis + "!!" }, "text", HexLinkDocuments.MOST_TEXT));
    }
}
