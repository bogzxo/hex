using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The workspace, the folders the editor has open (see HexWorkspace), added and taken out, opened and saved as a file,
// and the one that was open last brought back when the editor starts. The tree of them on the left is Explorer.
internal sealed partial class HexScene
{
    // What a workspace that was never saved is kept in, in the settings folder next to the editor, so it is there the next time
    private const string UNTITLED_WORKSPACE = "hex_untitled.hex-workspace";

    private HexWorkspace workspace = new();

    // Whether the workspace of last time has been brought back yet, it isn't written over before then
    private bool workspaceRestored;

    // The rows of the explorer, for the self-test to find
    private readonly List<(string Path, bool IsFolder, UITreeItem Row)> explorerRows = [];

    // The most rows the explorer lists, a folder of thousands of files would have the list take seconds to make
    private const int MAX_EXPLORER_ROWS = 4000;

    private string? UntitledWorkspacePath => options.SelfTest || options.Check.Length > 0 ? null : HexFiles.Kept(UNTITLED_WORKSPACE);

    /// <summary>
    /// Helper method to bring back the workspace that was open the last time, its folders and the files that were open in
    /// it, unless the editor is testing itself or was told what to open.
    /// </summary>
    private void RestoreWorkspace()
    {
        if (options.SelfTest || options.Check.Length > 0)
        {
            workspaceRestored = true;
            return;
        }

        try
        {
            if (settings.LastWorkspace.Length > 0 && File.Exists(settings.LastWorkspace))
                workspace = HexWorkspace.Load(settings.LastWorkspace);
            else if (UntitledWorkspacePath is { } untitled && File.Exists(untitled))
                workspace.Read(File.ReadAllText(untitled), AppContext.BaseDirectory);
        }
        catch (Exception e)
        {
            Say($"the workspace of last time didn't open, {e.Message}", error: true);
            workspace = new HexWorkspace();
        }

        workspaceRestored = true;
        if (workspace.IsEmpty)
            return;

        browsing = true;
        treeDirty = true;

        if (options.Open is null)
        {
            foreach (string file in workspace.OpenFiles.ToArray())
            {
                if (File.Exists(file))
                    Open(file);
            }
        }
    }

    /// <summary>Helper method to keep the workspace in its file (or next to the editor while it has none), after any change to it.</summary>
    private void KeepWorkspace()
    {
        if (!workspaceRestored)
            return;

        workspace.Terminal = terminalPanel.Tabs.Count > 0 ? TerminalSessions.Value(terminalPanel) : workspace.Terminal;

        workspace.OpenFiles.Clear();
        foreach (var open in documents)
        {
            if (open.Path is { } path && open != document)
                workspace.OpenFiles.Add(Path.GetFullPath(path));
        }
        if (document?.Path is { } shown)
            workspace.OpenFiles.Add(Path.GetFullPath(shown));

        try
        {
            if (workspace.FilePath is { } file)
                workspace.Save(file);
            else if (UntitledWorkspacePath is { } untitled)
                File.WriteAllText(untitled, workspace.Write(null));
        }
        catch (Exception e)
        {
            Say($"couldn't keep the workspace, {e.Message}", error: true);
        }

        string last = workspace.FilePath ?? string.Empty;
        if (settings.LastWorkspace != last)
        {
            settings.LastWorkspace = last;
            settings.Save();
        }
    }

    /* Folders in and out */

    /// <summary>Asks for a folder, with Hex's own dialog opened where the explorer is, and a line to type it into for the self test.</summary>
    private void AskFolder(Action<string> picked)
    {
        if (!options.SelfTest)
        {
            Ask(HexDialogs.PickFolder(browseDirectory, DialogHost), picked);
            return;
        }

        AskText("A folder", "The path of the folder", browseDirectory, "Add", typed =>
        {
            if (Directory.Exists(typed.Trim()))
                picked(typed.Trim());
            else
                Say($"there is no folder {typed}", error: true);
        });
    }

