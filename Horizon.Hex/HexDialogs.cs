using System.Runtime.InteropServices;

namespace Horizon.Hex;

/// <summary>
/// The dialogs of Windows for picking a layout to open and a place to save one. They run on a thread of their
/// own (they need one of a kind the engine doesn't have) while the editor carries on drawing, and hand back
/// what was picked as a task. Null if the dialog was closed without picking anything.
/// They are asked for from Windows directly (comdlg32) rather than through Windows Forms, which can be neither
/// trimmed nor compiled ahead of time and would be most of the editor by size.
/// </summary>
internal static unsafe partial class HexDialogs
{
    // What the dialog lists. Pairs of what it says and what that matches, each ended by a zero and the lot by another
    private const string FILTER = "Horizon files (*.hor)\0*.hor\0All files (*.*)\0*.*\0";
    private const string EXTENSION = "hor";
    private const string WORKSPACE_FILTER = "Hex workspaces (*.hex-workspace)\0*.hex-workspace\0All files (*.*)\0*.*\0";
    private const string WORKSPACE_EXTENSION = "hex-workspace";

    public const string FOLDER_TITLE = "Pick a folder";
    public const string OPEN_WORKSPACE_TITLE = "Open a workspace";
    public const string SAVE_WORKSPACE_TITLE = "Save the workspace as";

    public const string OPEN_TITLE = "Open a layout";
    public const string SAVE_TITLE = "Save the layout as";

    // As long as a path gets in Windows unless it is told otherwise, in characters
    private const int MAX_PATH = 32768;

    private const uint OFN_OVERWRITEPROMPT = 0x00000002;
    private const uint OFN_HIDEREADONLY = 0x00000004;
    private const uint OFN_NOCHANGEDIR = 0x00000008;
    private const uint OFN_PATHMUSTEXIST = 0x00000800;
    private const uint OFN_FILEMUSTEXIST = 0x00001000;
    private const uint OFN_EXPLORER = 0x00080000;

    /// <summary>OPENFILENAMEW, field for field.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct OpenFileName
    {
        public uint lStructSize;
        public nint hwndOwner;
        public nint hInstance;
        public char* lpstrFilter;
        public char* lpstrCustomFilter;
        public uint nMaxCustFilter;
        public uint nFilterIndex;
        public char* lpstrFile;
        public uint nMaxFile;
        public char* lpstrFileTitle;
        public uint nMaxFileTitle;
        public char* lpstrInitialDir;
        public char* lpstrTitle;
        public uint Flags;
        public ushort nFileOffset;
        public ushort nFileExtension;
        public char* lpstrDefExt;
        public nint lCustData;
        public nint lpfnHook;
        public char* lpTemplateName;
        public void* pvReserved;
        public uint dwReserved;
        public uint FlagsEx;
    }

    [LibraryImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW")]
    private static partial int GetOpenFileName(OpenFileName* dialog);

    [LibraryImport("comdlg32.dll", EntryPoint = "GetSaveFileNameW")]
    private static partial int GetSaveFileName(OpenFileName* dialog);

    /// <param name="directory">The folder the dialog starts in.</param>
    /// <param name="window">The handle of the window the dialog belongs to, zero for none.</param>
    public static Task<string?> Open(string directory, nint window) =>
        Show(window, OPEN_TITLE, directory, string.Empty, save: false);

    /// <summary>A workspace file to open (.hex-workspace).</summary>
    public static Task<string?> OpenWorkspace(string directory, nint window) =>
        Show(window, OPEN_WORKSPACE_TITLE, directory, string.Empty, save: false, WORKSPACE_FILTER, WORKSPACE_EXTENSION);

    /// <summary>Where to save a workspace file.</summary>
    public static Task<string?> SaveWorkspace(string directory, string name, nint window) =>
        Show(window, SAVE_WORKSPACE_TITLE, directory, name, save: true, WORKSPACE_FILTER, WORKSPACE_EXTENSION);

    /// <summary>A folder, the shell's own picker of folders.</summary>
    public static Task<string?> PickFolder(nint window) => OnThread(() => AskFolder(window));

    /// <param name="directory">The folder the dialog starts in.</param>
    /// <param name="name">The name the file is given unless another one is typed.</param>
    /// <param name="window">The handle of the window the dialog belongs to, zero for none.</param>
    public static Task<string?> Save(string directory, string name, nint window) =>
        Show(window, SAVE_TITLE, directory, name, save: true);

    private static Task<string?> Show(nint window, string title, string directory, string name, bool save, string filter = FILTER, string extension = EXTENSION) =>
        OnThread(() => Ask(window, title, directory, name, save, filter, extension));

    private static Task<string?> OnThread(Func<string?> asking)
    {
        var result = new TaskCompletionSource<string?>();

        var thread = new Thread(() =>
        {
            try
            {
                result.SetResult(asking());
            }
            catch (Exception e)
            {
                result.SetException(e);
            }
        })
        {
            IsBackground = true,
            Name = "Hex file dialog"
        };

        // The dialog is a window of the shell, which wants a thread like this one to live on
        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.STA);

        thread.Start();
        return result.Task;
    }

