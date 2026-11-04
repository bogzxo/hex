namespace Hex.Core;

// Folders, open files and run targets all use the workspace file's folder as their starting point.
internal static class WorkspacePaths
{
    // A drive or volume has no last segment. Its root is the name the editor shows.
    public static string FolderName(string folder) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } name ? name : folder;

    public static string Read(string path, string relativeTo) => Path.GetFullPath(Path.Combine(relativeTo, path));

    public static string ReadFolder(string path, string relativeTo)
    {
        // Older workspaces dropped the last slash even from the root of a drive.
        if (OperatingSystem.IsWindows() && path.Length == 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            path += Path.DirectorySeparatorChar;
        return Path.TrimEndingDirectorySeparator(Read(path, relativeTo));
    }

    public static string Write(string path, string? relativeTo) =>
        (relativeTo is null ? path : Path.GetRelativePath(relativeTo, path)).Replace(Path.DirectorySeparatorChar, '/');
}
