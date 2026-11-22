using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Hex.Core;

namespace Hex.Tests;

/// <summary>What Link keeps on disk: the identity and its wrapped key, the known devices, the audit log.</summary>
public sealed class HexLinkStoreTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "hex-link-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void An_identity_is_made_once_and_read_back_the_same()
    {
        string id;
        using (HexLinkIdentity made = HexLinkIdentity.LoadOrCreate(folder, "PC", fileOnly: true))
        {
            Assert.True(made.Made);
            Assert.Equal(HexLinkProtection.File, made.Protection);
            Assert.Matches("^[0-9A-F]{64}$", made.Id);
            Assert.Equal(made.Id, HexLinkIdentity.PinOf(made.Certificate));
            Assert.True(made.Certificate.HasPrivateKey);
            Assert.Equal("PC", made.Device.Name);
            Assert.NotEmpty(made.Device.Os);
            Assert.Matches("^[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}$", made.Device.Fingerprint);
            id = made.Id;
        }

        using HexLinkIdentity read = HexLinkIdentity.LoadOrCreate(folder, "Renamed", fileOnly: true);
        Assert.False(read.Made);
        Assert.Equal(id, read.Id);
        Assert.Equal("Renamed", read.Device.Name);
        Assert.True(read.Certificate.HasPrivateKey);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(folder, HexLinkIdentity.FILE)));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
        }
    }

    [Fact]
    public void Two_identities_are_two_devices()
    {
        using HexLinkIdentity one = HexLinkIdentity.LoadOrCreate(Path.Combine(folder, "one"), "One", fileOnly: true);
        using HexLinkIdentity two = HexLinkIdentity.LoadOrCreate(Path.Combine(folder, "two"), "Two", fileOnly: true);
        Assert.NotEqual(one.Id, two.Id);
    }

    [Fact]
    public void On_windows_the_key_file_holds_no_key_in_the_clear()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using HexLinkIdentity made = HexLinkIdentity.LoadOrCreate(folder, "PC");
        Assert.Equal(HexLinkProtection.Dpapi, made.Protection);
        using ECDsa key = made.Certificate.GetECDsaPrivateKey()!;
        byte[] pkcs8 = key.ExportPkcs8PrivateKey();
        byte[] scalar = key.ExportParameters(includePrivateParameters: true).D!;
        byte[] kept = File.ReadAllBytes(Path.Combine(folder, HexLinkIdentity.FILE));
        Assert.True(kept.AsSpan().IndexOf(pkcs8) < 0);
        Assert.True(kept.AsSpan().IndexOf(scalar) < 0);

        // Nor the certificate, DPAPI wraps the whole PKCS#12
        Assert.True(kept.AsSpan().IndexOf(made.Certificate.RawData) < 0);

        using HexLinkIdentity read = HexLinkIdentity.LoadOrCreate(folder, "PC");
        Assert.Equal(made.Id, read.Id);
    }

    [Fact]
    public void A_broken_identity_stays_in_place_until_an_explicit_reset()
    {
        using HexLinkIdentity original = HexLinkIdentity.LoadOrCreate(folder, "PC", fileOnly: true);
        string path = Path.Combine(folder, HexLinkIdentity.FILE);
        byte[] valid = File.ReadAllBytes(path);
        byte[] broken = [1, 2, 3, 4, 5, 6];
        File.WriteAllBytes(path, broken);
        var failure = Assert.Throws<HexLinkIdentityLoadException>(() => HexLinkIdentity.LoadOrCreate(folder, "PC", fileOnly: true));
        Assert.Equal(HexLinkIdentityProblem.Corrupt, failure.Failure.Problem);
        Assert.Equal(broken, File.ReadAllBytes(path));

        File.WriteAllBytes(path, valid);
        using HexLinkIdentity restored = HexLinkIdentity.LoadOrCreate(folder, "PC", fileOnly: true);
        Assert.Equal(original.Id, restored.Id);
        string backup = HexLinkIdentity.Reset(folder);
        Assert.StartsWith(path + ".backup-", backup);
        Assert.Equal(valid, File.ReadAllBytes(backup));
        using HexLinkIdentity next = HexLinkIdentity.LoadOrCreate(folder, "PC", fileOnly: true);
        Assert.NotEqual(original.Id, next.Id);
    }

    [Fact]
    public void A_key_that_needs_an_unavailable_store_is_left_in_place()
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, HexLinkIdentity.FILE);
        byte[] locked = [.. "HXL1"u8, (byte)(OperatingSystem.IsMacOS() ? HexLinkProtection.Dpapi : HexLinkProtection.Keychain), 0];
        File.WriteAllBytes(path, locked);
        var failure = Assert.Throws<HexLinkIdentityLoadException>(() => HexLinkIdentity.LoadOrCreate(folder, "PC", fileOnly: true));
        Assert.Equal(HexLinkIdentityProblem.Unavailable, failure.Failure.Problem);
        Assert.Equal(locked, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_store_can_be_published_while_a_reader_still_has_its_old_version()
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "known");
        HexLinkIdentity.WriteUserOnly(path, "old"u8.ToArray());
        using var old = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(old);
        HexLinkIdentity.WriteUserOnly(path, "new"u8.ToArray());
        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal("old", reader.ReadToEnd());
    }

    [Fact]
    public void Secrets_wrapped_by_the_file_come_back_and_broken_ones_dont()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(100);
        byte[] wrapped = HexLinkSecrets.Protect(secret, fileOnly: true);
        Assert.Equal(HexLinkProtection.File, HexLinkSecrets.ProtectionOf(wrapped));
        Assert.Equal(secret, HexLinkSecrets.Unprotect(wrapped));

        wrapped[0] ^= 0xFF;
        Assert.Throws<CryptographicException>(() => HexLinkSecrets.Unprotect(wrapped));
        Assert.Throws<CryptographicException>(() => HexLinkSecrets.Unprotect([]));

        if (OperatingSystem.IsWindows())
        {
            byte[] dpapi = HexLinkSecrets.Protect(secret);
            Assert.Equal(HexLinkProtection.Dpapi, HexLinkSecrets.ProtectionOf(dpapi));
            Assert.Equal(secret, HexLinkSecrets.Unprotect(dpapi));
            dpapi[^1] ^= 0xFF;
            Assert.Throws<CryptographicException>(() => HexLinkSecrets.Unprotect(dpapi));
        }
    }

    [Fact]
    public void Known_devices_are_kept_and_read_back()
    {
        var device = new HexLinkDevice(new string('A', 64), "Mac", "macos", "arm64", "3.3.0");
        var changed = new List<string>();
        var trust = new HexLinkTrust(folder);
        trust.Changed += changed.Add;

        trust.Add(device, HexLinkRights.View | HexLinkRights.Edit, "192.168.0.30", 47833);
        Assert.True(trust.SetRights(device.Id, HexLinkRights.View | HexLinkRights.Steer));
        Assert.True(trust.Rename(device.Id, "Laptop"));
        Assert.True(trust.Seen(device.Id, device with { Name = "", Version = "3.4.0" }, "100.80.122.37"));
        Assert.False(trust.SetRights(new string('B', 64), HexLinkRights.View));
        Assert.Equal(4, changed.Count);

        HexLinkKnown read = Assert.Single(new HexLinkTrust(folder).Known);
        Assert.Equal(device.Id, read.Device.Id);
        // Renamed here it's shown by the name given here, its own name kept beside it
        Assert.Equal(("Laptop", "Mac"), (read.Shown, read.Device.Name));
        Assert.Equal("macos", read.Device.Os);
        Assert.Equal("arm64", read.Device.Arch);
        Assert.Equal("3.4.0", read.Device.Version);
        Assert.Equal(HexLinkRights.View | HexLinkRights.Steer, read.Rights);
        Assert.Equal("100.80.122.37", read.Address);
        Assert.Equal(47833, read.Port);
        Assert.True(read.Seen >= read.Added);

        Assert.True(trust.Forget(device.Id));
        Assert.Null(trust.Find(device.Id));
        Assert.Empty(new HexLinkTrust(folder).Known);
        Assert.Equal(device.Id, changed[^1]);
        Assert.DoesNotContain("PRIVATE", File.ReadAllText(Path.Combine(folder, HexLinkTrust.FILE)));
    }

    [Fact]
    public void A_broken_devices_file_reads_as_none()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, HexLinkTrust.FILE), "{ not json");
        Assert.Empty(new HexLinkTrust(folder).Known);
    }

    [Fact]
    public void Audit_rotation_keeps_a_large_latest_event_that_fits_the_byte_limit()
    {
        string path = Path.Combine(folder, "audit.log");
        var audit = new HexLinkAudit(path, most: 256);
        string detail = new('界', 65);
        audit.Write("older", "DEVICE", "first event");
        audit.Write("newest", "DEVICE", detail);

        HexLinkAuditEntry entry = Assert.Single(audit.Latest(10));
        Assert.Equal(("newest", "DEVICE", detail), (entry.Event, entry.Device, entry.Detail));
        Assert.InRange(new FileInfo(path).Length, 1L, 256L);
    }

    [Fact]
    public void Rotation_of_an_existing_larger_log_respects_the_current_byte_limit()
    {
        string path = Path.Combine(folder, "audit.log");
        var older = new HexLinkAudit(path);
        for (int i = 0; i < 100; i++)
            older.Write("older", "DEVICE", $"event {i}");

        var audit = new HexLinkAudit(path, most: 256);
        audit.Write("newest", "DEVICE", "last event");

        Assert.InRange(new FileInfo(path).Length, 1L, 256L);
        Assert.Equal("last event", Assert.Single(audit.Latest(1)).Detail);
        Assert.All(File.ReadAllLines(path), line => Assert.Equal(4, line.Split('\t').Length));
    }

    [Fact]
    public void The_audit_log_keeps_its_latest_under_its_cap()
    {
        string path = Path.Combine(folder, "audit.log");
        var audit = new HexLinkAudit(path, most: 4096);
        for (int i = 0; i < 400; i++)
            audit.Write("refused", "DEVICE", $"try {i}\twith a tab");

        Assert.True(new FileInfo(path).Length <= 4096);
        List<HexLinkAuditEntry> latest = audit.Latest(3);
        Assert.Equal(["try 397 with a tab", "try 398 with a tab", "try 399 with a tab"], latest.Select(entry => entry.Detail));
        Assert.All(latest, entry => Assert.Equal("refused", entry.Event));
        Assert.All(latest, entry => Assert.True((DateTime.UtcNow - entry.Time).Duration() < TimeSpan.FromMinutes(5)));

        // Every line left is whole
        Assert.All(File.ReadAllLines(path), line => Assert.Equal(4, line.Split('\t').Length));
    }
}
