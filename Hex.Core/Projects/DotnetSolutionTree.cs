using System.Text.RegularExpressions;

namespace Hex.Core.Projects;

/// <summary>A NuGet package a project takes, by its name and the version it asks for (empty when nothing says one).</summary>
internal sealed record SolutionPackage(string Name, string Version);

internal sealed record SolutionProjectFile(string Path, string DisplayPath, string ItemTypes, bool Linked);

/// <summary>
/// A project of a solution as the Solution view shows it, its name, the projects it references and the packages it takes,
/// read off its file. Kept until its file is written again.
/// </summary>
internal sealed class SolutionProject
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path)!;

    /// <summary>The projects it references, full paths, in the order its file has them.</summary>
    public IReadOnlyList<string> References { get; set; } = [];

    public IReadOnlyList<SolutionPackage> Packages { get; set; } = [];

    public IReadOnlyList<SolutionProjectFile> Files { get; set; } = [];

    public string? DependencyConfiguration { get; set; }
    public string? DependencyError { get; set; }
    public bool DependenciesLoading { get; set; }
    public int EvaluationGeneration { get; set; }

    /// <summary>Whether it makes a program, OutputType Exe or WinExe.</summary>
    public bool IsProgram { get; init; }

    /// <summary>When its file and the Directory.Packages.props over it were written, what says it has to be read again.</summary>
    internal string Stamp { get; init; } = string.Empty;
}

/// <summary>A solution folder, the ones Visual Studio groups projects in, and the loose files it holds (Solution Items).</summary>
internal sealed class SolutionFolder
{
    public string Name { get; init; } = string.Empty;
    public List<SolutionFolder> Folders { get; } = [];
    public List<SolutionProject> Projects { get; } = [];
    public List<string> Files { get; } = [];
}

/// <summary>
/// A .sln or .slnx read for the Solution view, its folders, its projects and what each of them depends on. Read off
/// the files with no SDK asked, off the editor's thread, and read again with what was read before so a project whose
/// file didn't change is the one that was read already.
/// </summary>
internal sealed partial class DotnetSolutionTree
{
    // A .sln's solution folder, the kind Visual Studio gives every one of them
    private const string FOLDER_KIND = "2150E333-8FDC-42A3-9474-1A3956D46DE8";

    // Project("{kind}") = "Name", "Path", "{guid}"
    [GeneratedRegex(@"^Project\(""\{([^}]+)\}""\)\s*=\s*""([^""]*)""\s*,\s*""([^""]*)""\s*,\s*""\{([^}]+)\}""", RegexOptions.Multiline)]
    private static partial Regex SlnEntry();

    // {child} = {parent} in NestedProjects
    [GeneratedRegex(@"^\s*\{([^}]+)\}\s*=\s*\{([^}]+)\}\s*$", RegexOptions.Multiline)]
    private static partial Regex SlnNested();

