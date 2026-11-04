
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
    public string Name
    {
        get
        {
            if (FilePath is { } file)
                return Path.GetFileNameWithoutExtension(file);
            if (Folders.Count != 1)
                return Folders.Count == 0 ? "no folders" : "untitled";

            return WorkspacePaths.FolderName(Folders[0]);
        }
    }

    public bool IsEmpty => Folders.Count == 0;

    /// <summary>The tabs of the terminal page as they were, the object TerminalSessions writes, null for none kept.</summary>
    public ObjectValue? Terminal { get; set; }

    /// <summary>What F5 and the Run button run, picked beside the button, null for the file on screen.</summary>
    public HexRunTarget? RunTarget { get; set; }

    /// <summary>Adds a folder, unless it is in already. False for one that is.</summary>
    public bool Add(string folder)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (Folders.Contains(full, PlatformPaths.Comparer))
            return false;

        Folders.Add(full);
        Expanded.Add(full);
        return true;
    }

    public bool Remove(string folder)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return Folders.RemoveAll(known => PlatformPaths.Comparer.Equals(known, full)) > 0;
    }

    /// <summary>The folder of the workspace a file is in, null if it is in none of them.</summary>
    public string? FolderOf(string path)
    {
        string full = Path.GetFullPath(path);
        return Folders.Where(folder => PlatformPaths.IsUnder(full, folder))
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

        Folders.AddRange(Paths(values, "folders").Select(path => WorkspacePaths.ReadFolder(path, relativeTo)).Distinct(PlatformPaths.Comparer));
        Expanded.UnionWith(Paths(values, "expanded").Select(path => WorkspacePaths.ReadFolder(path, relativeTo)));
        OpenFiles.AddRange(Paths(values, "open").Select(path => WorkspacePaths.Read(path, relativeTo)));
        Terminal = values.TryGetValue("terminal", out var terminal) && terminal is ObjectValue session ? session : null;
        RunTarget = HexRunTarget.From(values.GetValueOrDefault("run_target"), relativeTo);
    }

    private static IEnumerable<string> Paths(Dictionary<string, IRuntimeValue> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || value is not ListValue list)
            yield break;

        foreach (var item in list.Items)
        {
            if (item is StringValue text && text.Value.Length > 0)
                yield return text.Value;
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
        if (RunTarget is { } target)
            workspace.Add("run_target", target.Said(relativeTo));
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
            written.Add(new StringValue(WorkspacePaths.Write(path, relativeTo)));

        return new ListValue(written);
    }
}
