using System.Security.Cryptography;
using System.Text;

namespace Hex.Core;

/// <summary>
/// The API key of edit prediction as the settings file keeps it (docs/edit-prediction-plan.md, 6): protected by the
/// system with <see cref="HexLinkSecrets"/> (DPAPI for the Windows user, the macOS login Keychain, the Linux secret
/// service, or the file alone where there is none) and written <c>protected:</c> and the protected bytes in base64. A
/// key without that in front is one somebody typed into the file by hand, used as it is and protected at the next save.
/// Never logged and never in a message: what goes wrong says so without the key.
/// </summary>
internal static class PredictionKey
{
    /// <summary>What a protected key starts with in the settings file.</summary>
    public const string PREFIX = "protected:";

    /// <summary>The environment variable the key is taken from when the setting is empty.</summary>
    public const string VARIABLE = "HEX_EDIT_PREDICTION_API_KEY";

    /// <summary>Whether what the file has is a protected key rather than one typed in by hand.</summary>
    public static bool IsProtected(string stored) => stored.StartsWith(PREFIX, StringComparison.Ordinal);

    /// <summary>
    /// The key protected for the settings file, <c>protected:</c> and base64, empty for no key. <paramref name="fileOnly"/>
    /// keeps it to the file's own protection (HEX_LINK_KEYSTORE=file does the same), for tests.
    /// </summary>
    public static string Wrap(string key, bool fileOnly = false) =>
        key.Length == 0 ? string.Empty : PREFIX + Convert.ToBase64String(HexLinkSecrets.Protect(Encoding.UTF8.GetBytes(key), fileOnly));

    /// <summary>
    /// The key out of what the settings file has. A key typed in by hand comes back as it is, trimmed. A protected one is
    /// unwrapped; one that can't be (not base64, made by another user or machine, changed) comes back empty with
    /// <paramref name="failed"/> set. Throws <see cref="HexLinkSecretsUnavailableException"/> when the store it was
    /// protected by can't be reached now (the Keychain locked), when what the file has should be kept as it is.
    /// </summary>
    public static string Unwrap(string stored, out bool failed)
    {
        failed = false;
        string said = stored.Trim();
        if (!IsProtected(said))
            return said;

        try
        {
            return Encoding.UTF8.GetString(HexLinkSecrets.Unprotect(Convert.FromBase64String(said[PREFIX.Length..])));
        }
        catch (Exception e) when (e is FormatException or CryptographicException or DecoderFallbackException)
        {
            failed = true;
            return string.Empty;
        }
    }

    /// <summary>The key in the environment variable HEX_EDIT_PREDICTION_API_KEY, trimmed, empty for none.</summary>
    public static string FromEnvironment() => Environment.GetEnvironmentVariable(VARIABLE)?.Trim() ?? string.Empty;
}
