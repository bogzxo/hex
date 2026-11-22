using System.Xml.Linq;

namespace Hex.Core;

/// <summary>Build inputs understood by Link. Opaque shell gates retain all materialized inputs.</summary>
internal static class HexLinkWorktreeGateInputs
{
    private static readonly string[] BuildConfiguration =
        ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "Directory.Build.rsp",
         "global.json", "NuGet.Config", ".editorconfig", ".globalconfig", "MSBuild.rsp"];

    public static Func<string, bool> For(HexLinkFolder gate, IReadOnlyList<HexLinkWorktreeGateOptions> gates)
    {
        var roots = new HashSet<string>(PlatformPaths.Comparer);
        var configuration = new HashSet<string>(PlatformPaths.Comparer);
        var examined = new HashSet<string>(PlatformPaths.Comparer);
        var projectsSeen = new HashSet<string>(PlatformPaths.Comparer);
        foreach (var options in gates)
        {
            string? target = options.Target;
            if (options.WorkingFolder is not { Length: > 0 } directory || options.TestCommand is not null ||
                options.Build is { } command && !TryDotnetProject(command, out target))
                return _ => true;

            string working = HexLinkFiles.Resolve([gate], gate.Name + "/" + directory);
            if (!Directory.Exists(working))
                return _ => true;
            if (target is not null && !PlainProject(target))
                return _ => true;
            string[] projects = target is { } project ? [Path.Combine(working, project)] :
                [.. Directory.EnumerateFiles(working, "*.csproj")];
            if (projects.Length != 1 || target is null && Directory.EnumerateFiles(working, "*.sln*").Any())
                return _ => true;
            foreach (string projectPath in projects)
                if (!AddProject(projectPath))
                    return _ => true;
        }
        return path => configuration.Contains(path) || roots.Any(root =>
            path.StartsWith(root, PlatformPaths.Comparison) && path.Length > root.Length && path[root.Length] == '/');

        bool AddProject(string file)
        {
            if (!PlatformPaths.IsUnder(file, gate.Path) || !File.Exists(file))
                return false;
            if (!projectsSeen.Add(file))
                return true;
            string directory = Path.GetDirectoryName(file)!;
            if (PlatformPaths.Same(directory, gate.Path))
                return false;
            roots.Add(Relative(directory));
            if (!AddReferences(file))
                return false;
            for (string? at = directory; at is not null && PlatformPaths.IsUnder(at, gate.Path); at = Path.GetDirectoryName(at))
            {
                foreach (string name in BuildConfiguration)
                {
                    string path = Path.Combine(at, name);
                    configuration.Add(Relative(path));
                    if ((name.EndsWith(".props", StringComparison.Ordinal) || name.EndsWith(".targets", StringComparison.Ordinal)) &&
                        File.Exists(path) && !AddReferences(path))
                        return false;
                }
            }
            return true;
        }

        bool AddReferences(string file)
        {
            if (!examined.Add(file))
                return true;
            XDocument project;
            try { project = XDocument.Load(file); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Xml.XmlException) { return false; }

            foreach (XElement field in project.Descendants())
            {
                string kind = field.Name.LocalName;
                string? source = kind switch
                {
                    "ProjectReference" or "Compile" or "EmbeddedResource" or "Content" or "None" or "AdditionalFiles" or "Analyzer" => field.Attribute("Include")?.Value,
                    "Import" => field.Attribute("Project")?.Value,
                    "HintPath" => field.Value,
                    _ => null
                };
                if (source is null)
                    continue;
                foreach (string name in source.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (name.IndexOfAny(['$', '%', '@']) >= 0)
                        return false;
                    string literal = name.Split(['*', '?'], 2)[0];
                    string path = Path.GetFullPath(literal.Replace('\\', '/'), Path.GetDirectoryName(file)!);
                    if (!PlatformPaths.IsUnder(path, gate.Path))
                        return false;
                    if (kind == "ProjectReference")
                    {
                        if (!AddProject(path))
                            return false;
                    }
                    else if (kind == "Import")
                    {
                        configuration.Add(Relative(path));
                        if (File.Exists(path) && !AddReferences(path))
                            return false;
                    }
                    else if (name.Length != literal.Length)
                    {
                        string directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
                        if (PlatformPaths.Same(directory, gate.Path) || !PlatformPaths.IsUnder(directory, gate.Path))
                            return false;
                        roots.Add(Relative(directory));
                    }
                    else
                    {
                        configuration.Add(Relative(path));
                    }
                }
            }
            return true;
        }

        string Relative(string full) => Path.GetRelativePath(gate.Path, full).Replace('\\', '/');
    }

    private static bool PlainProject(string path) =>
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
        path.IndexOfAny(['/', '\\', ':', '*', '?']) < 0;

    private static bool TryDotnetProject(string command, out string? target)
    {
        target = null;
        if (command.IndexOfAny(['&', '|', ';', '<', '>', '\n', '\r', ', (char)96]) >= 0)
            return false;
        string[] words = command.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || !words[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
            !words[1].Equals("build", StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (string word in words.Skip(2))
        {
            if (word.StartsWith("-m:", StringComparison.OrdinalIgnoreCase) || word.StartsWith("-v:", StringComparison.OrdinalIgnoreCase) ||
                word is "--no-restore" or "--no-incremental" or "--nologo" or "-p:ImportDirectoryBuildProps=false" or "-p:ImportDirectoryBuildTargets=false")
                continue;
            string file = word.Trim('"');
            if (target is not null || !PlainProject(file))
                return false;
            target = file;
        }
        return true;
    }
}
