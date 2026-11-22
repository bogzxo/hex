using System.Text;
using Hex.Core;
using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

public sealed class HexLinkDocumentSequenceTests
{
    [Fact]
    public void Overlap_resolves_in_either_delivery_order_and_undo_changes_only_its_author()
    {
        var left = new HexLinkDocumentSequence("abcd");
        var right = new HexLinkDocumentSequence("abcd");
        HexDocEdit first = left.View().Edit(new(1, 1), 1, 2, "X");
        HexDocEdit second = right.View().Edit(new(1, 2), 2, 1, "Y");
        left.Apply(first);
        left.Apply(second);
        right.Apply(second);
        right.Apply(first);
        Assert.Equal("aXYd", left.View().Text);
        Assert.Equal(left.View().Text, right.View().Text);

        var undo = new HexDocVisibility(new(2, 1), first.Id, false);
        var redo = new HexDocVisibility(new(3, 1), first.Id, true);
        left.Apply(undo);
        Assert.Equal("abYd", left.View().Text);
        left.Apply(redo);
        right.Apply(redo);
        right.Apply(undo);
        Assert.Equal("aXYd", right.View().Text);
        Assert.Equal(left.View().Text, right.View().Text);
        Assert.Throws<ArgumentException>(() => right.Apply(new HexDocVisibility(new(4, 2), first.Id, false)));
    }

    [Fact]
    public void The_authority_can_hold_one_site_for_every_linked_device()
    {
        var room = new HexLinkDocumentRoom("work/note.txt", "seed");
        room.Join(DeviceA.Id, new object(), DeviceA);
        HexDocExchange? last = null;
        for (int device = 0; device < HexLink.MOST; device++)
        {
            string id = device.ToString("X64", System.Globalization.CultureInfo.InvariantCulture);
            last = room.Join(id, new object(), new HexLinkDevice(id, $"Device {device + 1}"));
        }
        Assert.Equal(64, HexLink.MOST);
        Assert.Equal(65, last!.Participants.Count);
        Assert.Equal(65, last.Participants.Select(participant => participant.Author).Distinct().Count());
        Assert.Throws<InvalidOperationException>(() => room.Join(DeviceC.Id, new object(), DeviceC));
    }

