using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The explorer, the folders of the workspace as a tree on the left, and what can be done to their files from it. Made,
// renamed and deleted (into the trash, see HexTrash).
internal sealed partial class HexScene
{
    /* The explorer */
    // The files the editor opens, .hor first of all, anything else that is text by the look of its name, and whatever a
    // grammar colours (IsEditable asks the languages too)
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".hor", ".hex-workspace", ".txt", ".md", ".json", ".xml", ".tmx", ".tsx", ".tx", ".lua", ".py", ".cs", ".slang", ".hlsl",
        ".csproj", ".sln", ".props", ".targets", ".yml", ".yaml", ".ini", ".cfg", ".gitignore", ".editorconfig", ".fnt", ".csv"
    };

    private static bool IsEditable(string path) => TextExtensions.Contains(Path.GetExtension(path)) || Path.GetExtension(path).Length == 0
        || Horizon.UI.Scripting.CodeLanguages.ForFile(path) is not null;

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
        shown.Add("Reveal in File Explorer", () => RevealInExplorer(entry.Path));
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

    /// <summary>A file or a folder shown in the machine's own file explorer, picked there.</summary>
    private void RevealInExplorer(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", ["-R", path]);
            else if (!ShowInFileManager(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Say($"{Path.GetFileName(path)} couldn't be shown, {e.Message}", error: true);
        }
    }

    /// <summary>
    /// On Linux, a file picked in the desktop's file manager (Files, Dolphin, Thunar and the rest answer
    /// org.freedesktop.FileManager1 on the session's bus), false when nothing answered and the folder should be opened instead.
    /// </summary>
    private static bool ShowInFileManager(string path)
    {
        try
        {
            using var asked = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dbus-send")
            {
                ArgumentList =
                {
                    "--session", "--print-reply", "--dest=org.freedesktop.FileManager1", "--type=method_call",
                    "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems",
                    $"array:string:{new Uri(Path.GetFullPath(path)).AbsoluteUri}", "string:"
                },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            return asked is not null && asked.WaitForExit(3000) && asked.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

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
                if (string.Equals(full, from, PlatformPaths.Comparison))
                {
                    open.Path = to;
                    open.Name = Path.GetFileNameWithoutExtension(to);
                }
                else if (full.StartsWith(from + Path.DirectorySeparatorChar, PlatformPaths.Comparison))
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

        // A language server running in it (its folder its process's) would keep Windows from moving it
        if (Directory.Exists(full))
        {
            foreach (HexDocument of in linters.Where(pair => PlatformPaths.IsUnder(pair.Value.RootDirectory, full)).Select(pair => pair.Key).ToList())
                StopLinting(of);
            Horizon.UI.Scripting.LanguageServers.StopUnder(full).Wait(TimeSpan.FromSeconds(6));
        }

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
            if (string.Equals(openFull, full, PlatformPaths.Comparison) || openFull.StartsWith(full + Path.DirectorySeparatorChar, PlatformPaths.Comparison))
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
