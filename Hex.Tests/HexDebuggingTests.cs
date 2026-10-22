using System.Formats.Tar;
using System.IO.Compression;

using Hex.Dawn;

namespace Hex.Tests;

/// <summary>
/// netcoredbg as Hex installs it, a release's zip or .tar.gz unpacked into a folder of its own so the netcoredbg folder
/// inside is where Hex looks for the debugger, and which of its files fits this machine.
/// </summary>
public sealed class HexDebuggingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hex-debugging-" + Guid.NewGuid().ToString("N"));

    public HexDebuggingTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    // A release's archive as netcoredbg's are, everything in a netcoredbg folder
    private string Packed(bool zip)
    {
        string made = Path.Combine(root, "made", "netcoredbg");
        Directory.CreateDirectory(made);
        File.WriteAllText(Path.Combine(made, "netcoredbg.exe"), "pretend");

        string archive = Path.Combine(root, zip ? "netcoredbg-win64.zip" : "netcoredbg-linux-amd64.tar.gz");
        File.Delete(archive);
        if (zip)
        {
            ZipFile.CreateFromDirectory(Path.Combine(root, "made"), archive);
            return archive;
        }

        using FileStream file = File.Create(archive);
        using var zipped = new GZipStream(file, CompressionLevel.Fastest);
        TarFile.CreateFromDirectory(Path.Combine(root, "made"), zipped, includeBaseDirectory: false);
        return archive;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_release_unpacks_into_the_netcoredbg_folder_hex_looks_in(bool zip)
    {
        string into = Path.Combine(root, "home");
        HexDebugging.Unpack(Packed(zip), into);
        Assert.Equal("pretend", File.ReadAllText(Path.Combine(into, "netcoredbg", "netcoredbg.exe")));

        // Again, over what's there, the way an update would
        HexDebugging.Unpack(Packed(zip), into);
        Assert.True(File.Exists(Path.Combine(into, "netcoredbg", "netcoredbg.exe")));
    }

    [Fact]
    public void This_machine_has_a_file_of_netcoredbgs_releases_or_is_sent_to_them()
    {
        string? asset = HexDebugging.NetcoredbgAsset();
        if (OperatingSystem.IsWindows() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64)
            Assert.Equal("netcoredbg-win64.zip", asset);
        else
            Assert.True(asset is null || asset.EndsWith(".tar.gz", StringComparison.Ordinal));
    }
}
