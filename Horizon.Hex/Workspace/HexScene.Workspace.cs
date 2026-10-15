using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Hex;

// The workspace, the folders the editor has open (see HexWorkspace), shown as a tree in the explorer on the left with
// what can be done to their files, made, renamed, deleted (into the trash). Opened and saved as a file, and the one
// that was open last comes back when the editor starts.
internal sealed partial class HexScene
{
    // What a workspace that was never saved is kept in, next to the editor, so it is there the next time
    private const string UNTITLED_WORKSPACE = "hex_untitled.hex-workspace";

    private HexWorkspace workspace = new();

    // Whether the workspace of last time has been brought back yet, it isn't written over before then
    private bool workspaceRestored;

    // The rows of the explorer, for the self-test to find
    private readonly List<(string Path, bool IsFolder, UITreeItem Row)> explorerRows = [];

    // The most rows the explorer lists, a folder of thousands of files would have the list take seconds to make
    private const int MAX_EXPLORER_ROWS = 4000;

    private string? UntitledWorkspacePath => options.SelfTest || options.Check.Length > 0 ? null : Path.Combine(AppContext.BaseDirectory, UNTITLED_WORKSPACE);

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

    /// <summary>Asks for a folder, Explorer's picker of folders where there is one, opened where the explorer is, and a line to type it into otherwise.</summary>
    private void AskFolder(Action<string> picked)
    {
        if (OperatingSystem.IsWindows() && !options.SelfTest)
        {
            Ask(HexDialogs.PickFolder(browseDirectory, WindowHandle), picked);
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
        ForgetVocabulary();
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
        ForgetVocabulary();
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
        ForgetVocabulary();

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
        ForgetVocabulary();
        KeepWorkspace();
        Say("the workspace is closed, no folders");
    }

    private void OpenWorkspacePressed()
    {
        if (options.SelfTest)
            return;

        Ask(HexDialogs.OpenWorkspace(browseDirectory, WindowHandle), OpenWorkspaceFile);
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
        Ask(HexDialogs.SaveWorkspace(start, workspace.Name + HexWorkspace.EXTENSION, WindowHandle), SaveWorkspaceAs);
    }

    /// <summary>The left back to what is in the document on screen, its components or its outline.</summary>
    /// <summary>Ctrl+Shift+O, the outline of a file of code, or the toolbox, components and layers of a layout.</summary>
    private void ShowOutline()
    {
        left.Visible = true;
        Side = HomeSide;
    }

    /// <summary>Ctrl+Shift+E, the explorer, whatever is open.</summary>
    private void ShowExplorer()
    {
        left.Visible = true;
        Side = HexSide.Explorer;
    }

    /// <summary>Opens up every folder of the workspace a file is inside of, so the explorer shows it.</summary>
    private void OpenUpTo(string path)
    {
        string full = Path.GetFullPath(path);
        foreach (string root in workspace.Folders)
        {
            string top = Path.GetFullPath(root);
            if (!full.StartsWith(top, StringComparison.OrdinalIgnoreCase))
                continue;

            for (string? at = Path.GetDirectoryName(full); at is not null && at.Length >= top.Length; at = Path.GetDirectoryName(at))
                workspace.Expanded.Add(at);
        }

        treeDirty = true;
    }

    /* The explorer */
    // The files the editor opens, .hor first of all, and anything else that is text by the look of its name
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".hor", ".hex-workspace", ".txt", ".md", ".json", ".xml", ".tmx", ".tsx", ".tx", ".lua", ".py", ".cs", ".slang", ".hlsl",
        ".csproj", ".sln", ".props", ".targets", ".yml", ".yaml", ".ini", ".cfg", ".gitignore", ".editorconfig", ".fnt", ".csv"
    };

    private static bool IsEditable(string path) => TextExtensions.Contains(Path.GetExtension(path)) || Path.GetExtension(path).Length == 0;

    private void ExplorerPressed(ExplorerEntry entry)
    {
        if (entry.IsFolder)
        {
            if (!workspace.Expanded.Remove(entry.Path))
                workspace.Expanded.Add(entry.Path);

            treeDirty = true;
            KeepWorkspace();
            return;
        }

        if (!IsEditable(entry.Path))
        {
            Say($"{Path.GetFileName(entry.Path)} isn't text, there is nothing to edit in it here", error: true);
            return;
        }

        Open(entry.Path);
    }

    private void ShowExplorerMenu(ExplorerEntry entry)
    {
        var shown = new Menu("file");
        string folder = entry.IsFolder ? entry.Path : Path.GetDirectoryName(entry.Path)!;

        if (!entry.IsFolder)
            shown.Add("Open", () => Open(entry.Path));

        shown.Add("New file...", () => NewFileIn(folder));
        shown.Add("New folder...", () => NewFolderIn(folder));
        shown.AddSeparator();
        shown.Add("Rename...", () => RenamePath(entry.Path)).IsEnabled = () => !entry.IsRoot;
        shown.Add("Delete...", () => DeletePath(entry.Path)).IsEnabled = () => !entry.IsRoot;
        shown.AddSeparator();
        shown.Add("Copy the path", () =>
        {
            UIClipboard.Copy(entry.Path);
            Say($"{entry.Path} is on the clipboard");
        });

        if (entry.IsRoot)
        {
            shown.AddSeparator();
            shown.Add("Take out of the workspace", () => RemoveFolder(entry.Path));
        }

        contextMenu = ContextMenu.ShowAt(chrome, compositor.Pointer.Position, shown);
    }

