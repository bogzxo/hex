using Horizon.UI.Components;

namespace Hex.Shell;

/// <summary>
/// Where a mode shows a dialog and what it has for its places: <see cref="Present"/> puts the window over the mode
/// (coloured as its other windows are), <see cref="Roots"/> are its workspace's folders.
/// </summary>
public sealed record HexDialogHost(Action<WindowPanel> Present, IReadOnlyList<string> Roots);

/// <summary>
/// Picking a layout to open, a place to save one, a workspace, a folder or any file, with Hex's own window for it
/// (<see cref="HexFileBrowser"/>), the same on Windows, macOS and Linux. It used to be Windows' own dialogs (comdlg32
/// and the shell's folder picker), which nothing else has. Each hands back what was picked as a task, finished on the
/// UI's thread when the window is done with, null if it was closed without picking anything.
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
    public static Task<string?> Open(string directory, HexDialogHost host) =>
        Show(host, HexBrowse.Open, OPEN_TITLE, directory, string.Empty, Layouts);

    /// <summary>A workspace file to open (.hex-workspace).</summary>
    public static Task<string?> OpenWorkspace(string directory, HexDialogHost host) =>
        Show(host, HexBrowse.Open, OPEN_WORKSPACE_TITLE, directory, string.Empty, Workspaces);

    /// <summary>Where to save a workspace file.</summary>
    public static Task<string?> SaveWorkspace(string directory, string name, HexDialogHost host) =>
        Show(host, HexBrowse.Save, SAVE_WORKSPACE_TITLE, directory, name, Workspaces);

    /// <summary>
    /// Any file to open, for an extension (hex.pick_file). The filter is pairs of what it says and what it matches,
    /// "Pictures" and "*.png;*.jpg", all files after them.
    /// </summary>
    public static Task<string?> OpenFile(string title, string directory, IReadOnlyList<(string Name, string Pattern)> filter, HexDialogHost host) =>
        Show(host, HexBrowse.Open, title, directory, string.Empty, filter);

    /// <summary>A folder, starting in the folder given.</summary>
    public static Task<string?> PickFolder(string directory, HexDialogHost host) =>
        Show(host, HexBrowse.Folder, FOLDER_TITLE, directory, string.Empty, []);

    /// <param name="name">The name the file is given unless another one is typed.</param>
    public static Task<string?> Save(string directory, string name, HexDialogHost host) =>
        Show(host, HexBrowse.Save, SAVE_TITLE, directory, name, Layouts);

    private static Task<string?> Show(HexDialogHost host, HexBrowse kind, string title, string directory, string name, IReadOnlyList<(string, string)> filter)
    {
        var browser = new HexFileBrowser(kind, title, directory, name, filter, host.Roots);
        host.Present(browser.Window);
        return browser.Picked;
    }
}
