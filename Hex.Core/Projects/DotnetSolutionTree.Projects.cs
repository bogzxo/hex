namespace Hex.Core.Projects;

// A project of a solution read off its own text, its ProjectReference and PackageReference items in any ItemGroup and
// its OutputType. A package without a version of its own takes the one the Directory.Packages.props over it gives,
// central package management's way. Nothing is evaluated, a reference a condition leaves out still shows, that's fine
// for a tree of what a project depends on.
internal sealed partial class DotnetSolutionTree
{
    private const string CENTRAL_PACKAGES = "Directory.Packages.props";

    /// <summary>A project read off its file, what its Directory.Packages.props says kept in <paramref name="props"/> for the next.</summary>
    internal static SolutionProject ReadProject(string project, Dictionary<string, Dictionary<string, string>> props)
    {
        string stamp = StampOf(project);
        string folder = System.IO.Path.GetDirectoryName(project)!;
        string text;
        try
        {
            text = File.ReadAllText(project);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new SolutionProject { Path = project, Stamp = stamp };
        }

        if (ProjectXml.Read(text) is not { } root)
            return new SolutionProject { Path = project, Stamp = stamp };

        var references = new List<string>();
        var packages = new List<SolutionPackage>();
        string? outputType = null;
        Dictionary<string, string>? central = null;
        foreach (ProjectXmlElement group in root.Children)
        {
            if (group.Name == "PropertyGroup")
            {
                if (group.Children.LastOrDefault(element => element.Name.Equals("OutputType", StringComparison.OrdinalIgnoreCase)) is { } type)
                    outputType = ProjectXml.InnerText(text, type).Trim();
                continue;
            }

            if (group.Name != "ItemGroup")
                continue;

            foreach (ProjectXmlElement item in group.Children)
            {
                if (item.Attribute("Include") is not { Value.Length: > 0 } include)
                    continue;

                if (item.Name == "ProjectReference")
                {
                    // An unevaluated MSBuild expression is not a filesystem path. The
                    // dependencies view resolves it with the SDK before exposing actions.
                    if (include.Value.Contains("$(", StringComparison.Ordinal))
                        continue;
                    string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, include.Value.Replace('\\', System.IO.Path.DirectorySeparatorChar)));
                    if (!references.Contains(path, PlatformPaths.Comparer))
                        references.Add(path);
                }
                else if (item.Name == "PackageReference")
                {
                    string version = item.Attribute("Version")?.Value
                        ?? (item.Child("Version") is { } said ? ProjectXml.InnerText(text, said).Trim() : null)
                        ?? (central ??= CentralVersions(folder, props)).GetValueOrDefault(include.Value)
                        ?? string.Empty;
                    packages.Add(new SolutionPackage(include.Value, version));
                }
            }
        }

        return new SolutionProject
        {
            Path = project,
            Stamp = stamp,
            References = references,
            Packages = [.. packages.OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)],
            IsProgram = outputType is { } output && (output.Equals("Exe", StringComparison.OrdinalIgnoreCase) || output.Equals("WinExe", StringComparison.OrdinalIgnoreCase))
        };
    }

    // The versions the nearest Directory.Packages.props over a folder gives packages, by name, none without one
    private static Dictionary<string, string> CentralVersions(string folder, Dictionary<string, Dictionary<string, string>> props)
    {
        if (Nearest(folder, CENTRAL_PACKAGES) is not { } file)
            return [];
        if (props.TryGetValue(file, out Dictionary<string, string>? known))
            return known;

        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string text = File.ReadAllText(file);
            if (ProjectXml.Read(text) is { } root)
            {
                foreach (ProjectXmlElement item in root.Children.Where(group => group.Name == "ItemGroup").SelectMany(group => group.Children))
                {
                    if (item.Name == "PackageVersion" && item.Attribute("Include") is { } name && item.Attribute("Version") is { } version)
                        versions[name.Value] = version.Value;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Without it the packages say no version, the tree still shows them
        }

        return props[file] = versions;
    }

    // The nearest file of a name in a folder or any over it
    private static string? Nearest(string folder, string name)
    {
        for (string? at = folder; at is not null; at = System.IO.Path.GetDirectoryName(at))
        {
            string file = System.IO.Path.Combine(at, name);
            if (File.Exists(file))
                return file;
        }

        return null;
    }

    /// <summary>When a project's file and the Directory.Packages.props over it were last written, what says it's still the same.</summary>
    internal static string StampOf(string project)
    {
        return DotnetProjects.StampOf(project);
    }

    /// <summary>Whether a changed file is one a solution's tree is read from, a solution, a project or a Directory.Packages.props.</summary>
    public static bool Matters(string path) =>
        IsSolution(path) || path.EndsWith("proj", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".props", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The one solution at the top of some folders, a .sln and an .slnx of the same name being the same one (the .slnx
    /// taken). Null for none or more than one.
    /// </summary>
    public static string? OnlySolutionIn(IEnumerable<string> folders)
    {
        var found = SolutionsIn(folders);
        return found.Count == 1 ? found[0] : null;
    }

    /// <summary>The solutions at the top of some folders, an .slnx standing for the .sln of its name.</summary>
    public static List<string> SolutionsIn(IEnumerable<string> folders)
    {
        var found = new List<string>();
        foreach (string folder in folders)
        {
            string[] here;
            try
            {
                here = [.. Directory.EnumerateFiles(folder, "*.sln*").Where(IsSolution).Order(StringComparer.OrdinalIgnoreCase)];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string solution in here)
            {
                if (solution.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) && here.Contains(solution + "x", PlatformPaths.Comparer))
                    continue;
                found.Add(System.IO.Path.GetFullPath(solution));
            }
        }

        return found;
    }

    /// <summary>Whether a file is a solution, a .sln or an .slnx.</summary>
    public static bool IsSolution(string path) =>
        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
}
