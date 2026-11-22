using System.Text;
using System.Text.Json.Nodes;
using Hex.Core;

namespace Hex.Tests;

public sealed class HexLinkHoldStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"hex-hold-{Guid.NewGuid():N}");

    private HexLinkFolder Folder => new("shared", Directory.CreateDirectory(Path.Combine(root, "shared")).FullName);
    private HexLinkHoldStore Open() => new(Folder, Path.Combine(root, "sync"));

    [Fact]
    public void Hold_list_get_and_idempotency()
    {
        HexLinkHoldStore store = Open();
        const string wire = "shared/nested/a.txt";
        int changes = 0;
        store.Changed += () => changes++;
        HexLinkHold held = store.Hold("shared/nested\\a.txt", Bytes("base"), HexLinkHoldBaseSource.Agreed);
        Assert.Equal(wire, held.Path);
        Assert.Equal(HexLinkFiles.Hash(Bytes("base")), held.BaseHash);
        Assert.Equal(HexLinkHoldBaseSource.Agreed, held.BaseSource);
        Assert.True(store.IsHeld(wire));
        Assert.Equal(held, store.Get(wire));
        Assert.Equal(held, Assert.Single(store.List()));
        Assert.Equal(held, store.Hold(wire, Bytes("a different base"), HexLinkHoldBaseSource.Local));
        Assert.Equal(Bytes("base"), store.Read(wire)!.BaseBytes);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Restart_restores_the_persisted_hold_and_incoming()
    {
        const string wire = "shared/a.txt";
        HexLinkHoldStore first = Open();
        first.Hold(wire, Bytes("base"), HexLinkHoldBaseSource.Git);
        first.SetIncoming(wire, Bytes("incoming"), "Peer");
        Assert.True(first.MarkNoticeShown(wire));

        HexLinkHoldSnapshot restarted = Assert.IsType<HexLinkHoldSnapshot>(Open().Read(wire));
        Assert.Equal(first.Get(wire), restarted.Hold);
        Assert.Equal(HexLinkHoldBaseSource.Git, restarted.Hold.BaseSource);
        Assert.Equal("Peer", restarted.Hold.IncomingFrom);
        Assert.True(restarted.Hold.NoticeShown);
        Assert.Equal(Bytes("base"), restarted.BaseBytes);
        Assert.Equal(Bytes("incoming"), restarted.IncomingBytes);
    }

    [Fact]
    public void Latest_incoming_replaces_its_previous_blob()
    {
        const string wire = "shared/a.txt";
        HexLinkHoldStore store = Open();
        store.Hold(wire, Bytes("base"), HexLinkHoldBaseSource.Local);
        store.SetIncoming(wire, Bytes("first"), "Device B");
        var first = store.Get(wire)!;
        store.SetIncoming(wire, Bytes("second"), "Device C");
        var latest = store.Read(wire)!;
        Assert.Equal(Bytes("second"), latest.IncomingBytes);
        Assert.Equal("Device C", latest.Hold.IncomingFrom);
        Assert.Equal(2, Directory.GetFiles(store.BlobDirectory).Length);
        Assert.False(File.Exists(Path.Combine(store.BlobDirectory, first.IncomingHash!)));
        store.SetIncomingDeleted(wire, "Device C");
        Assert.True(store.Get(wire)!.IncomingDeleted);
        Assert.Null(store.Read(wire)!.IncomingBytes);
        Assert.Single(Directory.GetFiles(store.BlobDirectory));
    }

    [Fact]
    public void Release_retires_only_blobs_no_held_file_references()
    {
        HexLinkHoldStore store = Open();
        store.Hold("shared/a.txt", Bytes("shared content"), HexLinkHoldBaseSource.Agreed);
        store.Hold("shared/b.txt", Bytes("shared content"), HexLinkHoldBaseSource.Agreed);
        store.SetIncoming("shared/a.txt", Bytes("incoming"), "Peer");
        Assert.Equal(2, Directory.GetFiles(store.BlobDirectory).Length);
        Assert.True(store.Release("shared/a.txt"));
        Assert.False(store.IsHeld("shared/a.txt"));
        Assert.Single(Directory.GetFiles(store.BlobDirectory));
        Assert.True(store.Release("shared/b.txt"));
        Assert.Empty(Directory.GetFiles(store.BlobDirectory));
        Assert.Empty(Open().List());
    }

    [Fact]
    public void Corrupt_state_is_empty_and_the_original_is_saved_aside()
    {
        HexLinkHoldStore store = Open();
        Directory.CreateDirectory(Path.GetDirectoryName(store.StateFile)!);
        const string broken = "{ broken";
        File.WriteAllText(store.StateFile, broken);
        Assert.Empty(Open().List());
        Assert.False(File.Exists(store.StateFile));
        string saved = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.StateFile)!, "*.corrupt.hexlink"));
        Assert.Equal(broken, File.ReadAllText(saved));
    }

    [Fact]
    public async Task Two_threads_can_hold_different_files_without_losing_state()
    {
        HexLinkHoldStore store = Open();
        await Task.WhenAll(
            Task.Run(() => store.Hold("shared/a.txt", Bytes("a"), HexLinkHoldBaseSource.Agreed)),
            Task.Run(() => store.Hold("shared/b.txt", Bytes("b"), HexLinkHoldBaseSource.Agreed)));
        Assert.Equal(new[] { "shared/a.txt", "shared/b.txt" }, Open().List().Select(held => held.Path));
    }

    [Fact]
    public void Held_paths_use_the_platforms_existing_case_rule()
    {
        HexLinkHoldStore store = Open();
        store.Hold("shared/Upper.txt", Bytes("base"), HexLinkHoldBaseSource.Agreed);
        Assert.True(store.IsHeld("shared/Upper.txt"));
        Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), store.IsHeld("shared/upper.txt"));
    }

    [Fact]
    public void Rebase_clears_incoming_metadata_and_retires_the_previous_base()
    {
        const string wire = "shared/a.txt";
        HexLinkHoldStore store = Open();
        store.Hold(wire, Bytes("old"), HexLinkHoldBaseSource.Local);
        store.SetIncoming(wire, Bytes("new base"), "Peer");
        Assert.True(store.MarkNoticeShown(wire));
        Assert.True(store.Rebase(wire, Bytes("new base")));
        var rebased = Open().Read(wire)!;
        Assert.False(rebased.Hold.HasIncoming);
        Assert.False(rebased.Hold.NoticeShown);
        Assert.Null(rebased.Hold.IncomingFrom);
        Assert.Null(rebased.Hold.IncomingAt);
        Assert.Equal(HexLinkHoldBaseSource.Agreed, rebased.Hold.BaseSource);
        Assert.Equal(Bytes("new base"), rebased.BaseBytes);
        Assert.Single(Directory.GetFiles(store.BlobDirectory));
    }

    [Fact]
    public void Identically_named_folders_at_different_roots_do_not_share_holds()
    {
        HexLinkHoldStore first = Open();
        first.Hold("shared/a.txt", Bytes("base"), HexLinkHoldBaseSource.Agreed);
        var another = new HexLinkFolder(Folder.Name, Directory.CreateDirectory(Path.Combine(root, "another")).FullName);
        var second = new HexLinkHoldStore(another, Path.GetDirectoryName(first.StateFile)!);
        Assert.NotEqual(first.StateFile, second.StateFile);
        Assert.Empty(second.List());
    }

    [Fact]
    public void A_hold_index_and_its_blobs_are_never_addressable_or_in_a_manifest()
    {
        HexLinkFolder folder = Folder;
        var store = new HexLinkHoldStore(folder, folder.Path);
        const string wire = "shared/a.txt";
        File.WriteAllText(Path.Combine(folder.Path, "a.txt"), "working");
        var hold = store.Hold(wire, Bytes("base"), HexLinkHoldBaseSource.Agreed);
        string state = "shared/" + Path.GetFileName(store.StateFile);
        string blob = $"shared/{Path.GetFileName(store.BlobDirectory)}/{hold.BaseHash}";
        Assert.Throws<HexLinkException>(() => HexLinkFiles.Resolve([folder], state));
        Assert.Throws<HexLinkException>(() => HexLinkFiles.Resolve([folder], blob));
        var manifest = new HexLinkTree(folder).Manifest();
        Assert.Equal(wire, Assert.Single(manifest.Files).Path);
        var listing = Assert.IsType<JsonArray>(HexLinkFiles.List([folder], folder.Name)["entries"]);
        Assert.Equal("a.txt", Assert.Single(listing)!["name"]!.GetValue<string>());
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    public void Dispose() => Directory.Delete(root, recursive: true);
}