    // Debug|Any CPU = Debug|Any CPU in SolutionConfigurationPlatforms
    [GeneratedRegex(@"^\s*([^|=\r\n]+)\|[^=\r\n]*=", RegexOptions.Multiline)]
    private static partial Regex SlnConfiguration();

    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path)!;

    /// <summary>What's at the top of it, its folders and the projects in none.</summary>
    public SolutionFolder Root { get; } = new();

    /// <summary>The configurations it has, Debug and Release when it says none.</summary>
    public List<string> Configurations { get; } = [];

    /// <summary>Every project in it, folders or not.</summary>
    public IEnumerable<SolutionProject> Projects => All(Root);

    private static IEnumerable<SolutionProject> All(SolutionFolder folder) => folder.Projects.Concat(folder.Folders.SelectMany(All));

    /// <summary>
    /// A solution read, the projects of <paramref name="before"/> kept where their files haven't changed since. Null when
    /// the file can't be read.
    /// </summary>
    public static DotnetSolutionTree? Read(string solution, DotnetSolutionTree? before = null)
    {
        string full = System.IO.Path.GetFullPath(solution);
        string text;
        try
        {
            text = File.ReadAllText(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var tree = new DotnetSolutionTree { Path = full };
        var known = new Dictionary<string, SolutionProject>(PlatformPaths.Comparer);
        foreach (SolutionProject project in before?.Projects ?? [])
            known.TryAdd(project.Path, project);
        var props = new Dictionary<string, Dictionary<string, string>>(PlatformPaths.Comparer);
        SolutionProject Project(string path) => known.TryGetValue(path, out SolutionProject? kept) && kept.Stamp == StampOf(path) ? kept : ReadProject(path, props);

        if (full.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            tree.ReadSlnx(text, Project);
        else
            tree.ReadSln(text, Project);

        if (tree.Configurations.Count == 0)
            tree.Configurations.AddRange(["Debug", "Release"]);
        Sort(tree.Root);
        return tree;
    }

    private void ReadSln(string text, Func<string, SolutionProject> project)
    {
        var folders = new Dictionary<string, SolutionFolder>(StringComparer.OrdinalIgnoreCase);
        var projects = new Dictionary<string, SolutionProject>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (Match entry in SlnEntry().Matches(text))
        {
            string guid = entry.Groups[4].Value;
            if (entry.Groups[1].Value.Equals(FOLDER_KIND, StringComparison.OrdinalIgnoreCase))
            {
                var folder = new SolutionFolder { Name = entry.Groups[2].Value };
                folder.Files.AddRange(SolutionItems(text, entry.Index));
                folders[guid] = folder;
                order.Add(guid);
            }
            else if (Resolve(entry.Groups[3].Value) is { } path)
            {
                projects[guid] = project(path);
                order.Add(guid);
            }
        }

        var parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Section(text, "NestedProjects") is { } nested)
        {
            foreach (Match pair in SlnNested().Matches(nested))
                parents[pair.Groups[1].Value] = pair.Groups[2].Value;
        }

        foreach (string guid in order)
        {
            SolutionFolder into = parents.TryGetValue(guid, out string? parent) && folders.TryGetValue(parent, out SolutionFolder? over) ? over : Root;
            if (folders.TryGetValue(guid, out SolutionFolder? folder))
                into.Folders.Add(folder);
            else
                into.Projects.Add(projects[guid]);
        }

        if (Section(text, "SolutionConfigurationPlatforms") is { } configurations)
        {
            foreach (Match line in SlnConfiguration().Matches(configurations))
            {
                string name = line.Groups[1].Value.Trim();
                if (name.Length > 0 && !Configurations.Contains(name, StringComparer.OrdinalIgnoreCase))
                    Configurations.Add(name);
            }
        }
    }

    // The files of a solution folder's ProjectSection(SolutionItems), the lines between it and the EndProject after it
    private IEnumerable<string> SolutionItems(string text, int from)
    {
        int end = text.IndexOf("EndProject", from, StringComparison.Ordinal);
        int items = text.IndexOf("ProjectSection(SolutionItems)", from, StringComparison.Ordinal);
        if (end < 0 || items < 0 || items > end)
            yield break;

        int lineStart = text.IndexOf('\n', items) + 1;
        foreach (string line in text[lineStart..end].Split('\n'))
        {
            int equals = line.IndexOf('=');
            if (equals > 0 && !line.Contains("EndProjectSection", StringComparison.Ordinal) && Resolve(line[..equals].Trim()) is { } file)
                yield return file;
        }
    }

    // The text of a GlobalSection of a .sln, null when it has none of the name
    private static string? Section(string text, string name)
    {
        int start = text.IndexOf($"GlobalSection({name})", StringComparison.Ordinal);
        if (start < 0)
            return null;
        int end = text.IndexOf("EndGlobalSection", start, StringComparison.Ordinal);
        return end < 0 ? null : text[(text.IndexOf('\n', start) + 1)..end];
    }

    private void ReadSlnx(string text, Func<string, SolutionProject> project)
    {
        if (ProjectXml.Read(text) is not { } root)
            return;

        foreach (ProjectXmlElement element in root.Children)
        {
            switch (element.Name)
            {
                case "Project" when element.Attribute("Path") is { } path && Resolve(path.Value) is { } file:
                    Root.Projects.Add(project(file));
                    break;
                case "Folder" when element.Attribute("Name") is { } name:
                    SlnxFolder(FolderAt(name.Value), element, project);
                    break;
                case "Configurations":
                    Configurations.AddRange(element.Children.Where(child => child.Name == "BuildType" && child.Attribute("Name") is not null)
                        .Select(child => child.Attribute("Name")!.Value));
                    break;
            }
        }
    }

    // What an .slnx folder holds, its projects and its files
    private void SlnxFolder(SolutionFolder folder, ProjectXmlElement element, Func<string, SolutionProject> project)
    {
        foreach (ProjectXmlElement child in element.Children)
        {
            if (child.Attribute("Path") is not { } path || Resolve(path.Value) is not { } file)
                continue;

            if (child.Name == "Project")
                folder.Projects.Add(project(file));
            else if (child.Name == "File")
                folder.Files.Add(file);
        }
    }

    // An .slnx's folder by its name, "/src/tools/", made with the ones over it when it isn't there yet
    private SolutionFolder FolderAt(string name)
    {
        SolutionFolder at = Root;
        foreach (string part in name.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            SolutionFolder? next = at.Folders.Find(folder => folder.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (next is null)
                at.Folders.Add(next = new SolutionFolder { Name = part });
            at = next;
        }

        return at;
    }

    // A path the solution gives, made whole from its folder, null for one that isn't there
    private string? Resolve(string path)
    {
        if (path.Length == 0)
            return null;
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(Folder, path.Replace('\\', System.IO.Path.DirectorySeparatorChar)));
        return File.Exists(full) ? full : null;
    }

    // Folders first and then projects, each by name, the way Solution Explorer lists them
    private static void Sort(SolutionFolder folder)
    {
        folder.Folders.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        folder.Projects.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        foreach (SolutionFolder inside in folder.Folders)
            Sort(inside);
    }
}