    /* Files made, renamed and thrown away */

    // A name somebody typed for a file or a folder, nothing that climbs out of where it is made or names a drive
    private static bool IsPlainName(string name) =>
        name.Length > 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name != "." && name != "..";

    private void NewFileIn(string folder)
    {
        AskText("New file", $"A file in {folder}, .hor unless it says otherwise", "new.hor", "Make", name =>
        {
            name = name.Trim();
            if (!IsPlainName(name))
            {
                Say($"'{name}' can't be the name of a file", error: true);
                return;
            }

            if (Path.GetExtension(name).Length == 0)
                name += ".hor";

            MakeFile(Path.Combine(folder, name));
        });
    }

    /// <summary>Makes a file that isn't there yet, opens up the folder it is in, and opens it.</summary>
    private void MakeFile(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            Say($"there is a {Path.GetFileName(path)} there already", error: true);
            return;
        }

        try
        {
            File.WriteAllText(path, string.Empty);
        }
        catch (Exception e)
        {
            Say($"couldn't make {path}, {e.Message}", error: true);
            return;
        }

        workspace.Expanded.Add(Path.GetDirectoryName(Path.GetFullPath(path))!);
        treeDirty = true;
        Open(path);
        Say($"made {path}");
    }

    private void NewFolderIn(string folder)
    {
        AskText("New folder", $"A folder in {folder}", "new folder", "Make", name =>
        {
            name = name.Trim();
            string path = Path.Combine(folder, name);

            if (!IsPlainName(name) || Directory.Exists(path) || File.Exists(path))
            {
                Say($"'{name}' can't be made there", error: true);
                return;
            }

            Directory.CreateDirectory(path);
            workspace.Expanded.Add(Path.GetFullPath(folder));
            workspace.Expanded.Add(Path.GetFullPath(path));
            treeDirty = true;
            KeepWorkspace();
            Say($"made {path}");
        });
    }

    private void RenamePath(string path)
    {
        string old = Path.GetFileName(path.TrimEnd('\\', '/'));
        AskText("Rename", $"What {old} is called from now on", old, "Rename", name =>
        {
            name = name.Trim();
            if (!IsPlainName(name) || name == old)
                return;

            string to = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, name);
            if (File.Exists(to) || Directory.Exists(to))
            {
                Say($"there is a {name} there already", error: true);
                return;
            }

            try
            {
                if (Directory.Exists(path)) Directory.Move(path, to);
                else File.Move(path, to);
            }
            catch (Exception e)
            {
                Say($"couldn't rename {old}, {e.Message}", error: true);
                return;
            }

            // What is open in there goes by its new path, a layout or a file by its new name
            string from = Path.GetFullPath(path);
            foreach (var open in documents)
            {
                if (open.Path is not { } openPath)
                    continue;

                string full = Path.GetFullPath(openPath);
                if (string.Equals(full, from, StringComparison.OrdinalIgnoreCase))
                {
                    open.Path = to;
                    open.Name = Path.GetFileNameWithoutExtension(to);
                }
                else if (full.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    open.Path = to + full[from.Length..];
                }
            }

            if (workspace.Expanded.Remove(from))
                workspace.Expanded.Add(to);

            nameBox.Text = document.Name;
            tabsDirty = treeDirty = true;
            KeepWorkspace();
            Say($"{old} is {name} now");
        });
    }

    private void DeletePath(string path)
    {
        string name = Path.GetFileName(path.TrimEnd('\\', '/'));
        bool folder = Directory.Exists(path);

        UIDialog.Show(chrome, $"Delete {name}?",
            folder ? $"{name} and everything in it go in the trash, where they can be got back from." : $"{name} goes in the trash, where it can be got back from.",
            new DialogChoice("Delete", () => DeleteNow(path)),
            new DialogChoice("Cancel"));
    }

    /// <summary>Puts a file or a folder in the trash, and closes whatever was open of it.</summary>
    private void DeleteNow(string path)
    {
        string full = Path.GetFullPath(path);

        try
        {
            HexTrash.Discard(full);
        }
        catch (Exception e)
        {
            Say($"couldn't delete {Path.GetFileName(full)}, {e.Message}", error: true);
            return;
        }

        foreach (var open in documents.ToArray())
        {
            if (open.Path is not { } openPath)
                continue;

            string openFull = Path.GetFullPath(openPath);
            if (string.Equals(openFull, full, StringComparison.OrdinalIgnoreCase) || openFull.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                // Gone from the disk, what is in the editor is still there to be saved somewhere else, as a document without a file
                open.Path = null;
                open.Modified = true;
            }
        }

        tabsDirty = treeDirty = true;
        ForgetVocabulary();
        KeepWorkspace();
        Say($"{Path.GetFileName(full)} is in the trash");
    }
}
