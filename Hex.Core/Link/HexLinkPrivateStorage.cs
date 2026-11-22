using System.Security.Cryptography;
using System.Text;

namespace Hex.Core;

internal static class HexLinkPrivateStorage
{
    public static string? Choose(string linkData, string appData, IReadOnlyList<HexLinkFolder> exports)
    {
        string profile = NormalProfile(linkData);
        string beside = Path.Combine(profile, "wt");
        string? external = ExternalProfile(profile, appData);

        // Once created, keep the external journal and drafts together even if an export is removed.
        if (Shared(beside, exports) || (external is not null && HasPrivateState(external)))
            return external is not null && !Shared(external, exports) ? external : null;

        return beside;
    }

    internal static string? ChooseTreeCache(string linkData, string cacheDirectory, string appData, IReadOnlyList<HexLinkFolder> exports)
    {
        string usual = Path.GetFullPath(cacheDirectory);
        if (!Shared(usual, exports))
            return usual;
        if (ExternalProfile(NormalProfile(linkData), appData) is not { } external)
            return null;
        string fallback = Path.Combine(external, "tree-cache");
        return Shared(fallback, exports) ? null : fallback;
    }

    private static string NormalProfile(string linkData) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(linkData));

    private static string? ExternalProfile(string profile, string appData)
    {
        if (!Path.IsPathFullyQualified(appData))
            return null;
        string identity = PlatformPaths.Comparison == StringComparison.Ordinal ? profile : profile.ToUpperInvariant();
        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16));
        return Path.GetFullPath(Path.Combine(appData, "Hex", "link", key));
    }

    // A profile created for hash caches alone must not change where existing private drafts are loaded from.
    private static bool HasPrivateState(string profile) =>
        Directory.Exists(Path.Combine(profile, "pub")) || Directory.Exists(Path.Combine(profile, "drafts"));

    private static bool Shared(string storage, IReadOnlyList<HexLinkFolder> exports) =>
        exports.Any(folder => PlatformPaths.IsUnder(storage, folder.Path) || PlatformPaths.IsUnder(folder.Path, storage));
}
