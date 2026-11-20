using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Hex.Core;

/// <summary>
/// A project of the workspace as its build system says it, the file that makes it one, its name, its build system and
/// kind (library, exe, what the file says), what it targets, the other projects of the workspace it uses (as their files,
/// or their names where a path couldn't be told) and the packages it takes from outside.
/// </summary>
internal sealed record HexProject(string Path, string Name, string System, string Kind, string Targets, List<string> Uses, List<string> Packages);

// hex_projects, the projects of the workspace and what uses what, whatever builds them: MSBuild (.csproj, .fsproj,
// .vcxproj), npm (package.json), Cargo, Go modules, Python (pyproject.toml) and CMake. Read from the files the index
// has, nothing is built and no tool is run. dotnet-claude-kit's get_project_graph for every language, with the other
// way round (what uses a project, what a change to it can break) said too.
internal sealed partial class HexCodeIndex
{
    private static readonly string[] PROJECT_FILES = ["package.json", "Cargo.toml", "go.mod", "pyproject.toml", "CMakeLists.txt"];

    /// <summary>Whether a file of the workspace makes a project, by its name.</summary>
    internal static bool IsProjectFile(string relative)
    {
        string name = System.IO.Path.GetFileName(relative);
        string extension = System.IO.Path.GetExtension(name).ToLowerInvariant();
        return extension is ".csproj" or ".fsproj" or ".vbproj" or ".vcxproj" || PROJECT_FILES.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>hex_projects, the projects under a folder (all of them when it isn't said), each with what it uses and what uses it.</summary>
    public HexAnswer Projects(string? under, bool packages, int budget) =>
        Ask("hex_projects", $"{under}\n{packages}\n{budget}", index =>
        {
            List<HexIndexedFile> files = [.. index.Files.Where(file => IsProjectFile(file.Relative) && !IsProjectNoise(file.Relative))];
            List<HexProject> projects = [.. files.Select(file => ProjectOf(file.Path, File.Exists(file.Path) ? HexSearch.ReadLines(file.Path) : [])).OfType<HexProject>()];
            Resolve(projects);

            Dictionary<string, HexIndexedFile> byPath = files.ToDictionary(file => file.Path, PlatformPaths.Comparer);
            string RelativeOf(string path) => byPath.TryGetValue(path, out HexIndexedFile? file) ? file.Relative : path;
            List<HexProject> shown = [.. projects.Where(project => under is not { Length: > 0 } || RelativeOf(project.Path).StartsWith(under.Replace('\\', '/').TrimStart('.', '/'), StringComparison.OrdinalIgnoreCase))
                .OrderBy(project => RelativeOf(project.Path), StringComparer.OrdinalIgnoreCase)];

            var said = new HexBudget(budget);
            int edges = projects.Sum(project => project.Uses.Count(use => System.IO.Path.IsPathRooted(use)));
            said.Line(shown.Count == 0
                ? $"No project files{(under is { Length: > 0 } ? $" under {under}" : string.Empty)} (.csproj, package.json, Cargo.toml, go.mod, pyproject.toml, CMakeLists.txt)."
                : $"{shown.Count} projects{(shown.Count != projects.Count ? $" of {projects.Count}" : string.Empty)}, {edges} references between the workspace's projects. -> uses, <- used by.");
            foreach (HexProject project in shown)
            {
                string targets = project.Targets.Length == 0 ? string.Empty : $" {project.Targets}";
                said.Line($"{RelativeOf(project.Path)}  {project.Name} ({project.System} {project.Kind}{targets})");
                if (project.Uses.Count > 0)
                    said.Line("  -> " + string.Join(", ", project.Uses.Select(use => System.IO.Path.IsPathRooted(use) ? NameOf(projects, use) : use + "?").Distinct(StringComparer.OrdinalIgnoreCase)));
                List<string> usedBy = [.. projects.Where(other => other.Uses.Contains(project.Path, PlatformPaths.Comparer)).Select(other => other.Name).Distinct(StringComparer.OrdinalIgnoreCase)];
                if (usedBy.Count > 0)
                    said.Line("  <- " + string.Join(", ", usedBy));
                if (project.Packages.Count > 0)
                    said.Line(packages ? "  packages: " + string.Join(", ", project.Packages) : $"  {project.Packages.Count} packages (packages true lists them)");
            }

            return new HexAnswer(said.Finish("narrow with under or a bigger max_chars"), files.Sum(file => file.Length));
        });

    /// <summary>
    /// A project file that isn't one of the workspace's own projects: a package's under node_modules, or anything under a
    /// docs, benchmarks or samples folder (hex's docs/benchmarks keep a package.json that's only numbers).
    /// </summary>
    internal static bool IsProjectNoise(string relative)
    {
        string path = "/" + relative.Replace('\\', '/');
        return path.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase) || path.Contains("/docs/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/benchmarks/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NameOf(List<HexProject> projects, string path) =>
        projects.Find(project => PlatformPaths.Same(project.Path, path))?.Name ?? System.IO.Path.GetFileNameWithoutExtension(path);

    /// <summary>
    /// The uses that are names (a crate, a package, a CMake target, a project path with an MSBuild property in it) made
    /// the file of the workspace's project of that name where there's one, the rest left as packages from outside.
    /// </summary>
    internal static void Resolve(List<HexProject> projects)
    {
        foreach (HexProject project in projects)
        {
            for (int i = 0; i < project.Uses.Count; i++)
            {
                string use = project.Uses[i];
                if (System.IO.Path.IsPathRooted(use) && projects.Exists(other => PlatformPaths.Same(other.Path, use)))
                    continue;

                string name = System.IO.Path.GetFileNameWithoutExtension(use.Replace('\\', '/'));
                HexProject? other = projects.Find(candidate => !ReferenceEquals(candidate, project) && (candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || System.IO.Path.GetFileNameWithoutExtension(candidate.Path).Equals(name, StringComparison.OrdinalIgnoreCase) && candidate.System == "MSBuild"));
                if (other is not null)
                    project.Uses[i] = other.Path;
            }

            // A package that's a project of the workspace is a use of it, not something from outside
            for (int i = project.Packages.Count - 1; i >= 0; i--)
            {
                string name = project.Packages[i].Split(' ')[0];
                if (projects.Find(other => !ReferenceEquals(other, project) && other.System == project.System && other.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } local)
                {
                    project.Packages.RemoveAt(i);
                    if (!project.Uses.Contains(local.Path, PlatformPaths.Comparer))
                        project.Uses.Add(local.Path);
                }
            }

            project.Uses.RemoveAll(use => !System.IO.Path.IsPathRooted(use) && project.Packages.Exists(package => package.Split(' ')[0].Equals(use, StringComparison.OrdinalIgnoreCase)));
        }
    }

    /// <summary>What a project file says, null for one that isn't a project after all (a CMakeLists.txt of only subdirectories is still one).</summary>
    internal static HexProject? ProjectOf(string path, string[] lines)
    {
        string name = System.IO.Path.GetFileName(path);
        string folder = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        try
        {
            return System.IO.Path.GetExtension(name).ToLowerInvariant() switch
            {
                ".csproj" or ".fsproj" or ".vbproj" or ".vcxproj" => MsBuildProject(path, folder, string.Join('\n', lines)),
                _ when name.Equals("package.json", StringComparison.OrdinalIgnoreCase) => NpmProject(path, folder, string.Join('\n', lines)),
                _ when name.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase) => CargoProject(path, folder, lines),
                _ when name.Equals("go.mod", StringComparison.OrdinalIgnoreCase) => GoProject(path, folder, lines),
                _ when name.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) => PythonProject(path, lines),
                _ when name.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase) => CMakeProjectOf(path, folder, lines),
                _ => null
            };
        }
        catch (Exception e) when (e is System.Xml.XmlException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return new HexProject(path, System.IO.Path.GetFileNameWithoutExtension(folder), "?", "unreadable", string.Empty, [], []);
        }
    }

