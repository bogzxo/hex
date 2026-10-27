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

    internal void ShowExplorerMenu(ExplorerEntry entry) => context.Windows.Menu(ExplorerMenu(entry));

    /// <summary>The menu of a row of the explorer, what can be done to the file or the folder it is.</summary>
    internal Menu ExplorerMenu(ExplorerEntry entry)
    {
        var shown = new Menu("file");
        string folder = entry.IsFolder ? entry.Path : Path.GetDirectoryName(entry.Path)!;

        if (!entry.IsFolder)
            shown.Add(context.Strings.Said("explorer_files.open"), () => context.Documents.Open(entry.Path));

        shown.Add(context.Strings.Said("explorer_files.new_file"), () => NewFileIn(folder));
        shown.Add(context.Strings.Said("explorer_files.new_folder"), () => NewFolderIn(folder));
        shown.AddSeparator();
        shown.Add(context.Strings.Said("explorer_files.open_in_terminal"), () => OpenTerminalIn?.Invoke(folder), icon: "terminal");
        shown.Add(context.Strings.Said("explorer_files.find_in_folder"), () => FindInFolder?.Invoke(folder, ScopeOf(folder)), icon: "search");
        shown.AddSeparator();
        shown.Add(context.Strings.Said("explorer_files.cut"), () => CutPaths([entry.Path])).IsEnabled = () => !entry.IsRoot;
        shown.Add(context.Strings.Said("explorer_files.copy"), () => CopyPaths([entry.Path]), icon: "copy").IsEnabled = () => !entry.IsRoot;
        shown.Add(context.Strings.Said("explorer_files.paste"), () => PasteInto(folder)).IsEnabled = () => CanPaste;
        shown.Add(context.Strings.Said("explorer_files.duplicate"), () => Duplicate(entry.Path)).IsEnabled = () => !entry.IsRoot;
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
        shown.Add(context.Strings.Said("explorer_files.copy_relative_path"), () =>
        {
            string relative = RelativeOf(entry.Path);
            UIClipboard.Copy(relative);
            context.Notices.Say(context.Strings.Said("explorer_files.path_copied", ("path", relative)));
        });

        if (entry.IsRoot)
        {
            shown.AddSeparator();
            shown.Add(context.Strings.Said("explorer_files.take_out"), () => RemoveFolder(entry.Path));
        }

        // Last, where Windows and Visual Studio have it
        if (PropertiesOf is { } properties)
        {
            shown.AddSeparator();
            shown.Add(context.Strings.Said("file_properties.menu"), () => properties(entry.Path), "Alt+Enter", "info");
        }

        return shown;
    }

    /// <summary>The Properties panel brought up for a row (and the rows selected with it), the scene's.</summary>
    internal Action<string>? PropertiesOf { get; set; }

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
                // Backslashes, explorer opens Documents instead of picking anything for a path written with forward ones
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{Path.GetFullPath(path)}\"");
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
        string extension = GuessExtension(folder);
        context.Windows.Prompt(context.Strings.Said("explorer_files.new_file_title"), context.Strings.Said("explorer_files.new_file_text", ("folder", folder), ("extension", extension)), "new" + extension, context.Strings.Said("explorer_files.make"), string.Empty, name =>
        {
            name = name.Trim();
            if (!IsPlainName(name))
            {
                context.Notices.Say(context.Strings.Said("explorer_files.bad_file_name", ("name", name)), error: true);
                return;
            }

            if (Path.GetExtension(name).Length == 0)
                name += extension;

            lastMadeExtension = Path.GetExtension(name).ToLowerInvariant();
            MakeFile(Path.Combine(folder, name));
        });
    }

    /// <summary>
    /// The extension a new file in a folder most likely wants, the one most of the files beside it have, else the one
    /// made last, else .txt. A .hor only in a folder that's mostly .hor files, Go people kept getting new.hor.
    /// </summary>
    internal string GuessExtension(string folder)
    {
        string? common = null;
        try
        {
            common = Directory.EnumerateFiles(folder, "*", new EnumerationOptions { IgnoreInaccessible = true })
                .Take(500)
                .Where(file => !IsLeftover(file) && !Path.GetFileName(file).StartsWith('.'))
                .Select(file => Path.GetExtension(file).ToLowerInvariant())
                .Where(extension => extension.Length > 1)
                .GroupBy(extension => extension)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.Key)
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A folder that can't be listed has nothing beside the new file to go by
        }

        if (common is not null)
            return common;
        return lastMadeExtension is { Length: > 1 } last && last != ".hor" ? last : ".txt";
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

    internal void NewFolderIn(string folder)
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
        context.Windows.Prompt(context.Strings.Said("explorer_files.rename"), context.Strings.Said("explorer_files.rename_text", ("old", old)), old, context.Strings.Said("explorer_files.rename"), string.Empty, name => RenameTo(path, name));
    }

    /// <summary>
    /// A file or a folder renamed to what was typed, a name, or a path down from its folder with / in it ("sub/x.go")
    /// which moves it there inside the workspace, the folders on the way made. Nothing climbs out with .. and nothing
    /// lands outside every folder of the workspace.
    /// </summary>
    /// <returns>Whether it moved.</returns>
    internal bool RenameTo(string path, string typed)
    {
        string old = Path.GetFileName(path.TrimEnd('\\', '/'));
        string name = typed.Trim().Replace('\\', '/').Trim('/');
        string[] parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || name == old || !parts.All(IsPlainName))
            return false;

        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string to = Path.GetFullPath(Path.Combine([folder, .. parts]));
        if (parts.Length > 1 && !workspace.Folders.Any(root => PlatformPaths.IsUnder(to, root)))
        {
            context.Notices.Say(context.Strings.Said("explorer_files.outside_workspace", ("name", name)), error: true);
            return false;
        }

        if (!MovePath(path, to))
            return false;

        context.Notices.Say(context.Strings.Said("explorer_files.renamed", ("old", old), ("name", name)));
        return true;
    }

    /// <summary>
    /// Moves a file or a folder to somewhere that isn't taken, the folders on the way made, and what is open of it follows
    /// it to its new path. Said when it can't be.
    /// </summary>
    /// <returns>Whether it moved.</returns>
    internal bool MovePath(string path, string to)
    {
        string from = Path.GetFullPath(path);
        to = Path.GetFullPath(to);
        string old = Path.GetFileName(from.TrimEnd('\\', '/'));
        if (File.Exists(to) || Directory.Exists(to))
        {
            context.Notices.Say(context.Strings.Said("explorer_files.already_there", ("name", Path.GetFileName(to))), error: true);
            return false;
        }

        // A folder into itself would be a folder that holds itself
        if (Directory.Exists(from) && PlatformPaths.IsUnder(to, from))
        {
            context.Notices.Say(context.Strings.Said("explorer_files.into_itself", ("name", old)), error: true);
            return false;
        }

        try
        {
            if (Path.GetDirectoryName(to) is { } into)
                Directory.CreateDirectory(into);
            if (Directory.Exists(from)) Directory.Move(from, to);
            else File.Move(from, to);
        }
        catch (Exception e)
        {
            context.Notices.Say(context.Strings.Said("explorer_files.not_renamed", ("old", old), ("why", e.Message)), error: true);
            return false;
        }

        Retarget(from, to);
        if (workspace.Expanded.Remove(from))
            workspace.Expanded.Add(to);
        if (Path.GetDirectoryName(to) is { } shownIn)
            workspace.Expanded.Add(shownIn);

        window.ShowName();
        tabsDirty = treeDirty = true;
        FoldersChanged?.Invoke();
        KeepWorkspace();
        return true;
    }

    /// <summary>What is open of a file or a folder that moved goes by its new path, a layout or a file by its new name.</summary>
    private void Retarget(string from, string to)
    {
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
    }

    /// <summary>Where a file is inside the workspace's folder it's in, with forward slashes, the whole path for one outside them all.</summary>
    internal string RelativeOf(string path)
    {
        string full = Path.GetFullPath(path);
        foreach (string root in workspace.Folders)
        {
            if (PlatformPaths.IsUnder(full, root) && !PlatformPaths.Same(full, root))
                return Path.GetRelativePath(Path.GetFullPath(root), full).Replace('\\', '/');
        }

        return full;
    }

    /// <summary>
    /// What Find in Files' include box says for a folder, the folder down from its workspace folder (with the folder's name
    /// in front when there are more than one, HexScope reads it that way), empty for a workspace folder itself.
    /// </summary>
    internal string ScopeOf(string folder)
    {
        string full = Path.GetFullPath(folder);
        foreach (string root in workspace.Folders)
        {
            if (!PlatformPaths.IsUnder(full, root))
                continue;
            if (PlatformPaths.Same(full, root))
                return string.Empty;

            string inside = Path.GetRelativePath(Path.GetFullPath(root), full).Replace('\\', '/');
            return workspace.Folders.Count > 1 ? $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))}/{inside}" : inside;
        }

        return string.Empty;
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
        // A backup has the icon of what it was a copy of, Foo.cs.orig a C# file's
        path = CodeLanguages.WithoutBackupSuffix(path);
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

        // A leftover (a merge's .orig, a .bak, a patch's .rej, a .tmp) keeps the icon of what it was but goes dim, it's
        // not a file anybody means to work on
        if (IsLeftover(path))
            return syntax.Comment;

        path = CodeLanguages.WithoutBackupSuffix(path);
        return Path.GetExtension(path).Equals(".hexspace", StringComparison.OrdinalIgnoreCase) ? syntax.Declaration : UIFileKinds.ColourOf(path, syntax);
    }

    /// <summary>Whether a file is what something left behind, a backup or a reject, by the end of its name.</summary>
    internal static bool IsLeftover(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".orig" or ".bak" or ".rej" or ".tmp"
        || !string.Equals(CodeLanguages.WithoutBackupSuffix(path), path, StringComparison.Ordinal);

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