    [Fact]
    public void A_megabyte_seed_uses_one_run_and_rejects_split_surrogates()
    {
        string seed = new string('x', 1_048_576) + "🚀";
        var replica = new HexLinkDocumentSequence(seed);
        Assert.Equal(1, replica.RunCount);
        Assert.Same(seed, replica.View().Text);
        Assert.Single(replica.View().Spans);
        Assert.Throws<ArgumentOutOfRangeException>(() => replica.View().Edit(new(1, 1), seed.Length - 1, 0, "b"));
        Assert.Equal(HexDocApply.Accepted, replica.Apply(replica.View().Edit(new(1, 1), seed.Length - 2, 2, "🏠")));
        Assert.Equal(2, replica.RunCount);
        Assert.Equal(2, replica.View().Spans.Count);
        Assert.EndsWith("🏠", replica.View().Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Distant_remote_edits_patch_only_the_changed_ranges_and_keep_a_caret_between_them()
    {
        var shared = new HexLinkDocumentSequence("prefix\n" + new string('x', 1_048_576) + "\nsuffix");
        var original = shared.View();
        var held = original.PointAt(10);
        shared.Apply(original.Edit(new(1, 1), 0, 1, "P"));
        shared.Apply(original.Edit(new(1, 2), original.Text.Length - 1, 1, "X"));
        var changed = shared.View();
        Assert.Equal(new HexDocSplice[] { new(0, 1, "P"), new(original.Text.Length - 1, 1, "X") }, original.PatchesTo(changed));
        Assert.Equal(10, changed.OffsetOf(held));
    }

    [Fact]
    public void Reordered_dependencies_and_exact_retries_do_not_mutate_the_replica_twice()
    {
        var sender = new HexLinkDocumentSequence("abc");
        var receiver = new HexLinkDocumentSequence("abc");
        HexDocEdit first = sender.View().Edit(new(1, 1), 1, 0, "🐙");
        sender.Apply(first);
        HexDocEdit second = sender.View().Edit(new(2, 1), 3, 0, "!");
        Assert.Equal(HexDocApply.MissingDependency, receiver.Apply(second));
        Assert.Equal("abc", receiver.View().Text);
        Assert.Equal(0, receiver.OperationCount);
        Assert.Equal(HexDocApply.Accepted, receiver.Apply(first));
        Assert.Equal(HexDocApply.Accepted, receiver.Apply(second));
        Assert.Equal(HexDocApply.Duplicate, receiver.Apply(first));
        Assert.Throws<ArgumentException>(() => receiver.Apply(first with { Inserted = "different" }));
        Assert.Equal("a🐙!bc", receiver.View().Text);
    }

    [Fact]
    public void A_checkpoint_waits_for_every_participant_and_a_late_replica_reads_its_tail()
    {
        var authority = new HexLinkDocumentAuthority("hello");
        authority.Reconnect(1);
        authority.Reconnect(2);
        Guid oldEpoch = authority.Checkpoint.Epoch;
        var edit = authority.View().Edit(new(1, 1), 0, 1, "H");
        Assert.Throws<InvalidOperationException>(() => authority.Submit(oldEpoch, 2, edit));
        Assert.Equal(1L, authority.Submit(oldEpoch, 1, edit).Revision);
        Assert.Equal(HexDocApply.Duplicate, authority.Submit(oldEpoch, 1, edit).Outcome);
        authority.Acknowledge(oldEpoch, 1, 1, checkpointReady: true);
        authority.Disconnect(2);
        Assert.Null(authority.Compact());

        authority.Reconnect(2);
        Assert.Single(authority.Replay(oldEpoch, 0).Operations);
        authority.Acknowledge(oldEpoch, 2, 1, checkpointReady: false);
        Assert.Null(authority.Compact());
        authority.Acknowledge(oldEpoch, 2, 1, checkpointReady: true);
        var checkpoint = Assert.IsType<HexDocCheckpoint>(authority.Compact());
        Assert.Equal("Hello", checkpoint.Text);
        Assert.Equal(1L, checkpoint.Revision);
        Assert.NotEqual(oldEpoch, checkpoint.Epoch);
        Assert.Throws<InvalidOperationException>(() => authority.Submit(oldEpoch, 1, edit));

        authority.Submit(checkpoint.Epoch, 1, authority.View().Edit(new(1, 1), 5, 0, "!"));
        authority.Submit(checkpoint.Epoch, 2, authority.View().Edit(new(2, 2), 0, 0, "☀"));
        var replay = authority.Replay(oldEpoch, 0, limit: 1);
        Assert.Equal(checkpoint, replay.Checkpoint);
        Assert.Equal(3L, replay.Head);
        Assert.Single(replay.Operations);
        var late = new HexLinkDocumentSequence(replay.Checkpoint!.Text);
        late.Apply(replay.Operations[0]);
        HexDocReplay next = authority.Replay(replay.Epoch, replay.Since + replay.Operations.Length);
        Assert.Null(next.Checkpoint);
        late.Apply(Assert.Single(next.Operations));
        Assert.Equal("☀Hello!", late.View().Text);
        Assert.Equal(authority.View().Text, late.View().Text);
    }

    [Fact]
    public async Task Wire_rooms_allow_View_readers_bind_authors_and_restore_a_catchup_frontier()
    {
        const string path = "work/note.txt";
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        HexLinkDocumentRoom room = pair.A.Documents.Rooms.Open(path, "ab");
        var localConnection = new object();
        HexDocExchange host = room.Join(DeviceA.Id, localConnection, DeviceA);
        HexDocExchange guest = await pair.PeerA.OpenDocument(path, null, 0);
        Assert.Equal("ab", guest.Replay.Checkpoint!.Text);
        Assert.NotEqual(host.Author, guest.Author);
        Assert.Equal(2, guest.Participants.Count);
        Guid epoch = guest.Replay.Epoch;
        var sender = new HexLinkDocumentSequence("ab");
        HexDocEdit proposed = sender.View().Edit(new(1, guest.Author), 1, 1, "B");
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(pair.PeerA.ExchangeDocument(path, epoch, 0, [proposed], null, null, null, false)));

        HexDocEdit local = sender.View().Edit(new(1, host.Author), 1, 1, "A");
        room.Exchange(DeviceA.Id, localConnection, epoch, 0, [local], null, null, null, false);
        var readerReplay = Assert.IsType<HexDocExchange>(await pair.PeerA.WaitForDocumentRevision(path, epoch, 0, default));
        Assert.False(readerReplay.CanEdit);
        Assert.Single(readerReplay.Replay.Operations);

        pair.BOnA |= HexLinkRights.Edit;
        Assert.Equal(HexLinkErrors.CONFLICT, await CodeOf(pair.PeerA.ExchangeDocument(path, epoch, 0, [local], null, null, null, false)));
        HexDocPoint caret = new(proposed.Id, 1);
        HexDocExchange replay = await pair.PeerA.ExchangeDocument(path, epoch, 0, [proposed], caret, caret, null, false);
        Assert.Equal(2L, replay.Replay.Head);
        Assert.Null(replay.Replay.Checkpoint);
        Assert.Equal(caret, replay.Participants.Single(p => p.Author == guest.Author).Caret);
        foreach (HexDocOperation operation in replay.Replay.Operations)
            sender.Apply(operation);
        Assert.Equal("aBA", sender.View().Text);
        Assert.Equal(room.View().Text, sender.View().Text);
        HexDocExchange rosterOnly = await pair.PeerA.ExchangeDocument(path, epoch, replay.Replay.Head, [],
            knownRoster: replay.RosterVersion);
        Assert.False(rosterOnly.SameRoster);
        Assert.All(rosterOnly.Participants, participant => Assert.Null(participant.Caret));
        HexDocExchange quiet = await pair.PeerA.ExchangeDocument(path, epoch, replay.Replay.Head, [],
            knownRoster: rosterOnly.RosterVersion);
        Assert.True(quiet.SameRoster);
        Assert.Empty(quiet.Participants);
    }

    [Fact]
    public async Task Revision_wait_wakes_for_ordered_edits_without_losing_carets_or_waking_for_presence()
    {
        const string path = "work/note.txt";
        using var pair = await LinkedPair.Up(HexLinkRights.View | HexLinkRights.Edit);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(WAIT_MS));
        HexLinkDocumentRoom room = pair.A.Documents.Rooms.Open(path, "ab");
        var connection = new object();
        var host = new HexLinkDocumentReplica(room.Join(DeviceA.Id, connection, DeviceA));
        var guest = new HexLinkDocumentReplica(await pair.PeerA.OpenDocument(path));
        Guid epoch = host.Epoch;
        var localCaret = host.View.PointAt(0);
        host.Receive(room.Exchange(DeviceA.Id, connection, epoch, 0, [], localCaret, localCaret, null, false));
        var hostWait = room.WaitForRevision(DeviceA.Id, connection, epoch, 0, cancel.Token);
        Assert.Equal(HexLinkErrors.BUSY, await CodeOf(room.WaitForRevision(DeviceA.Id, connection, epoch, 0, cancel.Token)));
        var caret = guest.View.PointAt(1);
        guest.Receive(await pair.PeerA.ExchangeDocument(path, epoch, 0, [], caret, caret));
        Assert.False(hostWait.IsCompleted);

        guest.Capture(guest.View, "aBb");
        guest.Receive(await pair.PeerA.ExchangeDocument(path, epoch, 0, guest.Batch(), updatePresence: false));
        var caught = Assert.IsType<HexDocExchange>(await hostWait);
        Assert.Equal(caret, caught.Participants.Single(person => person.Author == guest.Author).Caret);
        host.Receive(caught);
        Assert.Equal("aBb", host.View.Text);

        var guestWait = pair.PeerA.WaitForDocumentRevision(path, epoch, 1, cancel.Token);
        host.Capture(host.View, "AaBb");
        host.Receive(room.Exchange(DeviceA.Id, connection, epoch, 1, host.Batch(), null, null, null, false));
        var woke = Assert.IsType<HexDocExchange>(await guestWait);
        var late = Assert.IsType<HexDocExchange>(await pair.PeerA.WaitForDocumentRevision(path, epoch, 1, cancel.Token));
        Assert.Equal(woke.Replay.Head, late.Replay.Head);
        guest.Receive(woke);
        Assert.Equal(host.View.Text, guest.View.Text);
    }

