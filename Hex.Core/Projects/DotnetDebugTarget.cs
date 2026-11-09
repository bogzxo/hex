namespace Hex.Core.Projects;

/// <summary>One evaluated build context for the binary being debugged.</summary>
internal static class DotnetDebugTarget
{
    public static List<string> BuildArguments(string project, string configuration, string? framework, DotnetRuntimeOptions? runtime = null)
    {
        List<string> arguments = ["build", project, "-c", configuration, "-nologo", "-v", "q"];
        if (!string.IsNullOrEmpty(framework)) arguments.AddRange(["--framework", framework]);
        runtime?.AddArguments(arguments);
        return arguments;
    }

    public static List<string> PropertyArguments(string project, string configuration, string? framework, string properties, DotnetRuntimeOptions? runtime = null)
    {
        // -getProperty without a target evaluates only. --no-restore also prevents the build CLI's implicit restore.
        // The CLI owns --arch/--os inference and its command-line marker properties; hand-built RIDs lose that context.
        List<string> arguments = runtime?.Any == true ? ["build", project, "--no-restore"] : ["msbuild", project];
        arguments.AddRange(["-getProperty:" + properties, "-p:Configuration=" + Escape(configuration), "-nologo"]);
        if (!string.IsNullOrEmpty(framework)) arguments.Add("-p:TargetFramework=" + Escape(framework));
        runtime?.AddArguments(arguments);
        return arguments;
    }

    public static IReadOnlyList<string> Frameworks(DotnetEvaluation evaluation)
    {
        string single = evaluation.Properties.GetValueOrDefault("TargetFramework") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(single)) return [single.Trim()];
        return (evaluation.Properties.GetValueOrDefault("TargetFrameworks") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static (string Program, IReadOnlyList<string> PrefixArguments) LaunchTarget(DotnetEvaluation evaluation, DotnetRuntimeOptions? runtime = null)
    {
        // The SDK's apphost owns OS/architecture selection. Launching its DLL instead can bypass that selection.
        bool selected = runtime?.Any == true || !string.IsNullOrEmpty(evaluation.Properties.GetValueOrDefault("RuntimeIdentifier"));
        string? command = evaluation.Properties.GetValueOrDefault("RunCommand");
        return selected && !string.IsNullOrWhiteSpace(command)
            ? (command, DotnetCommandLine.Words(evaluation.Properties.GetValueOrDefault("RunArguments") ?? string.Empty))
            : (evaluation.TargetPath, []);
    }

    private static string Escape(string value) => value.Replace("%", "%25", StringComparison.Ordinal).Replace(";", "%3B", StringComparison.Ordinal).Replace(",", "%2C", StringComparison.Ordinal);
}
