using System.Text;

using Hex.Core;

namespace Hex.Tests;

/// <summary>The store of merge bases kept on the disk by hash: what it keeps, what it refuses, what it lets go.</summary>
public sealed class HexLinkMergeBasesTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "hex-merge-bases-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
        }
    }

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void A_base_comes_back_by_its_hash_also_from_a_store_made_afresh()
    {
        byte[] bytes = Text("one\ntwo\nthree\n");
        string hash = HexLinkFiles.Hash(bytes);

        Assert.True(new HexLinkMergeBases(folder).Add(bytes));
        Assert.True(new HexLinkMergeBases(folder).Add(bytes));

        Assert.Equal(bytes, new HexLinkMergeBases(folder).Get(hash));
        Assert.Null(new HexLinkMergeBases(folder).Get(HexLinkFiles.Hash(Text("other"))));
        Assert.Single(Directory.GetFiles(folder));
    }

    [Fact]
    public void A_file_whose_bytes_are_not_what_its_name_says_is_refused()
    {
        var store = new HexLinkMergeBases(folder);
        byte[] bytes = Text("honest\n");
        string hash = HexLinkFiles.Hash(bytes);
        Assert.True(store.Add(bytes));

        File.WriteAllBytes(Path.Combine(folder, hash), Text("tampered\n"));

        Assert.Null(store.Get(hash));
    }

    [Fact]
    public void Binary_and_oversize_files_are_not_stored()
    {
        var store = new HexLinkMergeBases(folder);
        byte[] binary = [0, 1, 2, 0, 255, 0];
        byte[] big = Text(new string('a', HexLinkMerge.CACHE_FILE_BYTES + 1));

        Assert.False(store.Add(binary));
        Assert.False(store.Add(big));
        Assert.Null(store.Get(HexLinkFiles.Hash(big)));
        Assert.True(!Directory.Exists(folder) || Directory.GetFiles(folder).Length == 0);
    }

    [Fact]
    public void Prune_keeps_only_the_listed_hashes()
    {
        var store = new HexLinkMergeBases(folder);
        byte[] first = Text("first\n"), second = Text("second\n"), third = Text("third\n");
        store.Add(first);
        store.Add(second);
        store.Add(third);

        store.Prune([HexLinkFiles.Hash(second)]);

        Assert.Null(store.Get(HexLinkFiles.Hash(first)));
        Assert.Equal(second, store.Get(HexLinkFiles.Hash(second)));
        Assert.Null(store.Get(HexLinkFiles.Hash(third)));
    }

    [Fact]
    public void A_hash_that_is_not_a_hash_finds_nothing()
    {
        var store = new HexLinkMergeBases(folder);
        store.Add(Text("x\n"));

        Assert.Null(store.Get("../x"));
        Assert.Null(store.Get(""));
    }
}
