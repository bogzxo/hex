using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Hex.Installation;

namespace Hex.Installer.Tests;

public sealed class WindowsSetupBundleTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "hex-bundle-test-" + Guid.NewGuid().ToString("N"));
    public WindowsSetupBundleTests() => Directory.CreateDirectory(home);
    public void Dispose() => Directory.Delete(home, true);

    private static MemoryStream Bundle(params (string Name, string Contents)[] extra)
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, true))
            foreach (var (name, contents) in new[] { (InstallPaths.InstallerExecutable, "native setup"), ("Assets/installer/setup.hor", "editable layout") }.Concat(extra))
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(contents);
            }
        var result = new MemoryStream();
        result.Write("native executable prefix"u8);
        result.Write(zip.ToArray());
        result.Write(SHA256.HashData(zip.ToArray()));
        using (var writer = new BinaryWriter(result, Encoding.UTF8, true)) writer.Write(zip.Length);
        result.Write("HexSetupBundle01"u8);
        result.Position = 0;
        return result;
    }

    [Fact]
    public void ExtractsNextToLauncherPreservingAssetsAndCleansOnlyItsOwnFolder()
    {
        string downloads = Path.Combine(home, "Downloads with spaces");
        Directory.CreateDirectory(downloads);
        string launcher = Path.Combine(downloads, "Renamed Hex Setup.exe");
        File.WriteAllText(launcher, "keep the download");
        File.WriteAllText(Path.Combine(downloads, "personal.txt"), "keep personal files");
        string folder = WindowsSetupBundle.CreateFolder(launcher);
        string other = WindowsSetupBundle.CreateFolder(launcher);
        try
        {
            Assert.Equal(downloads, Path.GetDirectoryName(folder));
            Assert.NotEqual(folder, other);
            using var source = Bundle(("Assets/installer/localisation/en-gb.hor", "editable texts"));
            Assert.True(WindowsSetupBundle.ReadFooter(source, out long length, out byte[] digest));
            WindowsSetupBundle.Extract(source, length, digest, folder);
            Assert.Equal("native setup", File.ReadAllText(Path.Combine(folder, InstallPaths.InstallerExecutable)));
            Assert.Equal("editable texts", File.ReadAllText(Path.Combine(folder, "Assets/installer/localisation/en-gb.hor")));
            Assert.Empty(Directory.GetFiles(folder, "*.zip"));
            WindowsSetupBundle.DeleteFolder(folder);
            WindowsSetupBundle.DeleteFolder(folder);
            Assert.False(Directory.Exists(folder));
            Assert.True(Directory.Exists(other));
            Assert.Equal("keep the download", File.ReadAllText(launcher));
            Assert.True(File.Exists(Path.Combine(downloads, "personal.txt")));
        }
        finally { WindowsSetupBundle.DeleteFolder(other); }
    }

    [Fact]
    public void RawExecutableHasNoBundleAndCorruptLengthIsRejected()
    {
        using var raw = new MemoryStream(new byte[100]);
        Assert.False(WindowsSetupBundle.ReadFooter(raw, out _, out _));
        using var source = Bundle();
        source.Position = source.Length - 24;
        using (var writer = new BinaryWriter(source, Encoding.UTF8, true)) writer.Write(long.MaxValue);
        Assert.Throws<InvalidDataException>(() => WindowsSetupBundle.ReadFooter(source, out _, out _));
    }

    [Fact]
    public void DamagedPayloadIsRejectedBeforeExtractingAnyFiles()
    {
        using var source = Bundle();
        Assert.True(WindowsSetupBundle.ReadFooter(source, out long length, out byte[] digest));
        long start = source.Position;
        source.WriteByte(0);
        source.Position = start;
        string folder = WindowsSetupBundle.CreateFolder(Path.Combine(home, "setup.exe"));
        Assert.Throws<InvalidDataException>(() => WindowsSetupBundle.Extract(source, length, digest, folder));
        Assert.Empty(Directory.GetFileSystemEntries(folder));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("Assets/../../outside")]
    [InlineData("C:/outside")]
    [InlineData("/outside")]
    [InlineData("Assets\\..\\outside")]
    [InlineData("file:stream")]
    [InlineData("assets/installer/SETUP.hor")]
    public void RejectsUnsafeAndDuplicatePayloadPaths(string name)
    {
        using var source = Bundle((name, "invalid"));
        Assert.True(WindowsSetupBundle.ReadFooter(source, out long length, out byte[] digest));
        string folder = WindowsSetupBundle.CreateFolder(Path.Combine(home, "setup.exe"));
        Assert.Throws<InvalidDataException>(() => WindowsSetupBundle.Extract(source, length, digest, folder));
        Assert.False(File.Exists(Path.Combine(home, "outside")));
        WindowsSetupBundle.DeleteFolder(folder);
    }

    [Fact]
    public void RejectsSymbolicLinksInThePayload()
    {
        // Build a separate archive so its external file type is a Unix symbolic link.
        using var archiveBytes = new MemoryStream();
        using (var archive = new ZipArchive(archiveBytes, ZipArchiveMode.Create, true))
        {
            var link = archive.CreateEntry("link");
            link.ExternalAttributes = 0xa1ff << 16;
        }
        archiveBytes.Position = 0;
        string folder = WindowsSetupBundle.CreateFolder(Path.Combine(home, "setup.exe"));
        Assert.Throws<InvalidDataException>(() => WindowsSetupBundle.Extract(archiveBytes, archiveBytes.Length, SHA256.HashData(archiveBytes.ToArray()), folder));
    }

    [Fact]
    public void CleanupRequiresAnOwnedTemporaryCopy()
    {
        string source = Path.Combine(home, "installer");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, InstallPaths.InstallerExecutable), "setup");
        string copied = InstallerHandoff.CopyInstaller(source);
        string folder = Path.GetDirectoryName(copied)!;
        try
        {
            Assert.True(InstallerHandoff.IsInstallerCopy(folder));
            Assert.False(InstallerHandoff.IsInstallerCopy(source));
            File.WriteAllText(Path.Combine(folder, ".hex-setup-copy"), "wrong owner");
            Assert.False(InstallerHandoff.IsInstallerCopy(folder));
        }
        finally { Directory.Delete(folder, true); }
    }
}