    /// <summary>Adds a folder to the workspace, its files in the explorer from now on.</summary>
    private void AddFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            Say($"there is no folder {folder}", error: true);
            return;
        }

        if (!workspace.Add(folder))
        {
            Say($"{folder} is in the workspace already");
            return;
        }

        browseDirectory = Path.GetFullPath(folder);
        browsing = true;
        results = null;
        treeDirty = true;
        codeEditing.ForgetVocabulary();
        KeepWorkspace();
        Say($"added {folder}, the workspace has {workspace.Folders.Count} folders");
    }

    /// <summary>A workspace of one folder, the one that was open going away (its file stays where it is).</summary>
    private void OpenFolder(string folder)
    {
        workspace = new HexWorkspace();
        AddFolder(folder);
    }

    private void RemoveFolder(string folder)
    {
        if (!workspace.Remove(folder))
            return;

        treeDirty = true;
        codeEditing.ForgetVocabulary();
        KeepWorkspace();
        Say($"{Path.GetFileName(folder)} is out of the workspace, nothing in it was touched");
    }

    private void OpenWorkspaceFile(string path)
    {
        try
        {
            workspace = HexWorkspace.Load(path);
        }
        catch (Exception e)
        {
            Say($"{Path.GetFileName(path)} isn't a workspace, {e.Message}", error: true);
            return;
        }

        browsing = true;
        results = null;
        treeDirty = true;
        codeEditing.ForgetVocabulary();

        foreach (string file in workspace.OpenFiles.ToArray())
        {
            if (File.Exists(file))
                Open(file);
        }

        KeepWorkspace();
        Say($"opened the workspace {workspace.Name}, {workspace.Folders.Count} folders");
    }

    private void SaveWorkspaceAs(string path)
    {
        if (!path.EndsWith(HexWorkspace.EXTENSION, StringComparison.OrdinalIgnoreCase))
            path += HexWorkspace.EXTENSION;

        try
        {
            KeepWorkspace();
            workspace.Save(path);
            KeepWorkspace();
            Say($"saved the workspace as {path}");
        }
        catch (Exception e)
        {
            Say($"couldn't save the workspace, {e.Message}", error: true);
        }
    }

    private void CloseWorkspace()
    {
        workspace = new HexWorkspace();
        browsing = false;
        treeDirty = true;
        codeEditing.ForgetVocabulary();
        KeepWorkspace();
        Say("the workspace is closed, no folders");
    }

    private void OpenWorkspacePressed()
    {
        if (options.SelfTest)
            return;

        Ask(HexDialogs.OpenWorkspace(browseDirectory, DialogHost), OpenWorkspaceFile);
    }

    private void SaveWorkspacePressed(bool asNew)
    {
        if (!asNew && workspace.FilePath is { } file)
        {
            SaveWorkspaceAs(file);
            return;
        }

        if (options.SelfTest)
            return;

        string start = workspace.Folders.Count > 0 ? workspace.Folders[0] : browseDirectory;
        Ask(HexDialogs.SaveWorkspace(start, workspace.Name + HexWorkspace.EXTENSION, DialogHost), SaveWorkspaceAs);
    }

    /// <summary>The left back to what is in the document on screen, its components or its outline.</summary>
    /// <summary>Ctrl+Shift+O, the outline of a file of code, or the toolbox, components and layers of a layout.</summary>
    private void ShowOutline()
    {
        left.Show(true);
        Side = HomeSide;
    }

    /// <summary>Ctrl+Shift+E, the explorer, whatever is open.</summary>
    private void ShowExplorer()
    {
        left.Show(true);
        Side = HexSide.Explorer;
    }

    /// <summary>Opens up every folder of the workspace a file is inside of, so the explorer shows it.</summary>
    private void OpenUpTo(string path)
    {
        string full = Path.GetFullPath(path);
        foreach (string root in workspace.Folders)
        {
            string top = Path.GetFullPath(root);
            if (!full.StartsWith(top, PlatformPaths.Comparison))
                continue;

            for (string? at = Path.GetDirectoryName(full); at is not null && at.Length >= top.Length; at = Path.GetDirectoryName(at))
                workspace.Expanded.Add(at);
        }

        treeDirty = true;
    }

    /// <summary>The File menu's folders and workspaces, and Go's back and forward through the places the caret was.</summary>
    private void AddWorkspaceCommands()
    {
        commands.Add("File/workspace", "Open Folder...", () => AskFolder(OpenFolder), icon: "folder-opened");
        commands.Add("File/workspace", "Add Folder to Workspace...", () => AskFolder(AddFolder), icon: "new-folder");
        commands.Add("File/workspace", "Open Workspace...", OpenWorkspacePressed, icon: "folder-library");
        commands.Add("File/workspace", "Save Workspace", () => SaveWorkspacePressed(asNew: false)).IsEnabled = () => !workspace.IsEmpty;
        commands.Add("File/workspace", "Save Workspace As...", () => SaveWorkspacePressed(asNew: true)).IsEnabled = () => !workspace.IsEmpty;
        commands.Add("File/workspace", "Close Workspace", CloseWorkspace).IsEnabled = () => !workspace.IsEmpty;

        commands.Add("Go/places", "Back", () => GoPlace(back: true), "Alt+Left", "arrow-left", UIKeyUse.BoundWhileTyping);
        commands.Add("Go/places", "Forward", () => GoPlace(back: false), "Alt+Right", "arrow-right", UIKeyUse.BoundWhileTyping);
    }
}
