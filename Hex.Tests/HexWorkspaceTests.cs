using Hex.Core;

namespace Hex.Tests;

public sealed class HexWorkspaceTests
{
    [Fact]
    public void WatchExpressionsRoundTripInOrderAndLegacyWorkspacesClearThem()
    {
        var workspace = new HexWorkspace();
        workspace.Watches.AddRange(["items[0].Name", "value + 1", "lookup[\"key\"]"]);
        var restored = new HexWorkspace();
        restored.Read(workspace.Write(Path.GetTempPath()), Path.GetTempPath());
        Assert.Equal(workspace.Watches, restored.Watches);
        restored.Read("let workspace = { folders: [] };", Path.GetTempPath());
        Assert.Empty(restored.Watches);
    }

    [Fact]
    public void TheRootRemainsAWholePathThroughSaveAndRestore()
    {
        string root = Path.GetPathRoot(Path.GetFullPath("."))!;
        string nested = Path.Combine(root, "src");
        var workspace = new HexWorkspace();
        Assert.True(workspace.Add(root));
        Assert.Equal(root, workspace.Name);
        Assert.False(workspace.Add(root));
        Assert.True(workspace.Add(nested));

        var restored = new HexWorkspace();
        restored.Read(workspace.Write(null), nested);
        Assert.Equal(root, restored.FolderOf(Path.Combine(root, "src-other", "file.txt")));
        Assert.Equal(nested, restored.FolderOf(Path.Combine(nested, "file.txt")));
        Assert.Contains(root, restored.Expanded);
        Assert.True(restored.Remove(root));
        Assert.Null(restored.FolderOf(Path.Combine(root, "src-other")));
    }

    [Fact]
    public void OlderWindowsWorkspacesCanNameADriveWithoutItsLastSlash()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var workspace = new HexWorkspace();
        workspace.Read("""let workspace = { folders: ["C:"], expanded: ["C:"] };""", Path.GetFullPath("."));
        Assert.Equal(@"C:\", Assert.Single(workspace.Folders));
        Assert.Equal(@"C:\", Assert.Single(workspace.Expanded));
    }
}
