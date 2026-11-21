using Hex.Core;

namespace Hex.Tests;

public sealed class HexLinkMultiplayerStressTests
{
    private const string Path = "work/stress.txt";

    [Theory]
    [InlineData(2, 128, 1709)]
    [InlineData(3, 96, 2713)]
    [InlineData(4, 80, 3719)]
    [InlineData(5, 72, 4721)]
    [InlineData(6, 64, 5723)]
    [InlineData(7, 56, 6733)]
    [InlineData(8, 128, 7741)]
    public async Task Stale_writers_preserve_every_disjoint_edit(int writers, int rounds, int seed)
    {
        var documents = new HexLinkDocuments();
        var devices = Enumerable.Range(0, writers).Select(Device).ToArray();
        var lanes = Enumerable.Range(0, writers).Select(writer => $"[lane-{writer}]").ToArray();
        var start = documents.Share(Path, string.Concat(lanes));
        var random = new Random(seed);
        long revision = 0;

        for (int round = 0; round < rounds; round++)
        {
            var basis = documents.Snapshot(Path);
            AssertSnapshot(basis, start.Session, revision, string.Concat(lanes));
            var nextLanes = lanes.Select((_, writer) =>
                $"[lane-{writer}:{round:X4}.{random.Next():X8}|{new string('~', random.Next(1, 10))}🐙e\u0301]").ToArray();
            var proposals = lanes.Select((_, writer) =>
                string.Concat(lanes.Select((lane, index) => index == writer ? nextLanes[writer] : lane))).ToArray();

            var replies = await Race(writers, writer =>
                documents.Apply(Path, basis.Session, basis.Revision, proposals[writer], devices[writer]));

            // Replay the observed commit order with independent lane values. Every intermediate
            // snapshot must retain all accepted edits, including edits made from the stale basis.
            foreach (int writer in Enumerable.Range(0, writers).OrderBy(index => replies[index].Revision))
            {
                var reply = replies[writer];
                Assert.Equal(devices[writer].Id, reply.Author);
                lanes[writer] = nextLanes[writer];
                AssertSnapshot(reply, start.Session, ++revision, string.Concat(lanes));
            }

            int retryWriter = random.Next(writers);
            var expected = replies.MaxBy(reply => reply.Revision)!;
            Assert.Equal(expected, documents.Apply(Path, start.Session, basis.Revision,
                proposals[retryWriter], devices[retryWriter]));
            foreach (var snapshot in await Race(writers, _ => documents.Snapshot(Path)))
                Assert.Equal(expected, snapshot);
        }

        Assert.Equal((long)writers * rounds, revision);
    }

    [Fact]
    public async Task Near_limit_Unicode_insertions_follow_commit_order_and_overflow_is_atomic()
    {
        const int writers = 8;
        var documents = new HexLinkDocuments();
        var devices = Enumerable.Range(0, writers).Select(Device).ToArray();
        var random = new Random(8753);
        var markers = devices.Select((_, writer) => $"<{writer}:{random.Next():X8}:🚀>").ToArray();
        const string graphemes = "漢字e\u0301👩‍🚀";
        int padding = HexLinkDocuments.MOST_TEXT - markers.Sum(marker => marker.Length) - 1;
        string left = string.Concat(Enumerable.Repeat(graphemes, padding / graphemes.Length))
            + new string('界', padding % graphemes.Length);
        const string right = "尾";
        var start = documents.Share(Path, left + right);

        var replies = await Race(writers, writer =>
            documents.Apply(Path, start.Session, start.Revision, left + markers[writer] + right, devices[writer]));

        string inserted = string.Empty;
        long revision = 0;
        foreach (int writer in Enumerable.Range(0, writers).OrderBy(index => replies[index].Revision))
        {
            var reply = replies[writer];
            Assert.Equal(devices[writer].Id, reply.Author);
            inserted += markers[writer];
            AssertSnapshot(reply, start.Session, ++revision, left + inserted + right);
        }

        var full = documents.Snapshot(Path);
        AssertSnapshot(full, start.Session, writers, left + inserted + right);
        Assert.Equal(HexLinkDocuments.MOST_TEXT, full.Text.Length);
        string staleProposal = left + right + "!";
        Assert.True(staleProposal.Length < HexLinkDocuments.MOST_TEXT);
        var overflow = Assert.Throws<HexLinkException>(() =>
            documents.Apply(Path, start.Session, start.Revision, staleProposal, Device(writers)));
        Assert.Equal(HexLinkErrors.TOO_BIG, overflow.Code);
        Assert.Equal(full, documents.Snapshot(Path));

        var shrunk = documents.Apply(Path, start.Session, full.Revision, left + inserted, Device(writers));
        AssertSnapshot(shrunk, start.Session, writers + 1, left + inserted);
        AssertSnapshot(documents.Apply(Path, start.Session, shrunk.Revision, left + inserted + "!", Device(writers)),
            start.Session, writers + 2, left + inserted + "!");
    }

