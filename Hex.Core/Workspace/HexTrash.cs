using System.Runtime.InteropServices;

namespace Hex.Core;

/// <summary>
/// Where a file or a folder deleted in the explorer goes, which is never straight out of existence. The recycle bin on
/// Windows (the shell's own delete, the one that can be undone), the Trash on a Mac, and the freedesktop trash anywhere
/// else, so whatever goes can be got back by whoever deleted it.
/// </summary>
internal static unsafe partial class HexTrash
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    /// <summary>SHFILEOPSTRUCTW, field for field.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileOperation
    {
        public nint hwnd;
        public uint wFunc;
        public char* pFrom;
        public char* pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public nint hNameMappings;
        public char* lpszProgressTitle;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    private static partial int SHFileOperation(FileOperation* operation);

    /// <summary>Puts a file or a folder in the trash. Throws with what went wrong.</summary>
    public static void Discard(string path)
    {
        string full = Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full))
            throw new FileNotFoundException($"there is no {full}");

        if (OperatingSystem.IsWindows())
        {
            // The paths end with two zeros, it is a list of them
            string from = full + "\0\0";
            fixed (char* fromPointer = from)
            {
                var operation = new FileOperation
                {
                    wFunc = FO_DELETE,
                    pFrom = fromPointer,
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
                };

                int result = SHFileOperation(&operation);
                if (result != 0 || operation.fAnyOperationsAborted != 0)
                    throw new IOException($"Windows wouldn't put it in the recycle bin (error {result})");
            }

            return;
        }

        // The freedesktop trash is files/ and info/ under $XDG_DATA_HOME/Trash, a .trashinfo a file saying where it came
        // from and when, which is what the file manager's Restore goes by
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string trash = OperatingSystem.IsMacOS() ? Path.Combine(home, ".Trash") : Path.Combine(Horizon.Core.Platform.PlatformPaths.DataRoot, "Trash", "files");
        string? info = OperatingSystem.IsMacOS() ? null : Path.Combine(Path.GetDirectoryName(trash)!, "info");
        Directory.CreateDirectory(trash);
        if (info is not null)
            Directory.CreateDirectory(info);

        // A name the trash hasn't got yet, in either of its folders
        string name = Path.GetFileName(full.TrimEnd('/', '\\'));
        string to = Path.Combine(trash, name);
        for (int number = 2; File.Exists(to) || Directory.Exists(to) || (info is not null && File.Exists(Path.Combine(info, Path.GetFileName(to) + ".trashinfo"))); number++)
            to = Path.Combine(trash, $"{Path.GetFileNameWithoutExtension(name)} {number}{Path.GetExtension(name)}");

        // The info first, the spec says, so a file in the trash always has one
        string? infoFile = info is null ? null : Path.Combine(info, Path.GetFileName(to) + ".trashinfo");
        if (infoFile is not null)
        {
            string encoded = string.Join('/', full.Split('/').Select(Uri.EscapeDataString));
            File.WriteAllText(infoFile, $"[Trash Info]\nPath={encoded}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n");
        }

        try
        {
            if (Directory.Exists(full))
                Directory.Move(full, to);
            else
                File.Move(full, to);
        }
        catch (Exception)
        {
            // Not moved (another drive, which has a trash of its own this doesn't use), so not in the trash either
            if (infoFile is not null)
                File.Delete(infoFile);
            throw;
        }
    }
}
