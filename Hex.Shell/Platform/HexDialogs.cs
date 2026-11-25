using Horizon.Engine;
using Horizon.Platform;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Shell;

/// <summary>
/// Picking a layout to open, a place to save one, a workspace, a folder or any file, using the host's native Finder
/// panels on macOS (<see cref="IPlatformDialogs"/>) when available and not under scripted pointer self-test, and the
/// engine's own window (<see cref="UIFileDialogs"/>) everywhere else.
/// </summary>
public static class HexDialogs
{
    private static (string, string)[] Layouts => [(HexText.Get("dialogs.horizon_files"), "*.hor")];
    private static (string, string)[] Workspaces => [(HexText.Get("dialogs.hex_workspaces"), "*.hex-workspace")];

    /// <summary>Optional override for the native dialog provider; when null, resolves from the active platform host.</summary>
    public static IPlatformDialogs? NativeProvider { get; set; }

    public static string FOLDER_TITLE => HexText.Get("dialogs.folder_title");
    public static string OPEN_WORKSPACE_TITLE => HexText.Get("dialogs.open_workspace_title");
    public static string SAVE_WORKSPACE_TITLE => HexText.Get("dialogs.save_workspace_title");

    public static string OPEN_TITLE => HexText.Get("dialogs.open_title");
    public static string SAVE_TITLE => HexText.Get("dialogs.save_title");
    public static string SAVE_FILE_TITLE => HexText.Get("dialogs.save_file_title");

    private static IPlatformDialogs? ResolveNative()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("HORIZON_NATIVE_DIALOGS"), "off", StringComparison.OrdinalIgnoreCase))
            return null;
        if (UICompositor.IsPointerScripted)
            return null;

        return NativeProvider ?? GameEngine.Instance?.WindowManager?.Host?.Dialogs;
    }

    /// <param name="directory">The folder it starts in.</param>
    /// <param name="picked">A file in it picked as it opens, none for empty.</param>
    public static Task<string?> Open(string directory, UIDialogHost host, string picked = "") =>
        ResolveNative() is { } native
            ? native.OpenFile(OPEN_TITLE, directory, Layouts, picked)
            : UIFileDialogs.Open(OPEN_TITLE, directory, Layouts, host, picked);

    /// <summary>A workspace file to open (.hex-workspace).</summary>
    public static Task<string?> OpenWorkspace(string directory, UIDialogHost host) =>
        ResolveNative() is { } native
            ? native.OpenFile(OPEN_WORKSPACE_TITLE, directory, Workspaces)
            : UIFileDialogs.Open(OPEN_WORKSPACE_TITLE, directory, Workspaces, host);

    /// <summary>Where to save a workspace file.</summary>
    public static Task<string?> SaveWorkspace(string directory, string name, UIDialogHost host) =>
        ResolveNative() is { } native
            ? native.SaveFile(SAVE_WORKSPACE_TITLE, directory, name, Workspaces)
            : UIFileDialogs.Save(SAVE_WORKSPACE_TITLE, directory, name, Workspaces, host);

    /// <summary>Any file to open, for an extension (hex.pick_file) or a language server's program.</summary>
    public static Task<string?> OpenFile(string title, string directory, IReadOnlyList<(string Name, string Pattern)> filter, UIDialogHost host) =>
        ResolveNative() is { } native
            ? native.OpenFile(title, directory, filter)
            : UIFileDialogs.Open(title, directory, filter, host);

    /// <summary>A folder, starting in the folder given.</summary>
    public static Task<string?> PickFolder(string directory, UIDialogHost host) =>
        ResolveNative() is { } native
            ? native.OpenFolder(FOLDER_TITLE, directory)
            : UIFileDialogs.Folder(FOLDER_TITLE, directory, host);

    /// <param name="name">The name the file is given unless another one is typed.</param>
    public static Task<string?> Save(string directory, string name, UIDialogHost host) =>
        ResolveNative() is { } native
            ? native.SaveFile(SAVE_TITLE, directory, name, Layouts)
            : UIFileDialogs.Save(SAVE_TITLE, directory, name, Layouts, host);

    /// <summary>Where to save a file that isn't a layout, its own kind among the filters, nothing of a layout's about it.</summary>
    public static Task<string?> SaveFile(string directory, string name, IReadOnlyList<(string Name, string Pattern)> filter, UIDialogHost host) =>
        ResolveNative() is { } native
            ? native.SaveFile(SAVE_FILE_TITLE, directory, name, filter)
            : UIFileDialogs.Save(SAVE_FILE_TITLE, directory, name, filter, host);
}
