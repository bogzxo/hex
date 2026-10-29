using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Hex.Installation;

namespace Hex.Installer.Tests;

public sealed class InstallerUpdateTests : IDisposable
{
    private readonly string home = Directory.CreateTempSubdirectory("hex-update-test-").FullName;
    public void Dispose() => Directory.Delete(home, true);

    private static byte[] Package(string version, params (string Name, string Text)[] extra)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var (name, text) in new[]
            {
                (InstallPaths.Executable, "Hex " + version),
                ("installer/" + InstallPaths.InstallerExecutable, "Setup " + version),
                ("installer/Assets/installer/setup.hor", "new setup layout"),
                ("installer/runtime.dll", "new dependency")
            }.Concat(extra))
            {
                using var writer = new StreamWriter(zip.CreateEntry("Hex/" + name).Open());
                writer.Write(text);
            }
        return stream.ToArray();
    }

    private static ReleaseAsset Asset(byte[] bytes, string version = "v2.6.0") => new(version, "Hex-" + InstallPaths.Rid + ".zip",
        new Uri("https://github.com/bogzxo/hex/releases/download/" + version + "/Hex-" + InstallPaths.Rid + ".zip"),
        bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(home, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string Installed()
    {
        string root = Path.Combine(home, "Hex"), stage = root + ".stage";
        InstallTransaction.Prepare(Write("old.zip", Package("2.5.1", ("installer/obsolete.dll", "old dependency"))), stage, root, "v2.5.1");
        InstallTransaction.Commit(stage, root, () => { });
        Directory.CreateDirectory(Path.Combine(root, "settings"));
        File.WriteAllText(Path.Combine(root, "settings", "settings.hor"), "keep my settings");
        return root;
    }

    [Fact]
    public async Task Downloads_once_and_prepares_the_new_installer_with_its_dependencies_and_verified_cache()
    {
        byte[] bytes = Package("2.6.0");
        var handler = new Download(bytes);
        using var http = new HttpClient(handler);
        string copied;
        using (var prepared = await PreparedInstallerUpdate.Prepare(Asset(bytes), null, default, new ReleaseClient(http)))
        {
            copied = Path.GetDirectoryName(prepared.Executable)!;
            Assert.Equal("Setup 2.6.0", File.ReadAllText(prepared.Executable));
            Assert.Equal("new dependency", File.ReadAllText(Path.Combine(copied, "runtime.dll")));
            Assert.True(File.Exists(Path.Combine(copied, "Assets", "installer", "setup.hor")));
            await ReleaseClient.Verify(Asset(bytes), prepared.Package, default);
            Assert.Equal(1, handler.Requests);
            var args = prepared.Arguments(Path.Combine(home, "Hex"));
            Assert.Contains("--package", args);
            Assert.Contains(prepared.Package, args);
            Assert.Contains("v2.6.0", args);
            Assert.DoesNotContain("--uninstall", args);
        }
        Assert.False(Directory.Exists(copied));
    }

    [Fact]
    public async Task Rejects_corrupt_cancelled_and_unsafe_downloads_before_handoff()
    {
        byte[] bytes = Package("2.6.0");
        using var http = new HttpClient(new Download(bytes));
        await Assert.ThrowsAsync<InvalidDataException>(() => PreparedInstallerUpdate.Prepare(Asset(bytes) with { Sha256 = new string('0', 64) }, null, default, new ReleaseClient(http)));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PreparedInstallerUpdate.Prepare(Asset(bytes), null, cancel.Token, new ReleaseClient(http)));
        byte[] traversal = Package("2.6.0", ("installer/../../escape", "bad"));
        using var unsafeHttp = new HttpClient(new Download(traversal));
        await Assert.ThrowsAsync<InvalidDataException>(() => PreparedInstallerUpdate.Prepare(Asset(traversal), null, default, new ReleaseClient(unsafeHttp)));
    }

    [Fact]
    public async Task Cached_update_replaces_hex_and_uninstaller_preserves_settings_and_removes_old_dependencies()
    {
        string root = Installed();
        byte[] bytes = Package("2.6.0");
        string package = Write("release.zip", bytes);
        int registered = 0, stopped = 0;
        await InstallTransaction.Install(Asset(bytes), root, null, default, null, package,
            () => registered++, () => { }, () => stopped++);
        Assert.Equal(1, registered);
        Assert.Equal(1, stopped);
        Assert.Equal("v2.6.0", InstallTransaction.ReadManifest(root).Version);
        Assert.Equal("Setup 2.6.0", File.ReadAllText(Path.Combine(root, "installer", InstallPaths.InstallerExecutable)));
        Assert.Equal("Hex 2.6.0", File.ReadAllText(Path.Combine(root, InstallPaths.Executable)));
        Assert.False(File.Exists(Path.Combine(root, "installer", "obsolete.dll")));
        Assert.Equal("keep my settings", File.ReadAllText(Path.Combine(root, "settings", "settings.hor")));
        Assert.True(File.Exists(package)); // Retained for retries until the temporary installer exits.
    }

    [Fact]
    public async Task Failed_registration_rolls_back_both_hex_and_uninstaller()
    {
        string root = Installed();
        byte[] bytes = Package("2.6.0");
        bool restored = false;
        await Assert.ThrowsAsync<IOException>(() => InstallTransaction.Install(Asset(bytes), root, null, default, null, Write("new.zip", bytes),
            () => throw new IOException("registration failed"), () => restored = true, () => { }));
        Assert.True(restored);
        Assert.Equal("v2.5.1", InstallTransaction.ReadManifest(root).Version);
        Assert.Equal("Setup 2.5.1", File.ReadAllText(Path.Combine(root, "installer", InstallPaths.InstallerExecutable)));
        Assert.Equal("Hex 2.5.1", File.ReadAllText(Path.Combine(root, InstallPaths.Executable)));
        Assert.Empty(Directory.GetDirectories(home, "Hex.stage-*"));
    }

    [Fact]
    public async Task Cache_tampering_missing_cache_and_open_windows_leave_the_old_installation_untouched()
    {
        string root = Installed();
        byte[] bytes = Package("2.6.0");
        string package = Write("new.zip", bytes);
        byte[] changed = (byte[])bytes.Clone(); changed[^1] ^= 1;
        File.WriteAllBytes(package, changed);
        Task Install(string path, Action? stopped = null) => InstallTransaction.Install(Asset(bytes), root, null, default, null, path,
            () => throw new Xunit.Sdk.XunitException("must not commit"), () => { }, stopped ?? (() => { }));
        await Assert.ThrowsAsync<InvalidDataException>(() => Install(package));
        await Assert.ThrowsAsync<FileNotFoundException>(() => Install(Path.Combine(home, "missing.zip")));
        File.WriteAllBytes(package, bytes);
        await Assert.ThrowsAsync<IOException>(() => Install(package, () => throw new IOException("close Hex first")));
        Assert.Equal("v2.5.1", InstallTransaction.ReadManifest(root).Version);
        Assert.Equal("Setup 2.5.1", File.ReadAllText(Path.Combine(root, "installer", InstallPaths.InstallerExecutable)));
        Assert.Empty(Directory.GetDirectories(home, "Hex.stage-*"));
    }

    [Fact]
    public async Task Edits_to_the_original_cache_after_verification_do_not_change_the_private_snapshot()
    {
        string root = Installed();
        byte[] bytes = Package("2.6.0");
        string package = Write("new.zip", bytes);
        await InstallTransaction.Install(Asset(bytes), root, _ => File.WriteAllText(package, "tampered after snapshot"), default, null, package,
            () => { }, () => { }, () => { });
        Assert.Equal("Setup 2.6.0", File.ReadAllText(Path.Combine(root, "installer", InstallPaths.InstallerExecutable)));
    }

    private sealed class Download(byte[] bytes) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
