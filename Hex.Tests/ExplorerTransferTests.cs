using Hex.Dawn;

namespace Hex.Tests;

public class ExplorerTransferTests
{
    [Fact]
    public void Transfers_copy_whole_folders_without_overwrite_and_move_files()
    {
        string root = Path.Combine(Path.GetTempPath(), "hex-transfer-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source"), into = Path.Combine(root, "target");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        Directory.CreateDirectory(into);
        File.WriteAllText(Path.Combine(source, "nested", "beam.csv"), "x,y\r\n1,2\r\n");
        var first = Assert.Single(HexExplorer.Transfer([source], into, false));
        Assert.Null(first.Error);
        Assert.Equal("x,y\r\n1,2\r\n", File.ReadAllText(Path.Combine(first.To!, "nested", "beam.csv")));
        var second = Assert.Single(HexExplorer.Transfer([source], into, false));
        Assert.NotEqual(first.To, second.To);
        Assert.True(Directory.Exists(source));
        string file = Path.Combine(source, "nested", "beam.csv");
        var moved = Assert.Single(HexExplorer.Transfer([file], into, true));
        Assert.Null(moved.Error);
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(moved.To));
    }

    [Fact]
    public void Failed_moves_keep_the_source_and_recursive_transfers_are_rejected()
    {
        string root = Path.Combine(Path.GetTempPath(), "hex-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string child = Path.Combine(root, "child");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(root, "keep.txt"), "keep");
        Assert.NotNull(Assert.Single(HexExplorer.Transfer([root], child, false)).Error);
        Assert.NotNull(Assert.Single(HexExplorer.Transfer([root], child, true)).Error);
        Assert.NotNull(Assert.Single(HexExplorer.Transfer([Path.Combine(root, "keep.txt")], Path.Combine(root, "missing"), true)).Error);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(root, "keep.txt")));
    }
}
