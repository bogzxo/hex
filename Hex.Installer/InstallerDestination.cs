using Hex.Installation;

namespace Hex.Installer;

internal static class InstallerDestination
{
    // The picker selects an installation folder on Windows/Linux and the containing folder for a Mac app bundle.
    public static string FromFolder(string folder, bool macOS)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (macOS && !(Path.GetFileName(root) == "MacOS" && Path.GetFileName(Path.GetDirectoryName(root)) == "Contents"))
            root = root.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(root, "Contents", "MacOS") : Path.Combine(root, "Hex.app", "Contents", "MacOS");
        return InstallTransaction.ValidateRoot(root);
    }

    public static void ValidateVersion(string root, ReleaseAsset? release)
    {
        if (release is not null && File.Exists(Path.Combine(root, InstallPaths.Manifest)) &&
            ReleaseClient.VersionOf(release.Version) < ReleaseClient.VersionOf(InstallTransaction.ReadManifest(root).Version))
            throw new IOException(InstallText.Get("errors.downgrade"));
    }
}
