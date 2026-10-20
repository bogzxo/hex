
using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Hex.Core;

/// <summary>
/// The folders the editor has open, VS Code style. Any number of them, from anywhere, their files in the explorer and
/// in reach of opening by name, finding across and checking. Which of their folders are opened up in the explorer and
/// which files were open go with them.
/// <para>
/// A workspace can be saved to a file (.hex-workspace, HIDL like everything else, paths next to it written relative to
/// it) and opened again, which is a project of somebody's. One that has never been saved is kept in the editor's
/// settings, so it is there again the next time the editor starts.
/// </para>
/// </summary>
internal sealed class HexWorkspace
{
    public const string EXTENSION = ".hex-workspace";

    /// <summary>The file it is saved in, null for one that never was.</summary>
    public string? FilePath { get; set; }

    /// <summary>The folders, full paths, in the order they were added.</summary>
    public List<string> Folders { get; } = [];

    /// <summary>The folders that are opened up in the explorer, full paths.</summary>
    public HashSet<string> Expanded { get; } = new(PlatformPaths.Comparer);

    /// <summary>The files that were open, full paths, the one on screen last.</summary>
    public List<string> OpenFiles { get; } = [];

    /// <summary>What it is called, after its file, or its one folder, or nothing much.</summary>
    public string Name => FilePath is { } file ? Path.GetFileNameWithoutExtension(file)
        : Folders.Count == 1 ? Path.GetFileName(Folders[0].TrimEnd('\\', '/'))
        : Folders.Count == 0 ? "no folders" : "untitled";

    public bool IsEmpty => Folders.Count == 0;

    /// <summary>The tabs of the terminal page as they were, the object TerminalSessions writes, null for none kept.</summary>
    public ObjectValue? Terminal { get; set; }

    /// <summary>Adds a folder, unless it is in already. False for one that is.</summary>
    public bool Add(string folder)
    {
        string full = Path.GetFullPath(folder).TrimEnd('\\', '/');
        if (Folders.Any(known => string.Equals(known, full, PlatformPaths.Comparison)))
            return false;

        Folders.Add(full);
        Expanded.Add(full);
        return true;
    }

    public bool Remove(string folder) =>
        Folders.RemoveAll(known => string.Equals(known, Path.GetFullPath(folder).TrimEnd('\\', '/'), PlatformPaths.Comparison)) > 0;

    /// <summary>The folder of the workspace a file is in, null if it is in none of them.</summary>
    public string? FolderOf(string path)
    {
        string full = Path.GetFullPath(path);
        return Folders.Where(folder => full.StartsWith(folder + Path.DirectorySeparatorChar, PlatformPaths.Comparison) || string.Equals(full, folder, PlatformPaths.Comparison))
            .OrderByDescending(folder => folder.Length)
            .FirstOrDefault();
    }

    /* Saved and read again */

    /// <summary>Reads a workspace out of its file. Throws with what is wrong with it.</summary>
    public static HexWorkspace Load(string path)
    {
        string full = Path.GetFullPath(path);
        var workspace = new HexWorkspace { FilePath = full };
        workspace.Read(File.ReadAllText(full), Path.GetDirectoryName(full)!);
        return workspace;
    }

    /// <summary>Reads what a workspace says into this one, its paths relative to a folder.</summary>
    public void Read(string code, string relativeTo)
    {
        var runtime = new HIDLRuntime { Output = null };
        var (success, message) = runtime.Evaluate(code);
        if (!success)
            throw new Exception(message);

        if (runtime.UserScope.Lookup("workspace") is not ObjectValue { Properties: { } values })
            throw new Exception("a workspace file has to declare an object called workspace");

        Folders.Clear();
        Expanded.Clear();
        OpenFiles.Clear();

        foreach (string folder in Paths(values, "folders", relativeTo))
        {
            string trimmed = folder.TrimEnd('\\', '/');
            if (!Folders.Contains(trimmed, PlatformPaths.Comparer))
                Folders.Add(trimmed);
        }

        foreach (string expanded in Paths(values, "expanded", relativeTo))
            Expanded.Add(expanded.TrimEnd('\\', '/'));

        OpenFiles.AddRange(Paths(values, "open", relativeTo));
        Terminal = values.TryGetValue("terminal", out var terminal) && terminal is ObjectValue session ? session : null;
    }

    private static IEnumerable<string> Paths(Dictionary<string, IRuntimeValue> values, string key, string relativeTo)
    {
        if (!values.TryGetValue(key, out var value) || value is not ListValue list)
            yield break;

        foreach (var item in list.Items)
        {
            if (item is StringValue text && text.Value.Length > 0)
                yield return Path.GetFullPath(Path.IsPathRooted(text.Value) ? text.Value : Path.Combine(relativeTo, text.Value));
        }
    }

    /// <summary>Writes the workspace to a file, which it is then saved in from then on.</summary>
    public void Save(string path)
    {
        string full = Path.GetFullPath(path);
        File.WriteAllText(full, Write(Path.GetDirectoryName(full)!));
        FilePath = full;
    }

    /// <summary>The workspace as HIDL, the paths under a folder written relative to it and the rest as they are.</summary>
    public string Write(string? relativeTo)
    {
        var workspace = new HIDLObject()
            .Add("folders", Paths(Folders, relativeTo))
            .Add("expanded", Paths([.. Expanded.Order(StringComparer.OrdinalIgnoreCase)], relativeTo))
            .Add("open", Paths(OpenFiles, relativeTo));
        if (Terminal is { } session)
            workspace.Add("terminal", session);

        return new HIDLDocument()
            .Comment("A workspace of Hex.Dawn, the folders it has open. Open it with File, Open workspace.")
            .Let("workspace", workspace)
            .Write();
    }

    // paths as a list, the ones under a folder relative to it
    private static ListValue Paths(IReadOnlyList<string> paths, string? relativeTo)
    {
        var written = new List<IRuntimeValue>();
        foreach (string path in paths)
        {
            string shown = relativeTo is not null && IsUnder(path, relativeTo) ? Path.GetRelativePath(relativeTo, path) : path;
            written.Add(new StringValue(shown.Replace('\\', '/')));
        }

        return new ListValue(written);
    }

    // Whether a path can be written relative to a folder without climbing out of the drive it is on
    private static bool IsUnder(string path, string folder) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(path)), Path.GetPathRoot(Path.GetFullPath(folder)), PlatformPaths.Comparison);
}
