using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The workspace, the folders the editor has open (see HexWorkspace), added and taken out, opened and saved as a file,
// and the one that was open last brought back when the editor starts. The tree of them on the left is Explorer.
internal sealed partial class HexExplorer
{
    // What a workspace that was never saved is kept in, in the settings folder next to the editor, so it is there the next time
    private const string UNTITLED_WORKSPACE = "hex_untitled.hex-workspace";

    internal HexWorkspace workspace = new();

    // Whether the workspace of last time has been brought back yet, it isn't written over before then
    internal bool workspaceRestored;

    // The rows of the explorer, for the self-test to find
    internal readonly List<(string Path, bool IsFolder, UITreeItem Row)> explorerRows = [];

    // The most rows the explorer lists, a folder of thousands of files would have the list take seconds to make
    internal const int MAX_EXPLORER_ROWS = 4000;

    internal string? UntitledWorkspacePath => options.SelfTest || options.Check.Length > 0 ? null : HexFiles.Kept(UNTITLED_WORKSPACE);

    /// <summary>
    /// Helper method to bring back the workspace that was open the last time, its folders and the files that were open in
    /// it, unless the editor is testing itself or was told what to open.
    /// </summary>
    internal void RestoreWorkspace()
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
            context.Notices.Say(context.Strings.Said("explorer_workspace.last_workspace_failed", ("message", e.Message)), error: true);
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
                    context.Documents.Open(file);
            }
        }
    }

    /// <summary>Helper method to keep the workspace in its file (or next to the editor while it has none), after any change to it.</summary>
    internal void KeepWorkspace()
    {
        if (!workspaceRestored)
            return;

        workspace.Terminal = context.Terminals.Host.Panel.Tabs.Count > 0 ? TerminalSessions.Value(context.Terminals.Host.Panel) : workspace.Terminal;

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
            context.Notices.Say(context.Strings.Said("explorer_workspace.keep_failed", ("message", e.Message)), error: true);
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
    internal void AskFolder(Action<string> picked)
    {
        if (!options.SelfTest)
        {
            // From the folder of the file that's open, where Open starts too. It started where files were last opened from
            string from = context.Documents.Current.Path is { } open && Path.GetDirectoryName(Path.GetFullPath(open)) is { } here && Directory.Exists(here)
                ? here : browseDirectory;
            context.Windows.Pick(HexDialogs.PickFolder(from, context.Windows.DialogHost), picked);
            return;
        }

        context.Windows.Prompt(context.Strings.Said("explorer_workspace.folder_title"), context.Strings.Said("explorer_workspace.folder_prompt"), browseDirectory, context.Strings.Said("explorer_workspace.add"), string.Empty, typed =>
        {
            if (Directory.Exists(typed.Trim()))
                picked(typed.Trim());
            else
                context.Notices.Say(context.Strings.Said("explorer_workspace.no_folder", ("folder", typed)), error: true);
        });
    }

    /// <summary>Adds a folder to the workspace, its files in the explorer from now on.</summary>
    internal void AddFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            context.Notices.Say(context.Strings.Said("explorer_workspace.no_folder", ("folder", folder)), error: true);
            return;
        }

        if (!workspace.Add(folder))
        {
            context.Notices.Say(context.Strings.Said("explorer_workspace.already_in", ("folder", folder)));
            return;
        }

        browseDirectory = Path.GetFullPath(folder);
        browsing = true;
        window.ClearResults();
        treeDirty = true;
        FoldersChanged?.Invoke();
        KeepWorkspace();
        context.Notices.Say(context.Strings.Said("explorer_workspace.added", ("folder", folder), ("count", HexText.Count("folder", workspace.Folders.Count))));
    }

    /// <summary>A workspace of one folder, the one that was open going away (its file stays where it is).</summary>
    internal void OpenFolder(string folder)
    {
        workspace = new HexWorkspace();
        AddFolder(folder);
    }

    internal void RemoveFolder(string folder)
    {
        if (!workspace.Remove(folder))
            return;

        treeDirty = true;
        FoldersChanged?.Invoke();
        KeepWorkspace();
        context.Notices.Say(context.Strings.Said("explorer_workspace.removed", ("name", Path.GetFileName(folder))));
    }

    internal void OpenWorkspaceFile(string path)
    {
        try
        {
            workspace = HexWorkspace.Load(path);
        }
        catch (Exception e)
        {
            context.Notices.Say(context.Strings.Said("explorer_workspace.not_a_workspace", ("name", Path.GetFileName(path)), ("message", e.Message)), error: true);
            return;
        }

        browsing = true;
        window.ClearResults();
        treeDirty = true;
        FoldersChanged?.Invoke();

        foreach (string file in workspace.OpenFiles.ToArray())
        {
            if (File.Exists(file))
                context.Documents.Open(file);
        }

        KeepWorkspace();
        context.Notices.Say(context.Strings.Said("explorer_workspace.opened", ("name", workspace.Name), ("count", HexText.Count("folder", workspace.Folders.Count))));
    }

    internal void SaveWorkspaceAs(string path)
    {
        if (!path.EndsWith(HexWorkspace.EXTENSION, StringComparison.OrdinalIgnoreCase))
            path += HexWorkspace.EXTENSION;

        try
        {
            KeepWorkspace();
            workspace.Save(path);
            KeepWorkspace();
            context.Notices.Say(context.Strings.Said("explorer_workspace.saved_as", ("path", path)));
        }
        catch (Exception e)
        {
            context.Notices.Say(context.Strings.Said("explorer_workspace.save_failed", ("message", e.Message)), error: true);
        }
    }

    internal void CloseWorkspace()
    {
        workspace = new HexWorkspace();
        browsing = false;
        treeDirty = true;
        FoldersChanged?.Invoke();
        KeepWorkspace();
        context.Notices.Say(context.Strings.Said("explorer_workspace.closed"));
    }

    internal void OpenWorkspacePressed()
    {
        if (options.SelfTest)
            return;

        context.Windows.Pick(HexDialogs.OpenWorkspace(browseDirectory, context.Windows.DialogHost), OpenWorkspaceFile);
    }

    internal void SaveWorkspacePressed(bool asNew)
    {
        if (!asNew && workspace.FilePath is { } file)
        {
            SaveWorkspaceAs(file);
            return;
        }

        if (options.SelfTest)
            return;

        string start = workspace.Folders.Count > 0 ? workspace.Folders[0] : browseDirectory;
        context.Windows.Pick(HexDialogs.SaveWorkspace(start, workspace.Name + HexWorkspace.EXTENSION, context.Windows.DialogHost), SaveWorkspaceAs);
    }

    /// <summary>The left back to what is in the document on screen, its components or its outline.</summary>
    /// <summary>Ctrl+Shift+O, the outline of a file of code, or the toolbox, components and layers of a layout.</summary>
    internal void ShowOutline()
    {
        context.Panels.ShowSide(window.HomeSide);
    }

    /// <summary>Ctrl+Shift+E, the explorer, whatever is open.</summary>
    internal void ShowExplorer()
    {
        context.Panels.ShowSide(HexSide.Explorer);
    }

    /// <summary>Opens up every folder of the workspace a file is inside of, so the explorer shows it.</summary>
    internal void OpenUpTo(string path)
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
    internal void AddWorkspaceCommands()
    {
        commands.Add("File/workspace", context.Strings.Said("welcome.open_folder"), () => AskFolder(OpenFolder), icon: "folder-opened");
        commands.Add("File/workspace", context.Strings.Said("explorer_workspace.add_folder"), () => AskFolder(AddFolder), icon: "new-folder");
        commands.Add("File/workspace", context.Strings.Said("explorer_workspace.open_workspace"), OpenWorkspacePressed, icon: "folder-library");
        commands.Add("File/workspace", context.Strings.Said("explorer_workspace.save_workspace"), () => SaveWorkspacePressed(asNew: false)).IsEnabled = () => !workspace.IsEmpty;
        commands.Add("File/workspace", context.Strings.Said("explorer_workspace.save_workspace_as"), () => SaveWorkspacePressed(asNew: true)).IsEnabled = () => !workspace.IsEmpty;
        commands.Add("File/workspace", context.Strings.Said("explorer_workspace.close_workspace"), CloseWorkspace).IsEnabled = () => !workspace.IsEmpty;
    }
}
