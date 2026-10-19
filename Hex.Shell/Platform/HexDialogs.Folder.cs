using System.Runtime.InteropServices;

namespace Hex.Shell;

/// <summary>
/// Picking a folder with the dialog Explorer has had since Vista (IFileOpenDialog told to pick folders), the one with the
/// address bar, the quick access down the side and a search box, and not the little tree SHBrowseForFolder puts up.
/// It's COM, and called through its table of functions by hand rather than with the runtime's COM interop, which can
/// neither be trimmed nor compiled ahead of time, same reason the other dialogs are comdlg32 and not Windows Forms.
/// </summary>
public static unsafe partial class HexDialogs
{
    private static readonly Guid FileOpenDialogClass = new("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");
    private static readonly Guid FileOpenDialogInterface = new("d57c7288-d4ad-4768-be02-9d969532d960");
    private static readonly Guid ShellItemInterface = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    private const uint CLSCTX_INPROC_SERVER = 0x1;
    private const uint COINIT_APARTMENTTHREADED = 0x2;

    private const uint FOS_NOCHANGEDIR = 0x8;
    private const uint FOS_PICKFOLDERS = 0x20;
    private const uint FOS_FORCEFILESYSTEM = 0x40;
    private const uint FOS_PATHMUSTEXIST = 0x800;

    // What the shell item is asked to say about itself, its path on the disk
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    // Where the functions used are in the tables. IUnknown's three, IModalWindow's Show, then IFileDialog's own
    private const int RELEASE = 2;
    private const int SHOW = 3;
    private const int SET_OPTIONS = 9;
    private const int GET_OPTIONS = 10;
    private const int SET_FOLDER = 12;
    private const int SET_TITLE = 17;
    private const int GET_RESULT = 20;
    private const int GET_DISPLAY_NAME = 5;

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint model);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* type, nint outer, uint context, Guid* face, nint* made);

    [LibraryImport("ole32.dll")]
    private static partial void CoTaskMemFree(nint memory);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, nint context, Guid* face, nint* item);

    // The n-th function of a COM object's table
    private static nint Slot(nint thing, int index) => (*(nint**)thing)[index];

    private static void Release(nint thing)
    {
        if (thing != 0)
            ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(thing, RELEASE))(thing);
    }

    private static string? AskFolder(string directory, nint window)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The picker of folders is the one Windows has.");

        // The dialog thread is a fresh one of our own, so it's always ours to start COM on and take down after
        int started = CoInitializeEx(0, COINIT_APARTMENTTHREADED);
        nint dialog = 0;
        try
        {
            Guid type = FileOpenDialogClass, face = FileOpenDialogInterface;
            Check(CoCreateInstance(&type, 0, CLSCTX_INPROC_SERVER, &face, &dialog));

            uint options;
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(dialog, GET_OPTIONS))(dialog, &options));
            options |= FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(dialog, SET_OPTIONS))(dialog, options));

            fixed (char* title = FOLDER_TITLE)
                ((delegate* unmanaged[Stdcall]<nint, char*, int>)Slot(dialog, SET_TITLE))(dialog, title);

            StartIn(dialog, directory);

            // Closed without picking is ERROR_CANCELLED as an HRESULT, any failure at all means nothing was picked
            if (((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(dialog, SHOW))(dialog, window) < 0)
                return null;

            return Result(dialog);
        }
        finally
        {
            Release(dialog);
            if (started >= 0)
                CoUninitialize();
        }
    }

    // Opens the dialog on a folder, the one Hex was browsing. A folder that's gone is no reason not to ask, it opens where it last was
    private static void StartIn(nint dialog, string directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;

        nint folder = 0;
        Guid face = ShellItemInterface;
        if (SHCreateItemFromParsingName(Path.GetFullPath(directory), 0, &face, &folder) < 0)
            return;

        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(dialog, SET_FOLDER))(dialog, folder);
        Release(folder);
    }

    // The path of the folder picked, asked of the shell item the dialog hands back
    private static string? Result(nint dialog)
    {
        nint item = 0;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(dialog, GET_RESULT))(dialog, &item) < 0)
            return null;

        try
        {
            char* path = null;
            if (((delegate* unmanaged[Stdcall]<nint, uint, char**, int>)Slot(item, GET_DISPLAY_NAME))(item, SIGDN_FILESYSPATH, &path) < 0)
                return null;

            string picked = new(path);
            CoTaskMemFree((nint)path);
            return picked;
        }
        finally
        {
            Release(item);
        }
    }

    private static void Check(int result)
    {
        if (result < 0)
            Marshal.ThrowExceptionForHR(result);
    }
}
