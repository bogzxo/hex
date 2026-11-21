using System.Security.Cryptography;

namespace Hex.Core;

/// <summary>How the device's private key is kept from somebody who copies the file off the disk, for Link's window to say.</summary>
internal enum HexLinkProtection
{
    /// <summary>By the file's permissions alone (0600), when there was no Keychain or secret service to ask.</summary>
    File = 0,

    /// <summary>Windows' DPAPI, for this user on this machine.</summary>
    Dpapi = 1,

    /// <summary>AES-256-GCM under a key kept in the macOS login Keychain.</summary>
    Keychain = 2,

    /// <summary>AES-256-GCM under a key kept by the Linux secret service (libsecret, through secret-tool).</summary>
    SecretService = 3
}

/// <summary>
/// A secret that can't be read back right now: the Keychain is locked, or the secret service isn't running in this
/// session. Not the same as a secret that's wrong or broken, which is a CryptographicException, the difference being
/// that this one may well read fine later, so what was wrapped mustn't be thrown away over it.
/// </summary>
internal sealed class HexLinkSecretsUnavailableException(string message) : IOException(message);

/// <summary>
/// Wraps the device's private key for keeping on disk (docs/hex-link-plan.md, 3.1). What's written starts with a magic
/// and how it was wrapped, so a file made on one setup is read on the next by the same way it was made, and a Mac that
/// made its key in an ssh session with no Keychain (the file alone) reads it again once the Keychain is there.
/// <para>
/// Windows wraps with DPAPI for the current user. macOS keeps a random 256-bit wrapping key in the login Keychain under
/// the service "Hex Link" and seals the secret with AES-256-GCM under it; Linux keeps the same kind of key through
/// secret-tool, the key on its stdin, never on a command line. When neither is there the secret is kept as it is and the
/// file's permissions are all that protect it, which <see cref="HexLinkProtection.File"/> says so the window can too.
/// </para>
/// <para>
/// <c>HEX_LINK_KEYSTORE=file</c> in the environment (or <c>fileOnly</c>) makes the last the only way, for tests and a
/// second Hex on the machine that mustn't touch the person's real Keychain.
/// </para>
/// </summary>
internal static partial class HexLinkSecrets
{
    /// <summary>The environment variable that keeps secrets to the file alone when it says <c>file</c>.</summary>
    public const string KEYSTORE_VARIABLE = "HEX_LINK_KEYSTORE";

    // The service and account the wrapping key is kept under in the Keychain or the secret service
    private const string SERVICE = "Hex Link";
    private const string ACCOUNT = "identity wrapping key";

    // What a wrapped secret starts with, then a byte for how it was wrapped
    private static ReadOnlySpan<byte> MAGIC => "HXL1"u8;

    private const int NONCE = 12;
    private const int TAG = 16;
    private const int KEY = 32;

    /// <summary>Whether the environment says to keep secrets in the file alone.</summary>
    public static bool FileOnlyByEnvironment => string.Equals(Environment.GetEnvironmentVariable(KEYSTORE_VARIABLE), "file", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The secret wrapped the strongest way this machine has right now, the way said in what's returned. Never throws for
    /// a Keychain or secret service that isn't there, it falls back to the file.
    /// </summary>
    public static byte[] Protect(byte[] secret, bool fileOnly = false)
    {
        if (!fileOnly && !FileOnlyByEnvironment)
        {
            if (OperatingSystem.IsWindows())
            {
                if (DpapiProtect(secret) is { } wrapped)
                    return Wrap(HexLinkProtection.Dpapi, wrapped);
            }
            else if (OperatingSystem.IsMacOS())
            {
                if (KeychainKey(create: true) is { } key)
                    return Wrap(HexLinkProtection.Keychain, Seal(key, secret));
            }
            else if (OperatingSystem.IsLinux())
            {
                if (SecretServiceKey(create: true) is { } key)
                    return Wrap(HexLinkProtection.SecretService, Seal(key, secret));
            }
        }

        return Wrap(HexLinkProtection.File, secret);
    }

    /// <summary>
    /// The secret back. A CryptographicException when what's given isn't a wrapped secret or was changed, a
    /// <see cref="HexLinkSecretsUnavailableException"/> when the store it was wrapped by can't be reached now.
    /// </summary>
    public static byte[] Unprotect(byte[] wrapped)
    {
        HexLinkProtection how = ProtectionOf(wrapped);
        ReadOnlySpan<byte> body = wrapped.AsSpan(MAGIC.Length + 1);
        switch (how)
        {
            case HexLinkProtection.File:
                return body.ToArray();

            case HexLinkProtection.Dpapi:
                if (!OperatingSystem.IsWindows())
                    throw new HexLinkSecretsUnavailableException("the key was protected by Windows' DPAPI and this isn't Windows");
                return DpapiUnprotect(body.ToArray()) ?? throw new CryptographicException("DPAPI couldn't unwrap the key, it was made by another user or changed");

            case HexLinkProtection.Keychain:
                if (!OperatingSystem.IsMacOS())
                    throw new HexLinkSecretsUnavailableException("the key was protected by the macOS Keychain and this isn't macOS");
                return Open(KeychainKey(create: false) ?? throw new HexLinkSecretsUnavailableException("the Keychain is locked or has no Hex Link key"), body);

            case HexLinkProtection.SecretService:
                if (!OperatingSystem.IsLinux())
                    throw new HexLinkSecretsUnavailableException("the key was protected by the Linux secret service and this isn't Linux");
                return Open(SecretServiceKey(create: false) ?? throw new HexLinkSecretsUnavailableException("the secret service isn't running or has no Hex Link key"), body);

            default:
                throw new CryptographicException("not a wrapped secret");
        }
    }

    /// <summary>How a wrapped secret was wrapped, a CryptographicException when it isn't one.</summary>
    public static HexLinkProtection ProtectionOf(byte[] wrapped)
    {
        if (wrapped.Length <= MAGIC.Length || !wrapped.AsSpan(0, MAGIC.Length).SequenceEqual(MAGIC) || wrapped[MAGIC.Length] > (byte)HexLinkProtection.SecretService)
            throw new CryptographicException("not a wrapped secret");
        return (HexLinkProtection)wrapped[MAGIC.Length];
    }

    private static byte[] Wrap(HexLinkProtection how, byte[] body)
    {
        var wrapped = new byte[MAGIC.Length + 1 + body.Length];
        MAGIC.CopyTo(wrapped);
        wrapped[MAGIC.Length] = (byte)how;
        body.CopyTo(wrapped, MAGIC.Length + 1);
        return wrapped;
    }

    // AES-256-GCM, a fresh nonce each time: nonce, tag, then the sealed bytes
    private static byte[] Seal(byte[] key, byte[] secret)
    {
        var sealedBytes = new byte[NONCE + TAG + secret.Length];
        Span<byte> nonce = sealedBytes.AsSpan(0, NONCE);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TAG);
        aes.Encrypt(nonce, secret, sealedBytes.AsSpan(NONCE + TAG), sealedBytes.AsSpan(NONCE, TAG));
        return sealedBytes;
    }

    private static byte[] Open(byte[] key, ReadOnlySpan<byte> sealedBytes)
    {
        if (sealedBytes.Length < NONCE + TAG)
            throw new CryptographicException("the wrapped key is cut short");
        var secret = new byte[sealedBytes.Length - NONCE - TAG];
        using var aes = new AesGcm(key, TAG);
        aes.Decrypt(sealedBytes[..NONCE], sealedBytes[(NONCE + TAG)..], sealedBytes.Slice(NONCE, TAG), secret);
        return secret;
    }
}
