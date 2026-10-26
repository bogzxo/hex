using System.Text.Json;

namespace Hex.Core.Projects;

/// <summary>
/// What a project's program is started with under the debugger, the arguments of its Properties/launchSettings.json the
/// way Visual Studio reads them (the first profile that runs the project). Hex debugging Hex gets --window as well, or
/// the Hex it starts hands its folder to the Hex that's running and leaves.
/// </summary>
internal static class DotnetLaunch
{
    private const string WINDOW = "--window";

    /// <summary>The arguments a project's program is started with, a program of Hex's own with --window among them.</summary>
    public static List<string> ArgumentsFor(string project, string program, string? runningExe)
    {
        List<string> arguments = ProfileArguments(project);
        bool isHex = runningExe is not null && Path.GetFileNameWithoutExtension(program).Equals(Path.GetFileNameWithoutExtension(runningExe), StringComparison.OrdinalIgnoreCase);
        if (isHex && !arguments.Contains(WINDOW, StringComparer.OrdinalIgnoreCase))
            arguments.Insert(0, WINDOW);
        return arguments;
    }

    /// <summary>The commandLineArgs of the first profile of launchSettings.json that runs the project, none without one.</summary>
    public static List<string> ProfileArguments(string project)
    {
        string settings = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(project))!, "Properties", "launchSettings.json");
        if (!File.Exists(settings))
            return [];

        try
        {
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(settings), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!json.RootElement.TryGetProperty("profiles", out JsonElement profiles))
                return [];

            foreach (JsonProperty profile in profiles.EnumerateObject())
            {
                bool runsProject = !profile.Value.TryGetProperty("commandName", out JsonElement command) || command.GetString() == "Project";
                if (runsProject)
                    return profile.Value.TryGetProperty("commandLineArgs", out JsonElement said) && said.GetString() is { Length: > 0 } line ? DotnetCommandLine.Words(line) : [];
            }
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException)
        {
        }

        return [];
    }
}