    private static string Full(string folder, string relative) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, relative.Replace('\\', System.IO.Path.DirectorySeparatorChar).Replace('/', System.IO.Path.DirectorySeparatorChar)));

    private static HexProject MsBuildProject(string path, string folder, string text)
    {
        XElement root = XDocument.Parse(text).Root ?? throw new InvalidOperationException();
        IEnumerable<XElement> All(string local) => root.Descendants().Where(element => element.Name.LocalName == local);
        string? First(string local) => All(local).Select(element => element.Value.Trim()).FirstOrDefault(value => value.Length > 0);

        string name = First("AssemblyName") is { } assembly && !assembly.Contains("$(", StringComparison.Ordinal) ? assembly : System.IO.Path.GetFileNameWithoutExtension(path);
        string kind = First("OutputType")?.ToLowerInvariant() switch
        {
            "exe" or "winexe" => "exe",
            null => First("ConfigurationType")?.ToLowerInvariant() switch { "application" => "exe", "staticlibrary" => "static library", "dynamiclibrary" => "dll", _ => "library" },
            { } other => other
        };
        if (All("PackageReference").Any(element => (string?)element.Attribute("Include") is { } package && package.Contains("Test.Sdk", StringComparison.OrdinalIgnoreCase)))
            kind = "tests";
        string targets = First("TargetFrameworks") ?? First("TargetFramework") ?? string.Empty;

        List<string> uses = [.. All("ProjectReference").Select(element => (string?)element.Attribute("Include")).OfType<string>()
            .Select(include => include.Contains("$(", StringComparison.Ordinal) ? include : Full(folder, include))];
        List<string> packages = [.. All("PackageReference").Select(element => ((string?)element.Attribute("Include"), (string?)element.Attribute("Version") ?? element.Elements().FirstOrDefault(child => child.Name.LocalName == "Version")?.Value))
            .Where(each => each.Item1 is not null).Select(each => each.Item2 is { Length: > 0 } version ? $"{each.Item1} {version}" : each.Item1!)];
        return new HexProject(path, name, "MSBuild", kind, targets, uses, packages);
    }

    // What a package.json of npm's has at least one of, a JSON file of that name with none of them is only data
    private static readonly string[] NPM_KEYS = ["name", "version", "dependencies", "devDependencies", "peerDependencies", "workspaces", "scripts", "main", "module", "exports", "bin", "private"];

    private static HexProject? NpmProject(string path, string folder, string text)
    {
        using var json = System.Text.Json.JsonDocument.Parse(text, new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
        System.Text.Json.JsonElement root = json.RootElement;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object || !NPM_KEYS.Any(key => root.TryGetProperty(key, out _)))
            return null;
        string? Text(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;

        string name = Text("name") ?? System.IO.Path.GetFileName(folder);
        string kind = root.TryGetProperty("workspaces", out _) ? "workspace" : root.TryGetProperty("bin", out _) ? "cli" : Text("private") is "true" ? "app" : "package";
        List<string> uses = [], packages = [];
        foreach (string section in (string[])["dependencies", "devDependencies", "peerDependencies"])
        {
            if (!root.TryGetProperty(section, out var dependencies) || dependencies.ValueKind != System.Text.Json.JsonValueKind.Object)
                continue;
            foreach (var dependency in dependencies.EnumerateObject())
            {
                string version = dependency.Value.ValueKind == System.Text.Json.JsonValueKind.String ? dependency.Value.GetString() ?? string.Empty : string.Empty;
                if (version.StartsWith("file:", StringComparison.Ordinal) || version.StartsWith("link:", StringComparison.Ordinal))
                    uses.Add(Full(folder, System.IO.Path.Combine(version[5..], "package.json")));
                else if (version.StartsWith("workspace:", StringComparison.Ordinal))
                    uses.Add(dependency.Name);
                else
                    packages.Add($"{dependency.Name} {version}".Trim() + (section == "dependencies" ? string.Empty : " (dev)"));
            }
        }

        return new HexProject(path, name, "npm", kind, Text("version") ?? string.Empty, uses, packages);
    }

    // The lines of a TOML file by the [section] they're under, key = value, the little of TOML project files need
    private static List<(string Section, string Key, string Value)> Toml(string[] lines)
    {
        var read = new List<(string, string, string)>();
        string section = string.Empty;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (line[0] == '[')
            {
                section = line.Trim('[', ']', ' ');
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals <= 0)
                continue;
            string key = line[..equals].Trim().Trim('"'), value = line[(equals + 1)..].Trim();

            // An array over several lines, put together
            if (value.StartsWith('[') && !value.Contains(']'))
            {
                while (++i < lines.Length && !lines[i].Contains(']'))
                    value += lines[i].Trim();
                if (i < lines.Length)
                    value += lines[i].Trim();
            }
            read.Add((section, key, value));
        }

        return read;
    }

    private static string Unquoted(string value) => value.Trim().Trim('"', '\'');

    private static HexProject CargoProject(string path, string folder, string[] lines)
    {
        var toml = Toml(lines);
        string name = toml.Find(each => each.Section == "package" && each.Key == "name").Value is { Length: > 0 } named ? Unquoted(named) : System.IO.Path.GetFileName(folder);
        bool workspace = toml.Exists(each => each.Section == "workspace");
        string kind = workspace && !toml.Exists(each => each.Section == "package") ? "workspace" : toml.Exists(each => each.Section is "[bin]" or "bin") || File.Exists(System.IO.Path.Combine(folder, "src", "main.rs")) ? "bin" : "lib";
        List<string> uses = [], packages = [];
        foreach (var (section, key, value) in toml.Where(each => each.Section.EndsWith("dependencies", StringComparison.Ordinal)))
        {
            Match local = Regex.Match(value, @"path\s*=\s*""([^""]+)""");
            if (local.Success)
                uses.Add(Full(folder, System.IO.Path.Combine(local.Groups[1].Value, "Cargo.toml")));
            else
            {
                Match version = Regex.Match(value, @"version\s*=\s*""([^""]+)""");
                string said = version.Success ? version.Groups[1].Value : value.StartsWith('"') ? Unquoted(value) : string.Empty;
                packages.Add($"{key} {said}".Trim() + (section.StartsWith("dev", StringComparison.Ordinal) ? " (dev)" : string.Empty));
            }
        }

        if (toml.Find(each => each.Section == "workspace" && each.Key == "members").Value is { Length: > 0 } members)
            uses.AddRange(Regex.Matches(members, @"""([^""*]+)""").Select(match => Full(folder, System.IO.Path.Combine(match.Groups[1].Value, "Cargo.toml"))));
        return new HexProject(path, name, "Cargo", kind, toml.Find(each => each.Section == "package" && each.Key == "edition").Value is { Length: > 0 } edition ? "edition " + Unquoted(edition) : string.Empty, uses, packages);
    }

    private static HexProject GoProject(string path, string folder, string[] lines)
    {
        string name = System.IO.Path.GetFileName(folder), version = string.Empty;
        List<string> uses = [], packages = [];
        bool requiring = false;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.StartsWith("module ", StringComparison.Ordinal))
                name = line[7..].Trim();
            else if (line.StartsWith("go ", StringComparison.Ordinal))
                version = "go " + line[3..].Trim();
            else if (line.StartsWith("require (", StringComparison.Ordinal))
                requiring = true;
            else if (requiring && line == ")")
                requiring = false;
            else if (requiring || line.StartsWith("require ", StringComparison.Ordinal))
            {
                string[] parts = (requiring ? line : line[8..]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1 && !parts[0].StartsWith("//", StringComparison.Ordinal))
                    packages.Add(string.Join(' ', parts.Take(2)) + (line.Contains("// indirect", StringComparison.Ordinal) ? " (indirect)" : string.Empty));
            }
            else if (line.StartsWith("replace ", StringComparison.Ordinal) && line.Split("=>") is [_, var to] && to.Trim() is var target && (target.StartsWith('.') || target.StartsWith('/')))
            {
                uses.Add(Full(folder, System.IO.Path.Combine(target.Split(' ')[0], "go.mod")));
            }
        }

        return new HexProject(path, name, "Go", File.Exists(System.IO.Path.Combine(folder, "main.go")) ? "exe" : "module", version, uses, packages);
    }

    private static HexProject PythonProject(string path, string[] lines)
    {
        var toml = Toml(lines);
        string folder = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        string name = (toml.Find(each => each.Section is "project" or "tool.poetry" && each.Key == "name").Value is { Length: > 0 } named ? Unquoted(named) : null) ?? System.IO.Path.GetFileName(folder);
        List<string> packages = [];
        if (toml.Find(each => each.Section == "project" && each.Key == "dependencies").Value is { Length: > 0 } listed)
            packages.AddRange(Regex.Matches(listed, @"""([^""]+)""|'([^']+)'").Select(match => (match.Groups[1].Success ? match.Groups[1] : match.Groups[2]).Value));
        foreach (var (_, key, value) in toml.Where(each => each.Section == "tool.poetry.dependencies" && each.Key != "python"))
            packages.Add($"{key} {Unquoted(value)}".Trim());
        string kind = toml.Exists(each => each.Section is "project.scripts" or "tool.poetry.scripts") ? "cli" : "package";
        string python = toml.Find(each => each.Key == "requires-python").Value is { Length: > 0 } requires ? "python " + Unquoted(requires) : string.Empty;
        return new HexProject(path, name, "Python", kind, python, [], packages);
    }

    private static HexProject CMakeProjectOf(string path, string folder, string[] lines)
    {
        string text = string.Join('\n', lines.Select(line => line.Split('#')[0]));
        string name = Regex.Match(text, @"\bproject\s*\(\s*([\w.-]+)", RegexOptions.IgnoreCase) is { Success: true } project ? project.Groups[1].Value : System.IO.Path.GetFileName(folder);
        List<string> targets = [.. Regex.Matches(text, @"\badd_(executable|library)\s*\(\s*([\w.-]+)", RegexOptions.IgnoreCase).Select(match => $"{match.Groups[2].Value} ({(match.Groups[1].Value.Equals("executable", StringComparison.OrdinalIgnoreCase) ? "exe" : "lib")})")];
        List<string> uses = [.. Regex.Matches(text, @"\badd_subdirectory\s*\(\s*([^\s)]+)", RegexOptions.IgnoreCase).Select(match => Full(folder, System.IO.Path.Combine(match.Groups[1].Value.Trim('"'), "CMakeLists.txt")))];
        var linked = new List<string>();
        foreach (Match link in Regex.Matches(text, @"\btarget_link_libraries\s*\(\s*[\w.-]+\s+([^)]*)\)", RegexOptions.IgnoreCase))
            linked.AddRange(link.Groups[1].Value.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Where(word => word is not ("PUBLIC" or "PRIVATE" or "INTERFACE") && !word.StartsWith('$')));
        // A library linked that's a target of another CMakeLists of the workspace is a use of it, Resolve finds it by name
        uses.AddRange(linked.Distinct(StringComparer.Ordinal).Where(word => !word.Contains("::", StringComparison.Ordinal)));
        List<string> packages = [.. Regex.Matches(text, @"\bfind_package\s*\(\s*([\w.-]+)", RegexOptions.IgnoreCase).Select(match => match.Groups[1].Value)
            .Concat(linked.Where(word => word.Contains("::", StringComparison.Ordinal))).Distinct(StringComparer.Ordinal)];
        return new HexProject(path, name, "CMake", targets.Count == 0 ? "folder" : string.Join(", ", targets), string.Empty, uses, packages);
    }
}
