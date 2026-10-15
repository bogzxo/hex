using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Hex;

// The explorer, the folders of the workspace as a tree on the left, and what can be done to their files from it. Made,
// renamed and deleted (into the trash, see HexTrash).
internal sealed partial class HexScene
{
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