    [Fact]
    public async Task An_agent_edits_as_its_own_site_and_human_undo_leaves_its_change()
    {
        const string wire = "work/note.txt";
        using var pair = await LinkedPair.Up(HexLinkRights.Edit | HexLinkRights.View);
        HexLinkDocumentRoom room = pair.A.Documents.Rooms.Open(wire, "seed");
        var actor = new HexLinkDocumentReplica(await pair.PeerA.OpenDocument(wire));
        actor.Capture(actor.View, "Hseed");
        actor.Receive(await pair.PeerA.ExchangeDocument(wire, actor.Epoch, actor.Revision, actor.Batch()));

        var change = new HexDocAgentChange("42", "Fixture", HexDocAgentChange.HashText("Hseed"), new(5, 0, "!"));
        var applied = await pair.PeerA.EditDocumentAsAgent(wire, actor.Epoch, actor.Revision, change);
        actor.Receive(applied);
        var agent = Assert.Single(applied.Participants, participant => participant.Name == "Agent Fixture");
        Assert.NotEqual(actor.Author, agent.Author);
        Assert.Equal(new HexDocPoint(applied.Replay.Operations[^1].Id, 1), agent.Caret);
        Assert.Equal("Hseed!", actor.View.Text);

        Assert.True(actor.ChangeUndo(restoring: false));
        actor.Receive(await pair.PeerA.ExchangeDocument(wire, actor.Epoch, actor.Revision, actor.Batch()));
        Assert.Equal("seed!", actor.View.Text);
        Assert.Equal("seed!", room.View().Text);
        Assert.True(actor.ChangeUndo(restoring: true));
        actor.Receive(await pair.PeerA.ExchangeDocument(wire, actor.Epoch, actor.Revision, actor.Batch()));
        Assert.Equal("Hseed!", actor.View.Text);
    }