    /// <summary>
    /// Helper to show the dialog and wait for it, on whatever thread this is called from.
    /// </summary>
    /// <returns>The file that was picked, null if the dialog was closed without picking one.</returns>
    private static string? Ask(nint window, string title, string directory, string name, bool save, string filter, string extension)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The dialogs for opening and saving a layout are the ones Windows has.");

        // Where the dialog writes what was picked, starting out with the name it suggests
        char[] file = new char[MAX_PATH];
        name.AsSpan(0, Math.Min(name.Length, MAX_PATH - 1)).CopyTo(file);

        fixed (char* filePointer = file)
        fixed (char* filterPointer = filter)
        fixed (char* titlePointer = title)
        fixed (char* directoryPointer = directory)
        fixed (char* extensionPointer = extension)
        {
            var dialog = new OpenFileName
            {
                lStructSize = (uint)sizeof(OpenFileName),
                hwndOwner = window,
                lpstrFilter = filterPointer,
                nFilterIndex = 1,
                lpstrFile = filePointer,
                nMaxFile = MAX_PATH,
                lpstrInitialDir = directoryPointer,
                lpstrTitle = titlePointer,
                lpstrDefExt = extensionPointer,

                // The folder the editor works in stays what it is, whatever folder is looked into
                Flags = OFN_EXPLORER | OFN_NOCHANGEDIR | OFN_HIDEREADONLY | OFN_PATHMUSTEXIST | (save ? OFN_OVERWRITEPROMPT : OFN_FILEMUSTEXIST)
            };

            bool picked = (save ? GetSaveFileName(&dialog) : GetOpenFileName(&dialog)) != 0;
            return picked ? new string(filePointer) : null;
        }
    }

    /* Picking a folder, the shell's dialog for it */

    private const uint BIF_RETURNONLYFSDIRS = 0x0001;
    private const uint BIF_NEWDIALOGSTYLE = 0x0040;

    /// <summary>BROWSEINFOW, field for field.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BrowseInfo
    {
        public nint hwndOwner;
        public nint pidlRoot;
        public char* pszDisplayName;
        public char* lpszTitle;
        public uint ulFlags;
        public nint lpfn;
        public nint lParam;
        public int iImage;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHBrowseForFolderW")]
    private static partial nint SHBrowseForFolder(BrowseInfo* info);

    [LibraryImport("shell32.dll", EntryPoint = "SHGetPathFromIDListW")]
    private static partial int SHGetPathFromIDList(nint list, char* path);

    [LibraryImport("ole32.dll")]
    private static partial int OleInitialize(nint reserved);

    [LibraryImport("ole32.dll")]
    private static partial void CoTaskMemFree(nint memory);

    private static string? AskFolder(nint window)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The picker of folders is the one Windows has.");

        // The new style of the dialog wants OLE on its thread
        OleInitialize(0);

        char[] name = new char[MAX_PATH];
        char[] path = new char[MAX_PATH];

        fixed (char* namePointer = name)
        fixed (char* pathPointer = path)
        fixed (char* titlePointer = FOLDER_TITLE)
        {
            var info = new BrowseInfo
            {
                hwndOwner = window,
                pszDisplayName = namePointer,
                lpszTitle = titlePointer,
                ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE
            };

            nint picked = SHBrowseForFolder(&info);
            if (picked == 0)
                return null;

            try
            {
                return SHGetPathFromIDList(picked, pathPointer) != 0 ? new string(pathPointer) : null;
            }
            finally
            {
                CoTaskMemFree(picked);
            }
        }
    }
}
