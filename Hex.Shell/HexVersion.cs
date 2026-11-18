using System.Reflection;

namespace Hex.Shell;

/// <summary>
/// Hex's version, the three numbers the update check and the tools go by and the name the release goes by for people,
/// both out of Directory.Build.props (Version and VersionName). Every Hex project is built with the same two.
/// </summary>
internal static class HexVersion
{
    /// <summary>The three numbers of it, 2.8.0.</summary>
    public static string Number { get; } = typeof(HexVersion).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>What the release is called, empty when it isn't called anything.</summary>
    public static string Name { get; } = Metadata("VersionName");

    /// <summary>Whether this is the development branch's nightly build, built with HexChannel set to dev.</summary>
    public static bool Dev { get; } = Metadata("Channel") is "dev";

    /// <summary>The nightly release this build went out as (nightly-57), empty for a stable build or one built here.</summary>
    public static string Nightly { get; } = Metadata("Nightly");

    private static string Metadata(string key) => typeof(HexVersion).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(meta => meta.Key == key)?.Value ?? string.Empty;

    /// <summary>
    /// Which build of Hex this is, one more every time the exe is built (HexBuildNumber.targets writes it beside the exe),
    /// empty for a Hex that has none (a test host, an old install).
    /// </summary>
    public static string Build { get; } = ReadBuild();

    private static string ReadBuild()
    {
        try
        {
            string file = Path.Combine(AppContext.BaseDirectory, "build.txt");
            return File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } said && said.All(char.IsAsciiDigit) ? said : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>The one a person reads, the number and the name after it, 2.8.0 thickboy.</summary>
    public static string Label { get; } = (Name.Length > 0 ? $"{Number} {Name}" : Number) + (Nightly.Length > 0 ? $" ({Nightly})" : Dev ? " (dev)" : string.Empty);
}
