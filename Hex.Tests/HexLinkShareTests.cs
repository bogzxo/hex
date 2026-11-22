using System.Text.Json.Nodes;

using Hex.Core;
using Hex.Dawn;

namespace Hex.Tests;

/// <summary>
/// Sharing the code over Hex Link as the editor does it, what can be tested without a window: where another machine's
/// file is copied to here and the paths refused on the way, the files a device's hex_files answer names, the folders
/// kept as synced per device, and the colour a device is drawn in. The rest (a sync, a remote tab, presence, a clone)
/// is the link-files self test's, which needs two Hexes.
/// </summary>
public class HexLinkShareTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "hex-link-share-" + Guid.NewGuid().ToString("N"));

    public HexLinkShareTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void ARemoteFileIsCopiedUnderItsDevicesFolderByItsWorkspaceFolder()
    {
        string copy = DawnScene.LinkRemoteCopyIn(folder, "hex/Hex.Dawn/Link/DawnScene.Link.cs");
        Assert.True(Horizon.Core.Platform.PlatformPaths.IsUnder(copy, folder));
        Assert.EndsWith(Path.Combine("Hex.Dawn", "Link", "DawnScene.Link.cs"), copy);

        // Two workspace folders whose names only differ in what's made safe still get folders of their own
        string other = DawnScene.LinkRemoteCopyIn(folder, "Hex/Hex.Dawn/Link/DawnScene.Link.cs");
        Assert.NotEqual(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(copy))), Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(other))));
    }

    [Theory]
    [InlineData("hex/../../outside.txt")]
    [InlineData("hex/.git/config")]
    [InlineData("/hex/file.txt")]
    [InlineData("hex\\file.txt")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("file.txt")]
    [InlineData("hex/CON.txt")]
    public void APathThatLeavesItsFolderIsNeverCopiedHere(string wire)
    {
        var refused = Assert.Throws<HexLinkException>(() => DawnScene.LinkRemoteCopyIn(folder, wire));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, refused.Code);
    }

    [Fact]
    public void TheFilesADevicesHexFilesNamesAreWirePaths()
    {
        const string said = "3 files fit link, best first.\nHex.Dawn/Link/DawnScene.Link.cs  603 lines\ndocs/hex-link-plan.md  371 lines\n(index generation 6)\n";
        Assert.Equal(["hex/Hex.Dawn/Link/DawnScene.Link.cs", "hex/docs/hex-link-plan.md"], DawnScene.LinkFilesFound(["hex"], said));

        // In a workspace of several folders a path already starting with one of them is one
        const string several = "engine/Horizon/Core.cs  10 lines\nREADME.md  1 line\n";
        Assert.Equal(["engine/Horizon/Core.cs", "hex/README.md"], DawnScene.LinkFilesFound(["hex", "engine"], several));
        Assert.Empty(DawnScene.LinkFilesFound([], several));
    }

    [Fact]
    public void AFoldersSyncIsKeptOncePerDeviceAndForgottenWhenItsStopped()
    {
        JsonObject kept = DawnScene.LinkSyncChoicesWith([], "device-a", "hex", on: true);
        kept = DawnScene.LinkSyncChoicesWith(Choices(kept), "device-a", "hex", on: true);
        kept = DawnScene.LinkSyncChoicesWith(Choices(kept), "device-b", "hex", on: true);
        kept = DawnScene.LinkSyncChoicesWith(Choices(kept), "device-a", "engine", on: true);
        Assert.Equal(["hex", "engine"], DawnScene.LinkSyncChosenIn(Choices(kept), "device-a"));
        Assert.Equal(["hex"], DawnScene.LinkSyncChosenIn(Choices(kept), "device-b"));

        kept = DawnScene.LinkSyncChoicesWith(Choices(kept), "device-a", "hex", on: false);
        Assert.Equal(["engine"], DawnScene.LinkSyncChosenIn(Choices(kept), "device-a"));
        Assert.Equal(["hex"], DawnScene.LinkSyncChosenIn(Choices(kept), "device-b"));
        Assert.Empty(DawnScene.LinkSyncChosenIn(Choices(kept), "device-c"));
    }

    private static List<JsonObject> Choices(JsonObject kept) =>
        [.. ((JsonArray)kept["syncs"]!).Select(each => (JsonObject)each!.DeepClone())];

    [Fact]
    public void ADeviceIsAlwaysDrawnInTheSameColour()
    {
        string id = new('a', 64);
        Assert.Equal(DawnScene.LinkDeviceColour(id), DawnScene.LinkDeviceColour(id));
        var colours = Enumerable.Range(0, 40).Select(i => DawnScene.LinkDeviceColour(HexLinkFiles.Hash([(byte)i]))).Distinct().ToList();
        Assert.True(colours.Count > 3);
        Assert.All(colours, colour => Assert.Equal(1.0f, colour.W));
    }
}
