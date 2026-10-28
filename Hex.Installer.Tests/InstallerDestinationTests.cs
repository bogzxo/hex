namespace Hex.Installer.Tests;

public sealed class InstallerDestinationTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "hex-destination-" + Guid.NewGuid().ToString("N"));
    public InstallerDestinationTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void UsesSelectedDirectoryForWindowsAndLinux()
    {
        string selected = Path.Combine(folder, "My Hex");
        Assert.Equal(selected, InstallerDestination.FromFolder(selected, macOS: false));
    }

    [Fact]
    public void ResolvesMacApplicationBundleWithoutNestingBundles()
    {
        string expected = Path.Combine(folder, "Hex.app", "Contents", "MacOS");
        Assert.Equal(expected, InstallerDestination.FromFolder(folder, macOS: true));
        Assert.Equal(expected, InstallerDestination.FromFolder(Path.Combine(folder, "Hex.app"), macOS: true));
        Assert.Equal(expected, InstallerDestination.FromFolder(expected, macOS: true));
    }

    [Fact]
    public void RejectsNonemptyPersonalFoldersBeforeChangingDestination()
    {
        File.WriteAllText(Path.Combine(folder, "personal.txt"), "keep");
        Assert.Throws<IOException>(() => InstallerDestination.FromFolder(folder, macOS: false));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(folder, "personal.txt")));
    }
}
