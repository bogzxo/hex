using System.Text;

using Hex.Core;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

public sealed partial class HexLinkSyncTests
{
    [Theory]
    [InlineData(".DS_Store")]
    [InlineData("sub/._asset.png")]
    [InlineData(".Spotlight-V100/db/index")]
    [InlineData(".Trashes/1/deleted")]
    [InlineData(".fseventsd/log")]
    [InlineData("sub/THUMBS.DB")]
    [InlineData("ehthumbs.db")]
    [InlineData("sub/Desktop.ini")]
    [InlineData("$RECYCLE.BIN/user/file")]
    public void Operating_system_files_have_their_own_jail_rule(string path)
    {
        using var local = new LinkServices(DeviceA);
        HexLinkFolder folder = local.Workspace.Folders[0];
        var noEditorExclusions = new HexExcludes([], [], false);

        Assert.Throws<HexLinkException>(() => HexLinkFiles.Resolve([folder], "work/" + path, noEditorExclusions));
        string allowed = HexLinkFiles.Resolve([folder with { IncludeSystemFiles = true }], "work/" + path, noEditorExclusions);
        Assert.Equal(Path.Combine(local.Work, path.Replace('/', Path.DirectorySeparatorChar)), allowed);
        Assert.False(HexLinkSystemFiles.Matches("editor.swp"));
        Assert.False(HexLinkSystemFiles.Matches("notes~"));
        Assert.False(HexLinkSystemFiles.Matches("src/desktop.ini.template"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sync_reports_existing_operating_system_copies_but_never_writes_or_removes_them(bool bulk)
    {
        using var pair = bulk ? new LinkedPair(EDIT, EDIT) : await LinkedPair.Up(EDIT, EDIT);
        HexLinkPeer peer = bulk ? await PackedPeer(pair) : pair.PeerB;
        if (!NoGit)
            Repositories(pair);
        Put(pair.A, "sub/._both", "A copy");
        Put(pair.B, "sub/._both", "B copy");
        Put(pair.B, "sub/.DS_Store", "B Finder data");
        Put(pair.A, "ordinary.txt", "A");

        using var sync = SyncOf(pair, peer);
        await sync.Start();
        await Settled(sync);
        Put(pair.A, "sub/._new", "created after startup");
        Put(pair.A, "next.txt", "A later");
        await Settled(sync);

        Assert.Equal("A", Get(pair.B, "ordinary.txt"));
        Assert.Equal("A later", Get(pair.B, "next.txt"));
        Assert.Equal("A copy", Get(pair.A, "sub/._both"));
        Assert.Equal("B copy", Get(pair.B, "sub/._both"));
        Assert.Equal("B Finder data", Get(pair.B, "sub/.DS_Store"));
        Assert.Null(Get(pair.A, "sub/.DS_Store"));
        Assert.Null(Get(pair.B, "sub/._new"));
        Assert.Contains(sync.Skipped, row => row.Path == "work/sub/._both" && row.Why.Contains("both devices"));
        Assert.Contains(sync.Skipped, row => row.Path == "work/sub/.DS_Store" && row.Why.Contains(pair.B.Device.Name));
        Assert.Contains(sync.Skipped, row => row.Path == "work/sub/._new");
        Assert.Empty(sync.Conflicts);

        if (bulk)
        {
            var proposed = Encoding.UTF8.GetBytes("a packed write");
            var answer = await peer.WriteFilePack("work", [new HexLinkFilePack.Entry("work/sub/._both", proposed,
                HexLinkFiles.Hash(Encoding.UTF8.GetBytes("B copy")))], CancellationToken.None);
            Assert.Equal(HexLinkErrors.NOT_ALLOWED, answer["work/sub/._both"].Error?.Code);
            Assert.Equal("B copy", Get(pair.B, "sub/._both"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Operating_system_file_sync_takes_an_opt_in_on_both_devices(bool receiverIncludes)
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        if (!NoGit)
            Repositories(pair);
        IncludeSystemFiles(pair.A, true);
        IncludeSystemFiles(pair.B, receiverIncludes);
        Put(pair.A, "._chosen", "explicitly opted in");
        using var sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);
        Assert.Equal("explicitly opted in", Get(pair.A, "._chosen"));
        Assert.Equal(receiverIncludes ? "explicitly opted in" : null, Get(pair.B, "._chosen"));
        Assert.Equal(!receiverIncludes, sync.Skipped.Any(row => row.Path == "work/._chosen"));
    }

    [Fact]
    public async Task Get_reports_operating_system_files_and_leaves_them_on_the_source()
    {
        if (NoGit)
            return;
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Repositories(pair);
        Put(pair.A, "._finder", "untracked metadata");
        Put(pair.A, "keep.txt", "untracked content");
        string destination = Path.Combine(pair.B.Root, "download");
        using var limit = new CancellationTokenSource(TimeSpan.FromMilliseconds(LONG_MS));
        HexLinkProvisionResult got = await HexLinkProvision.Get(pair.PeerA, destination,
            Path.Combine(pair.B.Root, "get-journal.json"), null, limit.Token);

        string root = Assert.Single(got.Folders).Path;
        Assert.Equal("untracked content", File.ReadAllText(Path.Combine(root, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(root, "._finder")));
        Assert.Equal("untracked metadata", Get(pair.A, "._finder"));
        Assert.Contains(got.Skipped, skip => skip.Path == "._finder" && skip.File);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Operating_system_file_claims_follow_the_folder_policy(bool include)
    {
        using var local = new LinkServices(DeviceA);
        IncludeSystemFiles(local, include);
        local.Workspace = local.Workspace with { Folders = [local.Workspace.Folders[0] with
            { Authority = new HexLinkAuthority(DeviceA.Id, 1) }] };
        using var link = new HexLink(local);
        link.Claims.RefreshFolders();
        var scope = Assert.Single(link.Claims.Folders).Scope;
        var key = new HexLinkClaimKey("notes/._metadata", false, 1, HexLinkClaimKind.Planned);
        link.Claims.Update(new Dictionary<HexLinkClaimScope, Dictionary<HexLinkClaimKey, HexLinkClaimDraft>>
        {
            [scope] = new() { [key] = new(key, "Test") }
        });

        Assert.Equal(include, link.Claims.Board.At(scope, key.Path, false, Environment.TickCount64).Count == 1);
    }

    private static void IncludeSystemFiles(LinkServices side, bool include) =>
        side.Workspace = side.Workspace with { Folders = [side.Workspace.Folders[0] with { IncludeSystemFiles = include }] };
}
