using System.Security.Cryptography;
using System.Text;

namespace Hex.Core;

internal static class HexLinkPrivateStorage
{
    public static string? Choose(string linkData, string appData, IReadOnlyList<HexLinkFolder> exports)
    {
        string profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(linkData));
        string beside = Path.Combine(profile, "wt");
        string? external = null;
        if (Path.IsPathFullyQualified(appData))
        {
            string identity = PlatformPaths.Comparison == StringComparison.Ordinal ? profile : profile.ToUpperInvariant();
            string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16));
            external = Path.GetFullPath(Path.Combine(appData, "Hex", "link", key));
        }

        // Once created, keep the external journal and drafts together even if an export is removed.
        if (Shared(beside, exports) || (external is not null && Directory.Exists(external)))
            return external is not null && !Shared(external, exports) ? external : null;

        return beside;
    }

    private static bool Shared(string storage, IReadOnlyList<HexLinkFolder> exports) =>
        exports.Any(folder => PlatformPaths.IsUnder(storage, folder.Path) || PlatformPaths.IsUnder(folder.Path, storage));
}