    [Fact]
    public void History_boundary_and_reopen_reject_stale_requests_without_mutation()
    {
        const int historySize = 32;
        var documents = new HexLinkDocuments();
        var worker = Device(0);
        var delayed = Device(1);
        const string anchor = "⟨keep👩‍🚀e\u0301⟩";
        var start = documents.Share(Path, anchor);
        var current = start;
        string body = anchor;
        for (int index = 0; index < historySize - 1; index++)
        {
            body += $"[{index:X2}]";
            current = documents.Apply(Path, start.Session, current.Revision, body, worker);
            AssertSnapshot(current, start.Session, index + 1, body);
        }

        const string marker = "🧪";
        string oldProposal = marker + anchor;
        var retained = documents.Apply(Path, start.Session, 0, oldProposal, delayed);
        AssertSnapshot(retained, start.Session, historySize, marker + body);
        Assert.Equal(retained, documents.Apply(Path, start.Session, 0, oldProposal, delayed));

        var latest = documents.Apply(Path, start.Session, retained.Revision, marker + body + "!", worker);
        AssertSnapshot(latest, start.Session, historySize + 1, marker + body + "!");
        Assert.Equal(latest with { Conflict = true }, documents.Apply(Path, start.Session, 0, oldProposal, delayed));
        Assert.Equal(latest with { Conflict = true }, documents.Apply(Path, start.Session, 0, "new" + anchor, delayed));
        Assert.Equal(latest, documents.Snapshot(Path));

        documents.Close(Path);
        var reopened = documents.Share(Path, anchor);
        Assert.NotEqual(start.Session, reopened.Session);
        AssertSnapshot(reopened, reopened.Session, 0, anchor);
        Assert.Equal(reopened with { Conflict = true }, documents.Apply(Path, start.Session, 0, oldProposal, delayed));
        Assert.Equal(reopened, documents.Snapshot(Path));
        AssertSnapshot(documents.Apply(Path, reopened.Session, 0, oldProposal, delayed),
            reopened.Session, 1, oldProposal);
    }

    [Fact]
    public async Task Overlapping_replacements_conflict_while_a_disjoint_stale_edit_survives()
    {
        const string basis = "α[ABCDEF]ω";
        string[] proposals = ["α[AXEF]ω", "α[ABYF]ω", "α[ABCDEF]ω🚀"];
        for (int round = 0; round < 32; round++)
        {
            var documents = new HexLinkDocuments();
            var start = documents.Share(Path, basis);
            var replies = await Race(proposals.Length, writer =>
                documents.Apply(Path, start.Session, start.Revision, proposals[writer], Device(writer)));

            Assert.False(replies[2].Conflict);
            Assert.NotEqual(replies[0].Conflict, replies[1].Conflict);
            int winner = replies[0].Conflict ? 1 : 0;
            int loser = 1 - winner;
            var committed = new[] { (Writer: winner, Reply: replies[winner]), (Writer: 2, Reply: replies[2]) }
                .OrderBy(commit => commit.Reply.Revision).ToArray();
            string acceptedText = basis;
            long revision = 0;
            foreach (var commit in committed)
            {
                acceptedText = commit.Writer == 2 ? acceptedText + "🚀"
                    : proposals[winner] + (revision == 0 ? string.Empty : "🚀");
                Assert.Equal(Device(commit.Writer).Id, commit.Reply.Author);
                AssertSnapshot(commit.Reply, start.Session, ++revision, acceptedText);
            }

            var conflict = replies[loser];
            Assert.InRange(conflict.Revision, replies[winner].Revision, 2L);
            Assert.Equal(committed[(int)conflict.Revision - 1].Reply with { Conflict = true }, conflict);
            string finalText = proposals[winner] + "🚀";
            AssertSnapshot(documents.Snapshot(Path), start.Session, 2, finalText);
            Assert.Equal(documents.Snapshot(Path) with { Conflict = true },
                documents.Apply(Path, start.Session, 0, proposals[loser], Device(loser)));
            AssertSnapshot(documents.Snapshot(Path), start.Session, 2, finalText);
        }
    }

    private static HexLinkDevice Device(int writer) => new($"writer-{writer}", $"Writer {writer}");

    private static async Task<T[]> Race<T>(int writers, Func<int, T> apply)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, writers).Select(Run).ToArray();
        start.SetResult();
        return await Task.WhenAll(tasks);

        async Task<T> Run(int writer)
        {
            await start.Task.ConfigureAwait(false);
            return apply(writer);
        }
    }

    private static void AssertSnapshot(HexLinkDocSnapshot snapshot, string session, long revision, string text)
    {
        Assert.False(snapshot.Conflict);
        Assert.Equal(Path, snapshot.Path);
        Assert.Equal(session, snapshot.Session);
        Assert.Equal(revision, snapshot.Revision);
        Assert.Equal(text, snapshot.Text);
    }
}