    [Fact]
    public async Task A_shared_save_is_idempotent_preserves_encoding_and_refuses_a_new_disk_version()
    {
        const string wire = "work/note.txt";
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        string local = Path.Combine(pair.A.Work, "note.txt");
        var disk = new HexDocFileBasis("old\r\n", new UTF8Encoding(true, true), true);
        await File.WriteAllBytesAsync(local, disk.Bytes(disk.Text!));
        HexLinkDocumentRoom room = pair.A.Documents.Rooms.Open(wire, "seed\n", disk);
        var owner = new object();
        room.Join(DeviceA.Id, owner, DeviceA);
        var remote = new HexLinkDocumentReplica(await pair.PeerA.OpenDocument(wire));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(pair.PeerA.SaveDocument(wire, remote.Epoch, remote.Revision)));
        pair.BOnA |= HexLinkRights.Edit;
        remote.Capture(remote.View, "shared\n");
        remote.Receive(await pair.PeerA.ExchangeDocument(wire, remote.Epoch, remote.Revision, remote.Batch()));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(WAIT_MS));
        var waiting = pair.PeerA.WaitForDocumentRevision(wire, remote.Epoch, remote.Revision, timeout.Token, knownSaved: -1);
        HexDocSaved receipt = await room.Save(DeviceA.Id, owner, local, remote.Epoch, remote.Revision, timeout.Token);
        Assert.Equal(receipt, (await waiting)?.Saved);
        Assert.Equal(receipt, await pair.PeerA.SaveDocument(wire, remote.Epoch, remote.Revision));
        Assert.True(receipt.Matches(HexLinkFiles.Hash(Encoding.UTF8.GetBytes("shared\n")), HexDocSaved.TextDigest("shared\n")));
        Assert.False(receipt.Matches(HexLinkFiles.Hash(Encoding.UTF8.GetBytes("outside\n")), HexDocSaved.TextDigest("outside\n")));
        Assert.Equal(disk.Bytes("shared\r\n"), await File.ReadAllBytesAsync(local));
        remote.Receive(await pair.PeerA.ExchangeDocument(wire, remote.Epoch, remote.Revision, []));
        Assert.Equal(receipt, remote.Saved);

        await File.WriteAllTextAsync(local, "outside");
        remote.Capture(remote.View, "yet newer\n");
        remote.Receive(await pair.PeerA.ExchangeDocument(wire, remote.Epoch, remote.Revision, remote.Batch()));
        Assert.Equal(HexLinkErrors.CONFLICT, await CodeOf(pair.PeerA.SaveDocument(wire, remote.Epoch, remote.Revision)));
        Assert.Equal("outside", await File.ReadAllTextAsync(local));
        Assert.Equal("yet newer\n", room.View().Text);
    }

    [Fact]
    public async Task A_third_device_can_relay_through_one_link_with_rights_checked_at_each_hop()
    {
        const string wire = "work/note.txt";
        using var first = await LinkedPair.Up(HexLinkRights.View);
        using var tablet = new LinkServices(DeviceC);
        using var fromTablet = new HexLink(tablet);
        first.B.DocumentRoute = first.LinkB.PeerOf;
        string path = Path.Combine(first.A.Work, "note.txt");
        var basis = new HexDocFileBasis("draft\r\n", new UTF8Encoding(false, true), true);
        await File.WriteAllBytesAsync(path, basis.Bytes(basis.Text!));
        var room = first.A.Documents.Rooms.Open(wire, "draft\n", basis);
        var (b, c) = Pair();
        HexLinkRights fromC = HexLinkRights.View | HexLinkRights.Edit;
        var tabletAtB = first.LinkB.Add(new HexLinkConnection(b, DeviceC, "memory", true, () => fromC));
        var relay = await fromTablet.Add(new HexLinkConnection(c, DeviceB, "memory", false, () => HexLinkRights.View));
        await tabletAtB;
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(relay.OpenDocument(wire, relayTo: DeviceA.Id)));

        fromC |= HexLinkRights.Steer;
        HexDocExchange opened = await relay.OpenDocument(wire, relayTo: DeviceA.Id);
        Assert.False(opened.CanEdit);
        Assert.Equal(DeviceC.Id, Assert.Single(opened.Participants).Device);
        var replica = new HexLinkDocumentReplica(opened);
        replica.Capture(replica.View, "draft!\n");
        Assert.Equal(HexLinkErrors.NOT_ALLOWED,
            await CodeOf(relay.ExchangeDocument(wire, replica.Epoch, replica.Revision, replica.Batch(), relayTo: DeviceA.Id)));

        first.BOnA |= HexLinkRights.Edit;
        replica.Receive(await relay.ExchangeDocument(wire, replica.Epoch, replica.Revision, replica.Batch(), relayTo: DeviceA.Id));
        Assert.Equal("draft!\n", replica.View.Text);
        HexDocSaved saved = await relay.SaveDocument(wire, replica.Epoch, replica.Revision, relayTo: DeviceA.Id);
        Assert.Equal(basis.Bytes("draft!\r\n"), await File.ReadAllBytesAsync(path));
        Assert.DoesNotContain((await relay.OpenDocument(wire, relayTo: DeviceA.Id)).Participants, person => person.Device == DeviceA.Id);

        replica.Capture(replica.View, "newer\n");
        replica.Receive(await relay.ExchangeDocument(wire, replica.Epoch, replica.Revision, replica.Batch(), relayTo: DeviceA.Id));
        Assert.Equal("newer\n", room.View().Text);
        fromC &= ~HexLinkRights.Edit;
        var unchangedSave = Assert.IsType<HexDocSavedSnapshot>(
            await relay.ReadSavedDocument(wire, includeText: true, default, relayTo: DeviceA.Id));
        Assert.Equal(saved, unchangedSave.Receipt);
        Assert.Equal("draft!\n", unchangedSave.Text);
        fromC &= ~HexLinkRights.Steer;
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(relay.ReadSavedDocument(wire, includeText: true, default, relayTo: DeviceA.Id)));
        Assert.Contains(first.B.Audited, item => item.What == "relay" && item.Detail.Contains(DeviceC.Id, StringComparison.Ordinal));
        Assert.Contains(first.A.Audited, item => item.What == "relay" && item.Detail.Contains(DeviceC.Id, StringComparison.Ordinal));
    }

    [Fact]
    public void A_key_typed_during_replay_uses_the_last_painted_atoms_without_losing_remote_changes()
    {
        var room = new HexLinkDocumentRoom("work/note.txt", "abcde");
        var a = new object();
        var b = new object();
        var alice = new HexLinkDocumentReplica(room.Join(DeviceA.Id, a, DeviceA));
        var bob = new HexLinkDocumentReplica(room.Join(DeviceB.Id, b, DeviceB));
        var alicePainted = alice.Capture(alice.View, "abXcde");
        var bobPainted = bob.Capture(bob.View, "abcQe");
        HexDocPoint bobCaret = bobPainted.PointAt(4);
        var acceptedButLost = Exchange(DeviceA, a, alice, alicePainted.PointAt(3));
        Assert.Equal(1L, acceptedButLost.Replay.Head);
        bob.Receive(Exchange(DeviceB, b, bob, bobCaret));
        Assert.Equal("abXcQe", bob.View.Text);
        var aliceCaughtUp = Exchange(DeviceA, a, alice, alicePainted.PointAt(3));
        alice.Receive(aliceCaughtUp);
        Assert.False(alice.HasPending);

        var latestTyping = alice.Capture(alicePainted, "abXYcde");
        alice.Receive(Exchange(DeviceA, a, alice, latestTyping.PointAt(4)));
        bob.Receive(Exchange(DeviceB, b, bob, bobCaret));
        Assert.Equal("abXYcQe", bob.View.Text);
        Assert.Equal(bob.View.Text, alice.View.Text);
        Assert.Equal(6, alice.View.OffsetOf(bobCaret));
        Assert.Equal(6, alice.View.OffsetOf(new HexDocPoint(default, 4))); // the old 'd' was deleted

        HexDocExchange Exchange(HexLinkDevice device, object connection, HexLinkDocumentReplica replica, HexDocPoint caret) =>
            room.Exchange(device.Id, connection, replica.Epoch, replica.Revision, replica.Batch(), caret, caret, null, false);
    }

    [Fact]
    public void An_epoch_change_preserves_unsent_text_and_gives_the_draft_the_new_shared_version()
    {
        var room = new HexLinkDocumentRoom("work/note.txt", "seed");
        var connection = new object();
        var editor = new HexLinkDocumentReplica(room.Join(DeviceA.Id, connection, DeviceA));
        editor.Capture(editor.View, "unsent");
        var replacement = new HexLinkDocumentRoom(room.Path, "authority");
        var changed = replacement.Join(DeviceA.Id, connection, DeviceA, editor.Epoch, editor.Revision);
        var conflict = Assert.Throws<HexDocEpochConflict>(() => editor.Receive(changed));
        Assert.Equal("authority", conflict.SharedText);
        Assert.Equal("unsent", editor.View.Text);
        Assert.True(editor.HasPending);
    }

    [Fact]
    public void Sixteen_editors_converge_after_random_overlaps_reorder_and_partitions()
    {
        string? overrideCount = Environment.GetEnvironmentVariable("HEX_LINK_CRDT_SEEDS");
        int seeds = int.TryParse(overrideCount, out int requested) ? Math.Clamp(requested, 1, 65_536) : 32;
        for (int seed = 0; seed < seeds; seed++)
            Simulate(seed);
    }

    private static void Simulate(int seed)
    {
        const int editors = 16;
        const string initial = "zero 種 a line\nlast 🚀";
        var random = new Random(seed);
        var authority = new HexLinkDocumentAuthority(initial);
        Guid epoch = authority.Checkpoint.Epoch;
        var model = Enumerable.Range(0, editors).Select(_ => new HexLinkDocumentSequence(initial)).ToArray();
        var connected = Enumerable.Repeat(true, editors).ToArray();
        List<HexDocOperation>[] inbox = Enumerable.Range(0, editors).Select(_ => new List<HexDocOperation>()).ToArray();
        List<HexDocOperation>[] outbox = Enumerable.Range(0, editors).Select(_ => new List<HexDocOperation>()).ToArray();
        List<HexDocId>[] authored = Enumerable.Range(0, editors).Select(_ => new List<HexDocId>()).ToArray();
        for (int editor = 0; editor < editors; editor++)
            authority.Reconnect(editor + 1);

        string[] pieces = ["x", "YZ", "\n", "漢", "🐙", "e\u0301", ""];
        bool authoredOffline = false;
        for (int tick = 0; tick < 160; tick++)
        {
            int editor = random.Next(editors);
            switch (random.Next(8))
            {
                case 0:
                    connected[editor] = !connected[editor];
                    if (connected[editor]) authority.Reconnect(editor + 1);
                    else authority.Disconnect(editor + 1);
                    break;
                case 1:
                    Deliver(editor);
                    break;
                case 2:
                    Send(editor);
                    break;
                default:
                    var id = new HexDocId(model[editor].Clock + 1, editor + 1);
                    HexDocOperation operation;
                    if (authored[editor].Count > 0 && random.Next(7) == 0)
                        operation = new HexDocVisibility(id, authored[editor][random.Next(authored[editor].Count)], random.Next(2) == 0);
                    else
                    {
                        HexDocView before = model[editor].View();
                        int start = BoundaryBefore(before.Text, random.Next(before.Text.Length + 1));
                        int end = BoundaryBefore(before.Text, Math.Min(start + random.Next(7), before.Text.Length));
                        string text = pieces[random.Next(pieces.Length)];
                        if (start == end && text.Length == 0)
                            text = "!";
                        operation = before.Edit(id, start, end - start, text);
                        authored[editor].Add(id);
                    }

                    Assert.Equal(HexDocApply.Accepted, model[editor].Apply(operation));
                    outbox[editor].Add(operation);
                    authoredOffline |= !connected[editor];
                    break;
            }

            Send(random.Next(editors));
            Deliver(random.Next(editors));
            Deliver(random.Next(editors));
        }

        for (int editor = 0; editor < editors; editor++)
        {
            connected[editor] = true;
            authority.Reconnect(editor + 1);
            // Local operations form a causal chain; a client only saw peer edits after the authority accepted them.
            while (outbox[editor].Count > 0)
                Assert.True(Send(editor, ordered: true), $"Authority missing dependency at seed {seed}, editor {editor}.");
        }

        byte[] expected = Encoding.UTF8.GetBytes(authority.View().Text);
        for (int editor = 0; editor < editors; editor++)
        {
            int stalled = 0;
            while (inbox[editor].Count > 0)
            {
                if (Deliver(editor)) stalled = 0;
                else stalled++;
                // Restore deterministic delivery if the random scheduler repeatedly selected an unmet dependency.
                if (stalled > inbox[editor].Count * 4)
                {
                    HexDocOperation next = inbox[editor].MinBy(op => op.Id)!;
                    Assert.NotEqual(HexDocApply.MissingDependency, model[editor].Apply(next));
                    inbox[editor].Remove(next);
                    stalled = 0;
                }
            }
            byte[] actual = Encoding.UTF8.GetBytes(model[editor].View().Text);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"Seed {seed}, editor {editor}, offline drafts {authoredOffline}.");
        }

        bool Send(int editor, bool ordered = false)
        {
            if (!connected[editor] || outbox[editor].Count == 0)
                return false;
            int at = ordered ? 0 : random.Next(outbox[editor].Count);
            HexDocOperation operation = outbox[editor][at];
            var receipt = authority.Submit(epoch, editor + 1, operation);
            if (receipt.Outcome == HexDocApply.MissingDependency)
                return false;
            outbox[editor].RemoveAt(at);
            for (int receiver = 0; receiver < editors; receiver++)
            {
                inbox[receiver].Add(operation);
                if (random.Next(31) == 0)
                    inbox[receiver].Add(operation);
            }
            return true;
        }

        bool Deliver(int editor)
        {
            if (!connected[editor] || inbox[editor].Count == 0)
                return false;
            int at = random.Next(inbox[editor].Count);
            if (model[editor].Apply(inbox[editor][at]) == HexDocApply.MissingDependency)
                return false;
            inbox[editor].RemoveAt(at);
            return true;
        }
    }

    private static int BoundaryBefore(string text, int offset) =>
        offset < text.Length && char.IsLowSurrogate(text[offset]) ? offset - 1 : offset;
}
