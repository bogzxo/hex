namespace Hex.Core;

/// <summary>
/// Whether Hex is running from a macOS application bundle (Hex.app/Contents/MacOS/Hex). A bundle is signed and its
/// seal breaks if anything inside it changes, so a Hex in one never writes next to itself: its settings, data and
/// caches are in the user's folders (~/Library/Application Support/Hex, ~/Library/Caches/Hex), its log in
/// ~/Library/Logs/Hex, and what it ships (Assets, shaders, the built in extensions) is read from Contents/Resources.
/// </summary>
public static class HexBundle
{
    /// <summary>Whether this Hex is the program of an .app.</summary>
    public static bool Inside { get; } = OperatingSystem.IsMacOS() && IsBundleProgram(AppContext.BaseDirectory);

    /// <summary>The .app folder, null when Hex isn't in one.</summary>
    public static string? Root { get; } = Inside ? RootOf(AppContext.BaseDirectory) : null;

    /// <summary>Contents/Resources, where an app keeps what isn't code, null when Hex isn't in a bundle.</summary>
    public static string? Resources { get; } = Root is null ? null : Path.Combine(Root, "Contents", "Resources") + Path.DirectorySeparatorChar;

    /// <summary>Where a Hex in a bundle writes its debug log, ~/Library/Logs/Hex, which Console.app shows.</summary>
    public static string LogFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "Hex");

    /// <summary>Whether a program folder is an app's Contents/MacOS.</summary>
    internal static bool IsBundleProgram(string programFolder) => RootOf(programFolder) is not null;

    /// <summary>The .app folder of an app's Contents/MacOS folder, null for any other folder.</summary>
    internal static string? RootOf(string programFolder)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(programFolder);
        if (Path.GetFileName(trimmed) != "MacOS")
            return null;

        string? contents = Path.GetDirectoryName(trimmed);
        if (contents is null || Path.GetFileName(contents) != "Contents")
            return null;

        string? app = Path.GetDirectoryName(contents);
        return app is not null && app.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? app : null;
    }
}
