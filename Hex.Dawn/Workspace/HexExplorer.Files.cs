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
    // The files the editor opens, .hor first of all, anything else that is text by the look of its name, and whatever a
    // grammar colours (IsEditable asks the languages too)
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".hor", ".hex-workspace", ".txt", ".md", ".json", ".xml", ".tmx", ".tsx", ".tx", ".lua", ".py", ".cs", ".slang", ".hlsl",
        ".csproj", ".sln", ".props", ".targets", ".yml", ".yaml", ".ini", ".cfg", ".gitignore", ".editorconfig", ".fnt", ".csv"
    };

    internal static bool IsEditable(string path) => TextExtensions.Contains(Path.GetExtension(path)) || Path.GetExtension(path).Length == 0
        || Horizon.UI.Scripting.CodeLanguages.ForFile(path) is not null;

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

        if (!IsEditable(entry.Path))
        {
            context.Notices.Say($"{Path.GetFileName(entry.Path)} isn't text, there is nothing to edit in it here", error: true);
            return;
        }

        context.Documents.Open(entry.Path);
    }

    internal void ShowExplorerMenu(ExplorerEntry entry)
    {
        var shown = new Menu("file");
        string folder = entry.IsFolder ? entry.Path : Path.GetDirectoryName(entry.Path)!;

        if (!entry.IsFolder)
            shown.Add("Open", () => context.Documents.Open(entry.Path));

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
            context.Notices.Say($"{entry.Path} is on the clipboard");
        });

        if (entry.IsRoot)
        {
            shown.AddSeparator();
            shown.Add("Take out of the workspace", () => RemoveFolder(entry.Path));
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
            context.Notices.Say($"{Path.GetFileName(path)} couldn't be shown, {e.Message}", error: true);
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
        context.Windows.Prompt("New file", $"A file in {folder}, .hor unless it says otherwise", "new.hor", "Make", string.Empty, name =>
        {
            name = name.Trim();
            if (!IsPlainName(name))
            {
                context.Notices.Say($"'{name}' can't be the name of a file", error: true);
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
            context.Notices.Say($"there is a {Path.GetFileName(path)} there already", error: true);
            return;
        }

        try
        {
            File.WriteAllText(path, string.Empty);
        }
        catch (Exception e)
        {
            context.Notices.Say($"couldn't make {path}, {e.Message}", error: true);
            return;
        }

        workspace.Expanded.Add(Path.GetDirectoryName(Path.GetFullPath(path))!);
        treeDirty = true;
        context.Documents.Open(path);
        context.Notices.Say($"made {path}");
    }

    private void NewFolderIn(string folder)
    {
        context.Windows.Prompt("New folder", $"A folder in {folder}", "new folder", "Make", string.Empty, name =>
        {
            name = name.Trim();
            string path = Path.Combine(folder, name);

            if (!IsPlainName(name) || Directory.Exists(path) || File.Exists(path))
            {
                context.Notices.Say($"'{name}' can't be made there", error: true);
                return;
            }

            Directory.CreateDirectory(path);
            workspace.Expanded.Add(Path.GetFullPath(folder));
            workspace.Expanded.Add(Path.GetFullPath(path));
            treeDirty = true;
            KeepWorkspace();
            context.Notices.Say($"made {path}");
        });
    }

    private void RenamePath(string path)
    {
        string old = Path.GetFileName(path.TrimEnd('\\', '/'));
        context.Windows.Prompt("Rename", $"What {old} is called from now on", old, "Rename", string.Empty, name =>
        {
            name = name.Trim();
            if (!IsPlainName(name) || name == old)
                return;

            string to = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, name);
            if (File.Exists(to) || Directory.Exists(to))
            {
                context.Notices.Say($"there is a {name} there already", error: true);
                return;
            }

            try
            {
                if (Directory.Exists(path)) Directory.Move(path, to);
                else File.Move(path, to);
            }
            catch (Exception e)
            {
                context.Notices.Say($"couldn't rename {old}, {e.Message}", error: true);
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
            context.Notices.Say($"{old} is {name} now");
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

        // Whatever runs in it (a language server, its folder its process's) would keep Windows from moving it
        if (Directory.Exists(full))
            Deleting?.Invoke(full);

        try
        {
            HexTrash.Discard(full);
        }
        catch (Exception e)
        {
            context.Notices.Say($"couldn't delete {Path.GetFileName(full)}, {e.Message}", error: true);
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
        context.Notices.Say($"{Path.GetFileName(full)} is in the trash");
    }

    /// <summary>The icon of a file in the explorer and the lists, by what it is.</summary>
    internal static string FileIcon(string path)
    {
        string name = Path.GetFileName(path);
        string extension = Path.GetExtension(path).ToLowerInvariant();

        if (extension == HexWorkspace.EXTENSION)
            return "folder-library";
        if (name.Equals("skin.hor", StringComparison.OrdinalIgnoreCase))
            return "paintcan";
        if (extension == ".hor")
            return Path.GetFileName(Path.GetDirectoryName(path))?.Equals("localisation", StringComparison.OrdinalIgnoreCase) == true ? "globe" : "file-code";

        return extension switch
        {
            ".png" or ".jpg" or ".jpeg" or ".aseprite" or ".ase" or ".gif" or ".bmp" or ".svg" or ".ico" => "file-media",
            ".ttf" or ".otf" or ".fnt" => "symbol-text",
            ".md" or ".markdown" => "markdown",
            ".txt" or ".log" => "book",
            ".json" or ".tmx" or ".tsx" or ".xml" or ".yaml" or ".yml" or ".toml" => "json",
            ".slang" or ".hlsl" or ".glsl" => "symbol-color",
            ".cs" or ".csproj" or ".sln" or ".props" or ".targets" => "symbol-class",
            ".py" or ".js" or ".ts" or ".mjs" or ".lua" or ".rs" or ".c" or ".h" or ".cpp" or ".go" => "code",
            ".ps1" or ".bash" or ".sh" or ".cmd" or ".bat" => "terminal",
            ".zip" or ".7z" or ".gz" or ".tar" => "file-zip",
            ".pdf" => "file-pdf",
            ".exe" or ".dll" or ".pdb" or ".so" or ".dylib" or ".bin" => "file-binary",
            ".wav" or ".ogg" or ".mp3" or ".flac" => "unmute",
            _ => "file"
        };
    }

    /// <summary>
    /// The colour of a file's icon by what it is, the colours the skin writes code in, so a theme colours them too. Null
    /// for the text's own colour, a file of nothing in particular.
    /// </summary>
    internal System.Numerics.Vector4? FileIconColor(string path)
    {
        if (compositor.Skin?.Syntax is not { } syntax)
            return null;

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".hor" or ".hexspace" => syntax.Declaration,
            ".cs" or ".csproj" or ".sln" or ".props" or ".targets" => syntax.Keyword,
            ".json" or ".yaml" or ".yml" or ".toml" or ".tmx" or ".tsx" or ".xml" => syntax.Call,
            ".md" or ".markdown" or ".txt" => syntax.Property,
            ".png" or ".jpg" or ".jpeg" or ".aseprite" or ".ase" or ".gif" or ".bmp" or ".svg" or ".ico" => syntax.Text,
            ".slang" or ".hlsl" or ".glsl" => syntax.Number,
            ".py" or ".js" or ".ts" or ".mjs" or ".lua" or ".rs" or ".c" or ".h" or ".cpp" or ".go" => syntax.Type,
            ".ps1" or ".bash" or ".sh" or ".cmd" or ".bat" => syntax.Builtin,
            _ => null
        };
    }

    internal void PickFile(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            // New layouts are saved to wherever was looked at last
            browseDirectory = path;
            treeDirty = true;
            context.Notices.Say($"files of {browseDirectory}");
            return;
        }

        context.Documents.Open(path);
    }
}
