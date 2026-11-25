using System.Text;

using Hex.Core;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

public sealed partial class HexLinkSyncTests
{
    private static string Numbered(params (int Line, string Text)[] changes)
    {
        string[] lines = [.. Enumerable.Range(1, 20).Select(n => $"line {n}")];
        foreach (var (line, text) in changes)
            lines[line - 1] = text;
        return string.Join('\n', lines) + "\n";
    }

    [Fact]
    public async Task A_conflicts_base_survives_a_restart_and_the_merge_is_by_region()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        const string path = "both.txt";
        string basis = Numbered((5, "settled five"));
        Put(pair.A, path, Numbered());
        Put(pair.B, path, Numbered());
        string baseHash = HexLinkFiles.Hash(Encoding.UTF8.GetBytes(basis));
        string bases = Path.Combine(pair.A.Root, "state", "merge-bases",
            Path.GetFileNameWithoutExtension(HexLinkSync.StateName(pair.PeerB.Device.Id, pair.A.Workspace.Folders[0].Name)));

        using (HexLinkSync first = SyncOf(pair))
        {
            await first.Start();
            await Settled(first);
            // A real change travelling is what settles the base worth keeping, the first agreement copies nothing
            Put(pair.A, path, basis);
            await Settled(first);
            Assert.Equal(basis, Get(pair.B, path));
            Assert.True(await Until(() => new HexLinkMergeBases(bases).Get(baseHash) is not null), "the base was not stored");
        }

        // Both change it while the sync is off: lines 2 and 10 here, 10 and 18 there, so only line 10 overlaps
        Put(pair.A, path, Numbered((5, "settled five"), (2, "mine two"), (10, "mine ten")));
        Put(pair.B, path, Numbered((5, "settled five"), (10, "theirs ten"), (18, "theirs eighteen")));
        using (HexLinkSync second = SyncOf(pair))
        {
            await second.Start();
            await Settled(second);
            Assert.Equal(baseHash, Assert.Single(second.Conflicts).Base);
        }

        // A fresh sync has no draft of the merge in memory: the base is found again from the conflict's hash
        using HexLinkSync resumed = SyncOf(pair);
        await resumed.Start();
        await Settled(resumed);
        Assert.Equal(baseHash, Assert.Single(resumed.Conflicts).Base);

        var session = await resumed.ConflictMergeSession(pair.A.Workspace.Folders[0].Name + "/" + path, CancellationToken.None);

        Assert.NotNull(session);
        Assert.NotNull(session.Model.Originals?.Base);
        Assert.Equal(1, session.Model.ConflictCount);
    }
}
