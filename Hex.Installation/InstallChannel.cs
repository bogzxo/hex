using System.Globalization;
using System.Text.Json.Nodes;

namespace Hex.Installation;

/// <summary>
/// Which Hex an installation is. Stable is what's released, a GitHub release with a version for a tag. Dev is the
/// development branch built every night, a prerelease tagged nightly and the number of the run that built it. Each is
/// installed in a folder of its own with its own entry in the system, so both can be on a machine side by side, and
/// each updates from its own kind of release.
/// </summary>
public enum InstallChannel
{
    Stable,
    Dev
}

/// <summary>What differs between the two, their names on the system, where they go and how a release is told apart.</summary>
public static class InstallChannels
{
    /// <summary>The start of a nightly release's tag, the number of the run that built it after it.</summary>
    public const string NIGHTLY = "nightly-";

    /// <summary>A channel by the word for it, dev (or development, nightly) and stable, stable for anything else.</summary>
    public static InstallChannel Parse(string? said) => said?.Trim().ToLowerInvariant() switch
    {
        "dev" or "development" or "nightly" => InstallChannel.Dev,
        _ => InstallChannel.Stable
    };

    /// <summary>The word a channel is written as, in a manifest and on the command line.</summary>
    public static string Word(this InstallChannel channel) => channel == InstallChannel.Dev ? "dev" : "stable";

    /// <summary>Where a channel installs unless somebody picks somewhere, the dev build beside the stable one.</summary>
    public static string DefaultRoot(InstallChannel channel)
    {
        if (channel == InstallChannel.Stable)
            return InstallPaths.DefaultRoot;
        return OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Hex Dev")
            : OperatingSystem.IsMacOS() ? Path.Combine(InstallPaths.Home, "Applications", "Hex Dev.app", "Contents", "MacOS")
            : Path.Combine(InstallPaths.Home, ".local", "opt", "hex-dev");
    }

    /// <summary>
    /// What a channel's installation is called on the system, the product's name, its uninstall entry, its Run command,
    /// its shortcuts and launchers. The dev build's say Dev, so the two installations never take each other's.
    /// </summary>
    public static (string Name, string Key, string Command, string Prefix) Names(InstallChannel channel) => channel == InstallChannel.Dev
        ? ("Hex Dev", "Hex Dev", "HexDev.exe", "hex-dev")
        : ("Hex", "Hex", "Hex.exe", "hex");

    /// <summary>The run a nightly release's tag says it was built by, null for a tag that isn't a nightly's.</summary>
    public static long? NightlyNumber(string tag) =>
        tag.StartsWith(NIGHTLY, StringComparison.Ordinal) && long.TryParse(tag.AsSpan(NIGHTLY.Length), NumberStyles.None, CultureInfo.InvariantCulture, out long run)
            ? run : null;

    /// <summary>
    /// Whether a release is newer than what's installed. A stable one by its version, a nightly by the run that built it,
    /// a later run being a later build of the development branch whatever version it says.
    /// </summary>
    public static bool IsNewer(InstallChannel channel, string release, string installed)
    {
        if (channel == InstallChannel.Dev)
            return NightlyNumber(release) is { } latest && (NightlyNumber(installed) is not { } had || latest > had);
        return ReleaseClient.VersionOf(release) > ReleaseClient.VersionOf(installed);
    }

    /// <summary>The channel an installation's record says it is, stable for one written before there were two.</summary>
    public static InstallChannel Of(string root)
    {
        string manifest = Path.Combine(root, InstallPaths.Manifest);
        try
        {
            return File.Exists(manifest) && JsonNode.Parse(File.ReadAllText(manifest))?["channel"] is JsonValue value && value.TryGetValue(out string? said)
                ? Parse(said) : InstallChannel.Stable;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return InstallChannel.Stable;
        }
    }
}
