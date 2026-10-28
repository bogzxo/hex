using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Hex.Installation;

namespace Hex.Installer.Tests;

public sealed class InstallationTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "hex-installer-test-" + Guid.NewGuid().ToString("N"));
    public InstallationTests() => Directory.CreateDirectory(home);
    public void Dispose() => Directory.Delete(home, true);

    private static string Release(string digest, string asset = "Hex-win-x64.zip", string tag = "v2.4.0", bool preview = false, string host = "github.com") => $$"""
        {"tag_name":"{{tag}}","draft":false,"prerelease":{{preview.ToString().ToLowerInvariant()}},"assets":[
          {"name":"{{asset}}","browser_download_url":"https://{{host}}/bogzxo/hex/releases/download/{{tag}}/{{asset}}","size":3,"digest":"{{digest}}"}]}
        """;

    [Theory]
    [InlineData("win-x64")][InlineData("win-arm64")][InlineData("linux-x64")][InlineData("linux-arm64")][InlineData("osx-x64")][InlineData("osx-arm64")]
    public void SelectsExactPlatform(string rid)
    {
        string name = $"Hex-{rid}.zip";
        var release = ReleaseClient.Parse(Release("sha256:" + new string('a', 64), name), rid);
        Assert.Equal(name, release.Name);
        Assert.Equal(new Version(2, 4, 0), ReleaseClient.VersionOf(release.Version));
    }

    [Fact]
    public void RefusesMissingAssetsPrereleasesUntrustedHostsAndAbsentDigests()
    {
        string digest = "sha256:" + new string('a', 64);
        Assert.Throws<InvalidOperationException>(() => ReleaseClient.Parse(Release(digest), "linux-x64"));
        Assert.Throws<InvalidDataException>(() => ReleaseClient.Parse(Release(digest, preview: true), "win-x64"));
        Assert.Throws<InvalidDataException>(() => ReleaseClient.Parse(Release(digest, host: "example.com"), "win-x64"));
        Assert.Throws<InvalidDataException>(() => ReleaseClient.Parse(Release(""), "win-x64"));
        Assert.Throws<PlatformNotSupportedException>(() => InstallPaths.ValidateRid("linux-riscv64"));
    }

    private string Package(params (string Name, string Text)[] extra)
    {
        string file = Path.Combine(home, Guid.NewGuid() + ".zip");
        using var archive = ZipFile.Open(file, ZipArchiveMode.Create);
        foreach (var (name, text) in new[] { (InstallPaths.Executable, "new Hex"), ("installer/" + InstallPaths.InstallerExecutable, "setup") }.Concat(extra))
        {
            using var writer = new StreamWriter(archive.CreateEntry("Hex/" + name).Open());
            writer.Write(text);
        }
        return file;
    }

    [Theory]
    [InlineData("../outside")][InlineData("/absolute")][InlineData("C:/outside")][InlineData("Assets/../../outside")][InlineData("a\\..\\outside")][InlineData("file:stream")]
    public void RejectsArchiveTraversal(string path)
    {
        string root = Path.Combine(home, "hex");
        Assert.Throws<InvalidDataException>(() => InstallTransaction.Prepare(Package((path, "bad")), root + ".stage", root, "2.4.0"));
        Assert.False(File.Exists(Path.Combine(home, "outside")));
    }

    [Fact]
    public void PreservesSettingsRemovesObsoleteFilesAndRollsBackFailedCommit()
    {
        string root = Path.Combine(home, "hex"), stage = root + ".stage";
        InstallTransaction.Prepare(Package(("obsolete.dll", "old")), stage, root, "2.3.0");
        InstallTransaction.Commit(stage, root, () => { });
        Directory.CreateDirectory(Path.Combine(root, "settings"));
        File.WriteAllText(Path.Combine(root, "settings", "settings.hor"), "my settings");
        InstallTransaction.Prepare(Package(("current.dll", "new")), stage, root, "2.4.0");
        bool restoredRegistration = false;
        Assert.Throws<IOException>(() => InstallTransaction.Commit(stage, root, () => throw new IOException("registration failed"), () => restoredRegistration = true));
        Assert.True(restoredRegistration);
        Assert.Equal("2.3.0", InstallTransaction.ReadManifest(root).Version);
        Assert.True(File.Exists(Path.Combine(root, "obsolete.dll")));
        InstallTransaction.Commit(stage, root, () => { });
        Assert.Equal("2.4.0", InstallTransaction.ReadManifest(root).Version);
        Assert.False(File.Exists(Path.Combine(root, "obsolete.dll")));
        Assert.Equal("my settings", File.ReadAllText(Path.Combine(root, "settings", "settings.hor")));
    }

    [Fact]
    public void RefusesPortableFoldersAndPersonalFileCollisions()
    {
        string root = Path.Combine(home, "hex");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "personal.txt"), "keep");
        Assert.Throws<IOException>(() => InstallTransaction.ValidateRoot(root));
        Assert.Throws<IOException>(() => InstallTransaction.Prepare(Package(("personal.txt", "replace")), root + ".stage", root, "2.4.0"));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(root, "personal.txt")));
    }

    [Fact]
    public void RejectsSymlinksAndDuplicateNames()
    {
        string zip = Package(("link", "../../outside"));
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update)) archive.GetEntry("Hex/link")!.ExternalAttributes = 0xa1ff << 16;
        Assert.Throws<InvalidDataException>(() => InstallTransaction.Prepare(zip, Path.Combine(home, "stage"), Path.Combine(home, "hex"), "2.4.0"));
        Assert.Throws<InvalidDataException>(() => InstallTransaction.Prepare(Package(("Assets/A", "a"), ("Assets/a", "b")), Path.Combine(home, "stage2"), Path.Combine(home, "hex"), "2.4.0"));
    }

    [Fact]
    public async Task DownloadChecksHashLengthAndCancellation()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("abc");
        using var http = new HttpClient(new FakeDownload(bytes));
        var client = new ReleaseClient(http);
        var asset = new ReleaseAsset("2.4.0", "Hex-win-x64.zip", new Uri("https://github.com/bogzxo/hex/releases/download/v2.4.0/Hex-win-x64.zip"), bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
        string path = Path.Combine(home, "download.zip");
        await client.Download(asset, path, null, default);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.Download(asset with { Sha256 = new string('0', 64) }, path, null, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.Download(asset with { Size = 2 }, path, null, default));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Download(asset, path, null, cancel.Token));
    }

    [Fact]
    public async Task ParentWaitIsCancellableAndDetectsReusedPid()
    {
        using var parent = System.Diagnostics.Process.GetCurrentProcess();
        await InstallerHandoff.WaitForParent(parent.Id, 1, default);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InstallerHandoff.WaitForParent(parent.Id, parent.StartTime.ToUniversalTime().Ticks, cancel.Token));
    }

    [Fact]
    public void UninstallPreservesPersonalDataAndRecoversFromRegistrationFailure()
    {
        string root = Path.Combine(home, "hex"), stage = root + ".stage";
        InstallTransaction.Prepare(Package(("Assets/old.dat", "asset")), stage, root, "2.4.0");
        InstallTransaction.Commit(stage, root, () => { });
        File.WriteAllText(Path.Combine(root, "settings.hor"), "personal");
        Assert.Throws<IOException>(() => InstallTransaction.Uninstall(root, () => throw new IOException("refused"), () => { }));
        Assert.True(File.Exists(Path.Combine(root, InstallPaths.Executable)));
        Assert.Equal("2.4.0", InstallTransaction.ReadManifest(root).Version);
        InstallTransaction.Uninstall(root, () => { }, () => { });
        Assert.False(File.Exists(Path.Combine(root, InstallPaths.Executable)));
        Assert.False(File.Exists(Path.Combine(root, InstallPaths.Manifest)));
        Assert.False(Directory.Exists(Path.Combine(root, "Assets")));
        Assert.Equal("personal", File.ReadAllText(Path.Combine(root, "settings.hor")));
    }

    [Fact]
    public void NativeLaunchersQuoteSpacesAndMetacharacters()
    {
        string desktop = PlatformRegistration.DesktopEntry("/home/test/100% $hex", uninstall: true);
        Assert.Contains("100%% \\$hex", desktop);
        Assert.Contains("--uninstall --target", desktop);
        Assert.Equal("'it'\"'\"'s hex'", PlatformRegistration.ShellQuote("it's hex"));
        var plist = System.Xml.Linq.XDocument.Parse(PlatformRegistration.MacPlist("v2.4.0"));
        Assert.Contains("2.4.0", plist.ToString());
    }

    private sealed class FakeDownload(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
