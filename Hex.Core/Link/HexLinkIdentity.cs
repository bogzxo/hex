using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Hex.Core;

/// <summary>
/// This Hex as other Hexes know it (docs/hex-link-plan.md, 3.1): one P-256 key, made the first time Link is used, and a
/// self-signed certificate over it good for 20 years, whose subject is only a label. The device id is the SHA-256 of the
/// certificate's SubjectPublicKeyInfo, so it's the key that's known, not anything the certificate says.
/// <para>
/// The key and certificate are kept together as a PKCS#12 in <c>&lt;folder&gt;/identity</c>, wrapped by
/// <see cref="HexLinkSecrets"/>, the folder and file the user's alone where the OS has modes. The certificate handed out
/// is the PKCS#12 loaded again rather than the one CertificateRequest made, because SChannel and Apple's TLS want a key
/// they can find through the certificate's store, which an ephemeral key made in memory isn't.
/// </para>
/// </summary>
internal sealed class HexLinkIdentity : IDisposable
{
    /// <summary>The file in the folder the wrapped key and certificate are kept in.</summary>
    public const string FILE = "identity";

    // How long the certificate is good for, nobody is meant to see it run out
    private const int YEARS = 20;

    /// <summary>The certificate with its private key, what SslStream presents on both ends.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>How the key on disk is protected, for Link's window to say.</summary>
    public HexLinkProtection Protection { get; }

    /// <summary>The device id, the upper-case hex SHA-256 of the public key.</summary>
    public string Id { get; }

    /// <summary>What this device is called to others, a label only, changed by the <c>link_name</c> setting.</summary>
    public string Name { get; set; }

    /// <summary>Whether the identity was made now rather than read back.</summary>
    public bool Made { get; }

    /// <summary>This device as it introduces itself.</summary>
    public HexLinkDevice Device => new(Id, Name, OsName, ArchName, VersionName);

    private HexLinkIdentity(X509Certificate2 certificate, HexLinkProtection protection, string name, bool made)
    {
        Certificate = certificate;
        Protection = protection;
        Id = PinOf(certificate);
        Name = name;
        Made = made;
    }

    /// <summary>
    /// The identity kept in <paramref name="folder"/>, made and kept there when there's none. A file that can't be
    /// read because it's broken or was made for another user is set aside as <c>identity.unreadable</c> and a new identity
    /// made, which means pairing again; one that can't be read because its Keychain or secret service is out of reach
    /// right now throws <see cref="HexLinkSecretsUnavailableException"/> and is left as it is.
    /// </summary>
    /// <param name="fileOnly">Keep a new key by the file's permissions alone, see <see cref="HexLinkSecrets"/>.</param>
    public static HexLinkIdentity LoadOrCreate(string folder, string name, bool fileOnly = false)
    {
        MakeFolder(folder);
        string path = Path.Combine(folder, FILE);
        if (File.Exists(path))
        {
            try
            {
                byte[] wrapped = File.ReadAllBytes(path);
                HexLinkProtection protection = HexLinkSecrets.ProtectionOf(wrapped);
                byte[] pfx = HexLinkSecrets.Unprotect(wrapped);
                try
                {
                    return new HexLinkIdentity(Load(pfx), protection, name, made: false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pfx);
                }
            }
            catch (CryptographicException)
            {
                File.Move(path, Path.Combine(folder, FILE + ".unreadable"), overwrite: true);
            }
        }

        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Hex Link", key, HashAlgorithmName.SHA256);
        using X509Certificate2 made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(YEARS));
        byte[] exported = made.Export(X509ContentType.Pkcs12, "");
        try
        {
            byte[] kept = HexLinkSecrets.Protect(exported, fileOnly);
            WriteUserOnly(path, kept);
            return new HexLinkIdentity(Load(exported), HexLinkSecrets.ProtectionOf(kept), name, made: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(exported);
        }
    }

    /// <summary>The SHA-256 of a certificate's public key in upper-case hex, what a device is known by.</summary>
    public static string PinOf(X509Certificate certificate)
    {
        if (certificate is X509Certificate2 two)
            return Convert.ToHexString(SHA256.HashData(two.PublicKey.ExportSubjectPublicKeyInfo()));

        using X509Certificate2 copy = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return Convert.ToHexString(SHA256.HashData(copy.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>The OS a device says it runs, short and lower case.</summary>
    public static string OsName => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux"
        : OperatingSystem.IsAndroid() ? "android" : RuntimeInformation.OSDescription;

    /// <summary>The processor a device says it has, x64 or arm64.</summary>
    public static string ArchName => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

    /// <summary>The version of Hex a device says it runs.</summary>
    public static string VersionName
    {
        get
        {
            Assembly assembly = typeof(HexLinkIdentity).Assembly;
            string? said = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (said is not null && said.IndexOf('+') is var plus and > 0)
                said = said[..plus];
            return said ?? assembly.GetName().Version?.ToString() ?? "";
        }
    }

    private static X509Certificate2 Load(byte[] pfx) => X509CertificateLoader.LoadPkcs12(pfx, "", X509KeyStorageFlags.Exportable);

    /// <summary>Makes a folder only its user may enter, where the OS has modes.</summary>
    public static void MakeFolder(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
            return;
        }

        Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// Writes a file whole and at once (beside it, then moved over it), readable and writable by its user alone where the
    /// OS has modes, the mode set before a byte is written so there's no moment it's anyone's to read.
    /// </summary>
    public static void WriteUserOnly(string path, byte[] bytes)
    {
        string next = path + ".new";
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(next, bytes);
        }
        else
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            };
            using (var stream = new FileStream(next, options))
                stream.Write(bytes);
            File.SetUnixFileMode(next, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(next, path, overwrite: true);
    }

    public void Dispose() => Certificate.Dispose();
}
