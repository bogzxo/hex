namespace Hex.Core.Projects;

internal static partial class DotnetTests
{
    /// <summary>Prebuilt framework hosts can overlap only when their SDK outputs remain isolated.</summary>
    public static bool CanRunFrameworksInParallel(IReadOnlyList<DotnetEvaluation> targets)
    {
        if (targets.Count < 2) return false;
        var outputs = new HashSet<string>(PlatformPaths.Comparer);
        var intermediates = new HashSet<string>(PlatformPaths.Comparer);
        foreach (var target in targets)
        {
            if (target.Properties.GetValueOrDefault("TestTfmsInParallel")?.Trim().Equals("false", StringComparison.OrdinalIgnoreCase) == true
                || target.OutputFolder.Length == 0 || target.TargetPath.Length == 0
                || string.IsNullOrWhiteSpace(target.Properties.GetValueOrDefault("IntermediateOutputPath"))) return false;
            string intermediate = Path.GetFullPath(Path.Combine(target.Folder,
                target.Properties["IntermediateOutputPath"].Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
            if (!outputs.Add(Path.TrimEndingDirectorySeparator(target.OutputFolder))
                || !intermediates.Add(Path.TrimEndingDirectorySeparator(intermediate))) return false;
        }
        return true;
    }
}
