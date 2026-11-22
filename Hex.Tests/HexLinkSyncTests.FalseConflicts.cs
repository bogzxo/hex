using System.Text;
using System.Text.Json.Nodes;

using Hex.Core;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

public sealed partial class HexLinkSyncTests
{
    [Fact]
    public async Task First_comparison_agrees_on_utf8_with_and_without_a_bom()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        const string path = "generated.txt";
        byte[] local = HexLinkFiles.Encode("same content\r\n", "utf-8-bom")!;
        byte[] remote = Encoding.UTF8.GetBytes("same content\n");
        File.WriteAllBytes(Path.Combine(pair.A.Work, path), local);
        File.WriteAllBytes(Path.Combine(pair.B.Work, path), remote);

        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);

        Assert.Empty(sync.Conflicts);
        Assert.Equal(0, sync.Sent + sync.Received);
        Assert.Equal(local, File.ReadAllBytes(Path.Combine(pair.A.Work, path)));
        Assert.Equal(remote, File.ReadAllBytes(Path.Combine(pair.B.Work, path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_dirty_editor_conflict_clears_when_the_saved_files_match(bool restart)
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        const string path = "dirty.txt";
        Put(pair.A, path, "last saved");
        Put(pair.B, path, "last saved");
        using var sync = new HexLinkSync(pair.PeerB, pair.A.Workspace.Folders[0], Path.Combine(pair.A.Root, "state"))
        {
            ApplyLocal = (_, wire, _, _, _, _) => throw new HexLinkException(HexLinkErrors.CONFLICT, $"{wire} is dirty",
                new JsonObject { ["editor_dirty"] = true })
        };
        await sync.Start();
        await Settled(sync);
        Put(pair.B, path, "incoming");
        await Settled(sync);
        Assert.True(Assert.Single(sync.Conflicts).HasUnsavedChanges);

        if (restart)
            sync.Stop();
        Put(pair.A, path, "incoming");
        if (restart)
        {
            using HexLinkSync resumed = SyncOf(pair);
            await resumed.Start();
            await Settled(resumed);
            Assert.Empty(resumed.Conflicts);
            Assert.Equal(0, resumed.Sent + resumed.Received);
        }
        else
        {
            await Settled(sync);
            Assert.Empty(sync.Conflicts);
            Assert.Equal(0, sync.Sent + sync.Received);
        }
    }

    [Fact]
    public async Task A_format_only_rewrite_is_unchanged_against_the_base_without_a_merge_cache()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        const string path = "long.txt";
        string basis = new string('a', HexLinkMerge.CACHE_FILE_BYTES) + "\nbase\n";
        Put(pair.A, path, basis);
        Put(pair.B, path, basis);
        using (HexLinkSync first = SyncOf(pair))
        {
            await first.Start();
            await Settled(first);
        }
        Put(pair.A, path, basis.ReplaceLineEndings("\r\n"));
        string edited = basis.Replace("base\n", "edited\n", StringComparison.Ordinal);
        Put(pair.B, path, edited);

        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);

        Assert.Empty(sync.Conflicts);
        Assert.Equal(edited.ReplaceLineEndings("\r\n"), Get(pair.A, path));
        Assert.Equal(edited, Get(pair.B, path));
    }

    [Fact]
    public async Task A_pull_keeps_a_newer_remote_edit_reported_before_the_base_is_saved()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        const string path = "race.txt";
        Put(pair.A, path, "base");
        Put(pair.B, path, "base");
        using var sync = new HexLinkSync(pair.PeerB, pair.A.Workspace.Folders[0], Path.Combine(pair.A.Root, "state"))
        {
            ApplyLocal = async (full, wire, expected, bytes, stop, executable) =>
            {
                string? hash = await HexLinkFiles.CompareAndReplace(full, wire, expected, bytes, stop, executable);
                if (bytes is not null && bytes.AsSpan().SequenceEqual("first edit"u8))
                {
                    Put(pair.B, path, "newest edit");
                    await pair.PeerB.Flush("work", stop);
                }
                return hash;
            }
        };
        await sync.Start();
        await Settled(sync);

        Put(pair.B, path, "first edit");
        await Settled(sync);

        Assert.Equal("newest edit", Get(pair.A, path));
        Assert.Empty(sync.Conflicts);
    }
}
