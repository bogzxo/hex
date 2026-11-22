using System.Text.Json.Nodes;
using Hex.Core;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

public sealed partial class HexLinkSyncTests
{
    [Theory]
    [InlineData("legacy")]
    [InlineData("branch")]
    [InlineData("checkout")]
    [InlineData("authority")]
    [InlineData("unchanged")]
    public async Task Stale_tracked_agreement_uses_the_current_HEAD_but_keeps_untracked_bases(string reason)
    {
        if (NoGit)
            return;

        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Repositories(pair);
        var authority = new HexLinkAuthority(new string('A', 64), 1);
        if (reason == "authority")
            pair.B.Workspace = pair.B.Workspace with { Folders = [pair.B.Workspace.Folders[0] with { Authority = authority }] };
        Put(pair.A, "readme.md", "prior agreement\n");
        Put(pair.B, "readme.md", "prior agreement\n");
        Put(pair.A, "untracked.txt", "prior untracked");
        Put(pair.B, "untracked.txt", "prior untracked");
        string stateFile;
        using (HexLinkSync original = SyncOf(pair))
        {
            await original.Start();
            await Settled(original);
            stateFile = original.StateFile;
            await original.StopForRepositoryRefresh();
        }

        HexLinkFolder local = pair.A.Workspace.Folders[0];
        string clean = reason == "unchanged" ? "prior agreement\n" : "one\n";
        if (reason == "branch")
        {
            clean = "branch base\n";
            Git(pair.A.Work, "switch", "-qc", "new-branch");
            Put(pair.A, "readme.md", clean);
            Git(pair.A.Work, "commit", "-qam", "New branch");
            Git(pair.B.Work, "fetch", "-q", pair.A.Work, "new-branch");
            Git(pair.B.Work, "reset", "--hard", "FETCH_HEAD");
        }
        else if (reason == "checkout")
        {
            string checkout = Path.Combine(pair.A.Root, "different-checkout");
            Git(pair.A.Root, "clone", "-q", "-c", "core.autocrlf=false", pair.A.Work, checkout);
            local = local with { Path = checkout };
        }
        else if (reason == "legacy")
        {
            JsonObject snapshot = (JsonObject)JsonNode.Parse(File.ReadAllText(stateFile))!;
            snapshot.Remove("agreement");
            File.WriteAllText(stateFile, snapshot.ToJsonString());
        }
        else if (reason == "authority")
        {
            pair.B.Workspace = pair.B.Workspace with { Folders = [pair.B.Workspace.Folders[0] with { Authority = authority with { Epoch = 2 } }] };
        }

        string edited = reason == "branch" ? "prior agreement\n" : "new tracked edit\n";
        File.WriteAllText(Path.Combine(local.Path, "readme.md"), edited);
        File.WriteAllText(Path.Combine(local.Path, "untracked.txt"), "new untracked edit");
        Put(pair.B, "readme.md", clean);
        using var restarted = new HexLinkSync(pair.PeerB, local, Path.GetDirectoryName(stateFile)!)
        {
            Quiet = TimeSpan.FromMilliseconds(50),
            Rescan = TimeSpan.FromSeconds(2)
        };
        await restarted.Start();
        await Settled(restarted);

        Assert.Equal(edited, Get(pair.B, "readme.md"));
        Assert.Equal(edited, File.ReadAllText(Path.Combine(local.Path, "readme.md")));
        Assert.Equal("new untracked edit", Get(pair.B, "untracked.txt"));
        Assert.Empty(restarted.Conflicts);
        Assert.DoesNotContain(Directory.EnumerateFiles(local.Path), file => file.Contains(HexLinkSync.THEIRS, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_initial_tracked_delete_and_edit_remain_a_conflict()
    {
        if (NoGit)
            return;

        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Repositories(pair);
        File.Delete(Path.Combine(pair.A.Work, "readme.md"));
        Put(pair.B, "readme.md", "edited on the other side\n");
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);

        Assert.False(Has(pair.A, "readme.md"));
        Assert.Equal("edited on the other side\n", Get(pair.B, "readme.md"));
        Assert.Contains(sync.Conflicts, conflict => conflict.Path == "work/readme.md");
    }
}
