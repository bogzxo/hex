using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Hex.Core;

/// <summary>
/// A pairing waiting on the person (docs/hex-link-plan.md, 3.3): the other device, the six digits both screens show,
/// where it is. Whoever's asked shows them and answers what this machine grants the other once the person checked the
/// code is the same on both, or null to decline.
/// </summary>
/// <param name="Other">The other device, its id proved by the handshake, the rest only what it says.</param>
/// <param name="Code">Six digits, the same on both screens unless somebody is in the middle.</param>
/// <param name="Address">Where the other device is, for showing.</param>
/// <param name="Ended">Cancelled when the pairing ends without this side's answer (the other declined, two minutes passed, the connection broke), so a dialog asking can close.</param>
internal sealed record HexLinkPairing(HexLinkDevice Other, string Code, string Address, CancellationToken Ended,
    bool Invited = false, HexLinkPairingTerms? Terms = null);

internal enum HexLinkPreset { Collaborator, Worker, Viewer, Custom }

/// <summary>What an inviter proposes in both directions. Each device still decides what it grants.</summary>
internal sealed record HexLinkPairingTerms(HexLinkPreset Preset, HexLinkRights HostGrants, HexLinkRights HostRequests, bool ShareWorkspace = false);

/// <summary>How a pairing ended, on either side.</summary>
/// <param name="Paired">Whether both people confirmed and each stored the other.</param>
/// <param name="Reason">A word for how it ended: paired, declined-here, declined-there, mismatch, closed, busy, timeout, failed.</param>
/// <param name="Device">The other device, when it got as far as saying who it is.</param>
/// <param name="Code">The code that was shown, empty when it didn't get that far.</param>
/// <param name="Granted">What this side granted the other.</param>
/// <param name="Connection">The pairing's connection carrying on as a link, when it was paired and a link could be made of it.</param>
internal sealed record HexLinkPairResult(bool Paired, string Reason, HexLinkDevice? Device, string Code, HexLinkRights Granted, HexLinkConnection? Connection)
{
    public static HexLinkPairResult Failed(string reason, HexLinkDevice? device = null, string code = "") => new(false, reason, device, code, HexLinkRights.None, null);
}

/// <summary>
/// A link or pairing refused or broken, with a word for why, one of the words the other end said after <c>NO</c> (unknown,
/// busy, full, closed, version, mismatch) or one of this end's own (pin, refused, timeout).
/// </summary>
internal sealed class HexLinkRefused(string reason, string message, Exception? inner = null) : IOException(message, inner)
{
    /// <summary>Why, a word.</summary>
    public string Reason { get; } = reason;
}

/// <summary>The short-authentication-string arithmetic of pairing, apart from the sockets so it's tested on its own.</summary>
internal static class HexLinkSas
{
    /// <summary>How long a nonce is.</summary>
    public const int NONCE = 32;

    private static ReadOnlySpan<byte> LABEL => "hex-link-sas-1"u8;

    /// <summary>
    /// The six digits both sides show: SHA-256 over a label, the host's pin, the joiner's pin, the host's nonce and the
    /// joiner's nonce, its first four bytes as a number, the last six decimal digits of it. The pins are the raw 32 bytes,
    /// not their hex.
    /// </summary>
    public static string CodeOf(string hostPin, string joinerPin, byte[] hostNonce, byte[] joinerNonce)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(LABEL);
        hash.AppendData(Convert.FromHexString(hostPin));
        hash.AppendData(Convert.FromHexString(joinerPin));
        hash.AppendData(hostNonce);
        hash.AppendData(joinerNonce);
        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return (BinaryPrimitives.ReadUInt32BigEndian(digest) % 1_000_000).ToString("D6");
    }

    /// <summary>What a joiner commits to before it sees the host's nonce, the SHA-256 of its own.</summary>
    public static byte[] CommitmentOf(byte[] nonce) => SHA256.HashData(nonce);

    /// <summary>Whether a nonce revealed is the one committed to, compared in constant time.</summary>
    public static bool Keeps(byte[] commitment, byte[] nonce) =>
        commitment.Length == 32 && CryptographicOperations.FixedTimeEquals(commitment, SHA256.HashData(nonce));

    /// <summary>Bytes as the lower-case hex a pairing line carries.</summary>
    public static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    /// <summary>The 32 bytes a pairing line carried after its word, null when it doesn't say that word or isn't 32 bytes of hex.</summary>
    public static byte[]? BytesAfter(string? line, string word)
    {
        if (line is null || !line.StartsWith(word + " ", StringComparison.Ordinal))
            return null;
        string hex = line[(word.Length + 1)..];
        if (hex.Length != NONCE * 2)
            return null;
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
