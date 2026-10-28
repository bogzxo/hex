using System.Runtime.InteropServices;

namespace Hex.Installation;

public static class InstallPaths
{
    public const string Manifest = ".hex-install.json";
    public static string Rid => (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "osx" : "unknown")
        + "-" + RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
    public static string Executable => OperatingSystem.IsWindows() ? "Hex.exe" : "Hex";
    public static string InstallerExecutable => OperatingSystem.IsWindows() ? "Hex.Installer.exe" : "Hex.Installer";
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string DefaultRoot => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Hex")
        : OperatingSystem.IsMacOS() ? Path.Combine(Home, "Applications", "Hex.app", "Contents", "MacOS")
        : Path.Combine(Home, ".local", "opt", "hex");

    public static void ValidateRid(string rid)
    {
        if (rid is not ("win-x64" or "win-arm64" or "linux-x64" or "linux-arm64" or "osx-x64" or "osx-arm64"))
            throw new PlatformNotSupportedException(InstallText.Get("errors.platform", ("platform", rid)));
    }

    public static string Under(string root, string relative)
    {
        // Also reject Windows paths on Unix, so the archive has identical meaning on every platform.
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':') || relative.StartsWith('/')
            || relative.Split('/').Any(p => p is ".." or "." || p.EndsWith(' ') || p.EndsWith('.')))
            throw new InvalidDataException(InstallText.Get("errors.package_path", ("path", relative)));
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException(InstallText.Get("errors.path_escape"));
        return full;
    }

    public static void NoLinks(string path)
    {
        for (string? at = Path.GetFullPath(path); at is not null; at = Path.GetDirectoryName(at))
            if ((File.Exists(at) || Directory.Exists(at)) && File.GetAttributes(at).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException(InstallText.Get("errors.destination_link", ("path", at)));
    }

    public static void CopyTree(string from, string to)
    {
        NoLinks(from);
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from))
        {
            NoLinks(file);
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
        }
        foreach (string folder in Directory.EnumerateDirectories(from))
            CopyTree(folder, Path.Combine(to, Path.GetFileName(folder)));
    }
}
