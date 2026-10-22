using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The explorer, the folders of the workspace as a tree on the left, and what can be done to their files from it. Made,
// renamed and deleted (into the trash, see HexTrash).
internal sealed partial class HexExplorer
{
    /* The explorer */

    internal void ExplorerPressed(ExplorerEntry entry)
    {
        if (entry.IsFolder)
        {
            if (!workspace.Expanded.Remove(entry.Path))
                workspace.Expanded.Add(entry.Path);

            treeDirty = true;
            KeepWorkspace();
            return;
        }

        // Any file opens, whatever its name says. One that isn't text asks first (DawnScene.Documents), a list of the
        // extensions that were text kept every file of a kind nobody had thought of out
        context.Documents.Open(entry.Path);
    }

    internal void ShowExplorerMenu(ExplorerEntry entry)
    {
        var shown = new Menu("file");
        string folder = entry.IsFolder ? entry.Path : Path.GetDirectoryName(entry.Path)!;

        if (!entry.IsFolder)
            shown.Add(context.Strings.Said("explorer_files.open"), () => context.Documents.Open(entry.Path));

        shown.Add(context.Strings.Said("explorer_files.new_file"), () => NewFileIn(folder));
        shown.Add(context.Strings.Said("explorer_files.new_folder"), () => NewFolderIn(folder));
        shown.AddSeparator();
        shown.Add(context.Strings.Said("explorer_files.rename_menu"), () => RenamePath(entry.Path)).IsEnabled = () => !entry.IsRoot;
        shown.Add(context.Strings.Said("explorer_files.reveal"), () => RevealInExplorer(entry.Path));
        shown.Add(context.Strings.Said("explorer_files.delete_menu"), () => DeletePath(entry.Path)).IsEnabled = () => !entry.IsRoot;
        shown.AddSeparator();
        shown.Add(context.Strings.Said("explorer_files.copy_path"), () =>
        {
            UIClipboard.Copy(entry.Path);
            context.Notices.Say(context.Strings.Said("explorer_files.path_copied", ("path", entry.Path)));
        });

        if (entry.IsRoot)
        {
            shown.AddSeparator();
            shown.Add(context.Strings.Said("explorer_files.take_out"), () => RemoveFolder(entry.Path));
        }

        context.Windows.Menu(shown);
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
                System.Diagnostics.Process.Start("exe", $"/select,\"{path}\"");
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", ["-R", path]);
            else if (!ShowInFileManager(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            context.Notices.Say(context.Strings.Said("explorer_files.not_shown", ("name", Path.GetFileName(path)), ("why", e.Message)), error: true);
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

    internal void NewFileIn(string folder)
    {
        context.Windows.Prompt(context.Strings.Said("explorer_files.new_file_title"), context.Strings.Said("explorer_files.new_file_text", ("folder", folder)), "new.hor", context.Strings.Said("explorer_files.make"), string.Empty, name =>
        {
            name = name.Trim();
            if (!IsPlainName(name))
            {
                context.Notices.Say(context.Strings.Said("explorer_files.bad_file_name", ("name", name)), error: true);
                return;
            }

            if (Path.GetExtension(name).Length == 0)
                name += ".hor";

            MakeFile(Path.Combine(folder, name));
        });
    }

    /// <summary>Makes a file that isn't there yet, opens up the folder it is in, and opens it.</summary>
    internal void MakeFile(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            context.Notices.Say(context.Strings.Said("explorer_files.already_there", ("name", Path.GetFileName(path))), error: true);
            return;
        }

        try
        {
            File.WriteAllText(path, string.Empty);
        }
        catch (Exception e)
        {
            context.Notices.Say(context.Strings.Said("explorer_files.not_made", ("path", path), ("why", e.Message)), error: true);
            return;
        }

        workspace.Expanded.Add(Path.GetDirectoryName(Path.GetFullPath(path))!);
        treeDirty = true;
        context.Documents.Open(path);
        context.Notices.Say(context.Strings.Said("explorer_files.made", ("path", path)));
    }

    private void NewFolderIn(string folder)
    {
        context.Windows.Prompt(context.Strings.Said("explorer_files.new_folder_title"), context.Strings.Said("explorer_files.new_folder_text", ("folder", folder)), "new folder", context.Strings.Said("explorer_files.make"), string.Empty, name =>
        {
            name = name.Trim();
            string path = Path.Combine(folder, name);

            if (!IsPlainName(name) || Directory.Exists(path) || File.Exists(path))
            {
                context.Notices.Say(context.Strings.Said("explorer_files.bad_folder_name", ("name", name)), error: true);
                return;
            }

            Directory.CreateDirectory(path);
            workspace.Expanded.Add(Path.GetFullPath(folder));
            workspace.Expanded.Add(Path.GetFullPath(path));
            treeDirty = true;
            KeepWorkspace();
            context.Notices.Say(context.Strings.Said("explorer_files.made", ("path", path)));
        });
    }

    private void RenamePath(string path)
    {
        string old = Path.GetFileName(path.TrimEnd('\\', '/'));
        context.Windows.Prompt(context.Strings.Said("explorer_files.rename"), context.Strings.Said("explorer_files.rename_text", ("old", old)), old, context.Strings.Said("explorer_files.rename"), string.Empty, name =>
        {
            name = name.Trim();
            if (!IsPlainName(name) || name == old)
                return;

            string to = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, name);
            if (File.Exists(to) || Directory.Exists(to))
            {
                context.Notices.Say(context.Strings.Said("explorer_files.already_there", ("name", name)), error: true);
                return;
            }

            try
            {
                if (Directory.Exists(path)) Directory.Move(path, to);
                else File.Move(path, to);
            }
            catch (Exception e)
            {
                context.Notices.Say(context.Strings.Said("explorer_files.not_renamed", ("old", old), ("why", e.Message)), error: true);
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

            window.ShowName();
            treeDirty = true;
            KeepWorkspace();
            context.Notices.Say(context.Strings.Said("explorer_files.renamed", ("old", old), ("name", name)));
        });
    }

    private void DeletePath(string path)
    {
        string name = Path.GetFileName(path.TrimEnd('\\', '/'));
        bool folder = Directory.Exists(path);

        UIDialog.Show(chrome, context.Strings.Said("explorer_files.delete_title", ("name", name)),
            folder ? context.Strings.Said("explorer_files.delete_folder_text", ("name", name)) : context.Strings.Said("explorer_files.delete_file_text", ("name", name)),
            new DialogChoice(context.Strings.Said("explorer_files.delete"), () => DeleteNow(path)),
            new DialogChoice(context.Strings.Said("keys.leave_it")));
    }

    /// <summary>Puts a file or a folder in the trash, and closes whatever was open of it.</summary>
    private void DeleteNow(string path)
    {
        string full = Path.GetFullPath(path);

        // Whatever runs in it (a language server, its folder its process's) would keep Windows from moving it
        if (Directory.Exists(full))
            Deleting?.Invoke(full);

        try
        {
            HexTrash.Discard(full);
        }
        catch (Exception e)
        {
            context.Notices.Say(context.Strings.Said("explorer_files.not_deleted", ("name", Path.GetFileName(full)), ("why", e.Message)), error: true);
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
        FoldersChanged?.Invoke();
        KeepWorkspace();
        context.Notices.Say(context.Strings.Said("explorer_files.in_trash", ("name", Path.GetFileName(full))));
    }

    /// <summary>
    /// The icon of a file in the explorer and the lists, by what it is. Hex's own files (a workspace, a skin, a language)
    /// have their own, anything else is what the engine's file dialog draws it with.
    /// </summary>
    internal static string FileIcon(string path)
    {
        string name = Path.GetFileName(path);
        string extension = Path.GetExtension(path).ToLowerInvariant();

        if (extension == HexWorkspace.EXTENSION)
            return "folder-library";
        if (name.Equals("skin.hor", StringComparison.OrdinalIgnoreCase))
            return "paintcan";
        if (extension == ".hor" && Path.GetFileName(Path.GetDirectoryName(path))?.Equals("localisation", StringComparison.OrdinalIgnoreCase) == true)
            return "globe";

        return UIFileKinds.IconOf(path);
    }

    /// <summary>
    /// The colour of a file's icon by what it is, the colours the skin writes code in, so a theme colours them too. Null
    /// for the text's own colour, a file of nothing in particular.
    /// </summary>
    internal System.Numerics.Vector4? FileIconColor(string path)
    {
        if (compositor.Skin?.Syntax is not { } syntax)
            return null;

        return Path.GetExtension(path).Equals(".hexspace", StringComparison.OrdinalIgnoreCase) ? syntax.Declaration : UIFileKinds.ColourOf(path, syntax);
    }

    internal void PickFile(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            // New layouts are saved to wherever was looked at last
            browseDirectory = path;
            treeDirty = true;
            context.Notices.Say(context.Strings.Said("explorer_files.files_of", ("folder", browseDirectory)));
            return;
        }

        context.Documents.Open(path);
    }
}
