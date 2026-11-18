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
        if (release is null || !File.Exists(Path.Combine(root, InstallPaths.Manifest)))
            return;
        // One channel's installation is never turned into the other's, each keeps its folder, its entries and its updates
        InstallChannel installed = InstallChannels.Of(root);
        if (installed != release.Channel)
            throw new IOException(InstallText.Get("errors.other_channel", ("channel", InstallText.Get($"channel.{installed.Word()}"))));
        string had = InstallTransaction.ReadManifest(root).Version;
        if (InstallChannels.IsNewer(release.Channel, had, release.Version))
            throw new IOException(InstallText.Get("errors.downgrade"));
    }
}
