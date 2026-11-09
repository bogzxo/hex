namespace Hex.Core.Projects;

internal sealed partial class DotnetSolutionTree
{
    // Asked only when a project's dependencies are viewed or edited. Opening a solution
    // must not start an SDK process for every project.
    public static async Task<(IReadOnlyList<string> References, IReadOnlyList<SolutionPackage> Packages, IReadOnlyList<SolutionProjectFile> Files)> EvaluateDependencies(
        string project, string configuration, CancellationToken cancel = default)
    {
        async Task<DotnetEvaluation> Read(string? framework = null)
        {
            var arguments = new List<string> { "msbuild", project, "-nologo", "-p:Configuration=" + configuration,
                "-getItem:ProjectReference,PackageReference,PackageVersion,Compile,None,Content,EmbeddedResource,AdditionalFiles,Page,Resource,ApplicationDefinition", "-getProperty:TargetFramework,TargetFrameworks" };
            if (framework is not null)
                arguments.Add("-p:TargetFramework=" + framework);
            var (exit, output) = await DotnetCli.Run(System.IO.Path.GetDirectoryName(project)!, arguments, TimeSpan.FromSeconds(90), cancel);
            if (exit != 0)
                throw new InvalidDataException(output.Trim());
            return DotnetEvaluation.Parse(project, output);
        }
        DotnetEvaluation evaluated = await Read();
        if (string.IsNullOrEmpty(evaluated.Properties.GetValueOrDefault("TargetFramework"))
            && evaluated.Properties.GetValueOrDefault("TargetFrameworks") is { Length: > 0 } frameworks)
        {
            foreach (string framework in frameworks.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                evaluated.Items.AddRange((await Read(framework)).Items);
        }
        var versions = evaluated.Items.Where(item => item.Type == "PackageVersion")
            .GroupBy(item => item.Identity, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Metadata.GetValueOrDefault("Version") ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        return (
            evaluated.Items.Where(item => item.Type == "ProjectReference").Select(item => item.FullPath).Distinct(PlatformPaths.Comparer).ToArray(),
            evaluated.Items.Where(item => item.Type == "PackageReference")
                .Select(item => new SolutionPackage(item.Identity,
                    item.Metadata.GetValueOrDefault("VersionOverride") is { Length: > 0 } over ? over :
                    item.Metadata.GetValueOrDefault("Version") is { Length: > 0 } version ? version : versions.GetValueOrDefault(item.Identity) ?? string.Empty))
                .Distinct().OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            ProjectFiles(evaluated));
    }
    private static IReadOnlyList<SolutionProjectFile> ProjectFiles(DotnetEvaluation evaluation)
    {
        string folder = System.IO.Path.GetDirectoryName(evaluation.Project)!;
        var files = new Dictionary<string, SolutionProjectFile>(PlatformPaths.Comparer);
        foreach (var item in evaluation.Items)
        {
            if (item.Type is not ("Compile" or "None" or "Content" or "EmbeddedResource" or "AdditionalFiles" or "Page" or "Resource" or "ApplicationDefinition")
                || string.Equals(item.Metadata.GetValueOrDefault("Visible"), "false", StringComparison.OrdinalIgnoreCase))
                continue;
            string path = System.IO.Path.GetFullPath(item.FullPath);
            string relative = System.IO.Path.GetRelativePath(folder, path);
            string display = item.Metadata.GetValueOrDefault("Link") is { Length: > 0 } link ? link : relative;
            display = display.Replace('\\', '/');
            if (System.IO.Path.IsPathRooted(display) || display.Split('/').Any(part => part is "." or ".."))
                display = System.IO.Path.GetFileName(path);
            bool linked = !PlatformPaths.IsUnder(path, folder) || display != relative.Replace('\\', '/');
            string key = display + "\0" + path;
            if (files.TryGetValue(key, out var previous))
                files[key] = previous with { ItemTypes = string.Join(", ", previous.ItemTypes.Split(", ").Append(item.Type).Distinct().Order()) };
            else
                files[key] = new(path, display, item.Type, linked);
        }
        return [.. files.Values.OrderBy(file => file.DisplayPath, StringComparer.OrdinalIgnoreCase)];
    }
}
