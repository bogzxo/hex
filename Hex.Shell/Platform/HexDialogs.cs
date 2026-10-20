using Horizon.UI.Components;

namespace Hex.Shell;

/// <summary>
/// Picking a layout to open, a place to save one, a workspace, a folder or any file, with the engine's own window for it
/// (<see cref="UIFileDialogs"/>), Hex's titles and filters on it. It used to be Windows' own dialogs (comdlg32 and the
/// shell's folder picker), which nothing else has.
/// </summary>
public static class HexDialogs
{
    private static readonly (string, string)[] Layouts = [("Horizon files (*.hor)", "*.hor")];
    private static readonly (string, string)[] Workspaces = [("Hex workspaces (*.hex-workspace)", "*.hex-workspace")];

    public const string FOLDER_TITLE = "Pick a folder";
    public const string OPEN_WORKSPACE_TITLE = "Open a workspace";
    public const string SAVE_WORKSPACE_TITLE = "Save the workspace as";

    public const string OPEN_TITLE = "Open a layout";
    public const string SAVE_TITLE = "Save the layout as";

    /// <param name="directory">The folder it starts in.</param>
    public static Task<string?> Open(string directory, UIDialogHost host) => UIFileDialogs.Open(OPEN_TITLE, directory, Layouts, host);

    /// <summary>A workspace file to open (.hex-workspace).</summary>
    public static Task<string?> OpenWorkspace(string directory, UIDialogHost host) => UIFileDialogs.Open(OPEN_WORKSPACE_TITLE, directory, Workspaces, host);

    /// <summary>Where to save a workspace file.</summary>
    public static Task<string?> SaveWorkspace(string directory, string name, UIDialogHost host) =>
        UIFileDialogs.Save(SAVE_WORKSPACE_TITLE, directory, name, Workspaces, host);

    /// <summary>Any file to open, for an extension (hex.pick_file) or a language server's program.</summary>
    public static Task<string?> OpenFile(string title, string directory, IReadOnlyList<(string Name, string Pattern)> filter, UIDialogHost host) =>
        UIFileDialogs.Open(title, directory, filter, host);

    /// <summary>A folder, starting in the folder given.</summary>
    public static Task<string?> PickFolder(string directory, UIDialogHost host) => UIFileDialogs.Folder(FOLDER_TITLE, directory, host);

    /// <param name="name">The name the file is given unless another one is typed.</param>
    public static Task<string?> Save(string directory, string name, UIDialogHost host) => UIFileDialogs.Save(SAVE_TITLE, directory, name, Layouts, host);
}
