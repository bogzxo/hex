using System.Text.RegularExpressions;

namespace Hex.Core.Projects;

/// <summary>
/// The solutions and projects of a folder, what a build of it builds and which of them is the program to run, Visual
/// Studio's startup project. Read off the files themselves, quickly, without asking the SDK, for a template and a guess.
/// </summary>
internal static partial class DotnetSolutions
{
    // A project line of a .sln, Project("{type}") = "Name", "Path\Name.csproj", "{guid}"
    [GeneratedRegex(@"^Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*""\s*,\s*""([^""]+\.\w*proj)""", RegexOptions.Multiline)]
    private static partial Regex SlnProject();

    /// <summary>The projects a solution has, made whole, the ones that are there.</summary>
    public static List<string> ProjectsIn(string solution)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(solution))!;
        var found = new List<string>();
        try
        {
            string text = File.ReadAllText(solution);
            if (solution.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                if (ProjectXml.Read(text) is { } root)
                    Slnx(root, found);
            }
            else
            {
                found.AddRange(SlnProject().Matches(text).Select(match => match.Groups[1].Value));
            }
        }
        catch (IOException)
        {
            return [];
        }

        return [.. found.Select(path => Path.GetFullPath(Path.Combine(folder, path.Replace('\\', Path.DirectorySeparatorChar))))
            .Where(File.Exists).Distinct(PlatformPaths.Comparer)];
    }

    // The Project elements of an .slnx, in its folders or not
    private static void Slnx(ProjectXmlElement element, List<string> into)
    {
        foreach (ProjectXmlElement child in element.Children)
        {
            if (child.Name == "Project" && child.Attribute("Path") is { } path)
                into.Add(path.Value);
            Slnx(child, into);
        }
    }

    /// <summary>
    /// What a folder builds, its solution (.slnx before .sln, the one named like the folder before others) or else its one
    /// project, null for neither. Projects a level or two down count when the folder itself has none.
    /// </summary>
    public static string? BuildTargetIn(string folder)
    {
        string name = Path.GetFileName(Path.GetFullPath(folder).TrimEnd('\\', '/'));
        foreach (string pattern in (string[])["*.slnx", "*.sln"])
        {
            string[] solutions = Files(folder, pattern);
            if (solutions.Length > 0)
                return solutions.FirstOrDefault(solution => Path.GetFileNameWithoutExtension(solution).Equals(name, StringComparison.OrdinalIgnoreCase)) ?? solutions[0];
        }

        string[] projects = Files(folder, "*.*proj").Where(DotnetProjects.IsSdkProject).ToArray();
        if (projects.Length > 0)
            return projects[0];

        return ProjectsUnder(folder, 2).FirstOrDefault();
    }

    /// <summary>Every project a solution or a project means, the project itself for a project.</summary>
    public static List<string> ProjectsOf(string target) =>
        target.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            ? ProjectsIn(target)
            : Directory.Exists(target) ? BuildTargetIn(target) is { } inside ? ProjectsOf(inside) : [] : [target];

    /// <summary>
    /// The project to run of some, a program that isn't a test, the one named like the solution or the folder before the
    /// rest. Null when none of them is a program.
    /// </summary>
    public static string? StartupProject(IReadOnlyList<string> projects, string? named = null)
    {
        string[] programs = [.. projects.Where(project => IsProgram(project) && !IsTests(project))];
        if (programs.Length == 0)
            return null;

        return programs.FirstOrDefault(project => named is not null && Path.GetFileNameWithoutExtension(project).Equals(named, StringComparison.OrdinalIgnoreCase))
            ?? programs[0];
    }

    /// <summary>Whether a project says it makes a program, OutputType Exe or WinExe, by its text.</summary>
    public static bool IsProgram(string project) => Says(project, "OutputType") is { } type
        && (type.Equals("Exe", StringComparison.OrdinalIgnoreCase) || type.Equals("WinExe", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a project is tests, it says so or it has the test SDK.</summary>
    public static bool IsTests(string project)
    {
        try
        {
            string text = File.ReadAllText(project);
            return Says(project, "IsTestProject")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true
                || text.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    // The last value a project's own text gives a property, in any PropertyGroup
    private static string? Says(string project, string property)
    {
        try
        {
            string text = File.ReadAllText(project);
            if (ProjectXml.Read(text) is not { } root)
                return null;

            return root.Children.Where(group => group.Name == "PropertyGroup").SelectMany(group => group.Children)
                .LastOrDefault(element => element.Name.Equals(property, StringComparison.OrdinalIgnoreCase)) is { } found
                ? ProjectXml.InnerText(text, found).Trim()
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string[] Files(string folder, string pattern)
    {
        try
        {
            return [.. Directory.EnumerateFiles(folder, pattern).Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The SDK style projects in the folders under one, so far down, not in bin, obj or a dot folder.</summary>
    public static IEnumerable<string> ProjectsUnder(string folder, int depth)
    {
        if (depth <= 0)
            yield break;

        string[] inside;
        try
        {
            inside = [.. Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string sub in inside)
        {
            string name = Path.GetFileName(sub);
            if (name is "bin" or "obj" or "node_modules" || name.StartsWith('.'))
                continue;

            foreach (string project in Files(sub, "*.*proj").Where(DotnetProjects.IsSdkProject))
                yield return project;
            foreach (string deeper in ProjectsUnder(sub, depth - 1))
                yield return deeper;
        }
    }
}
