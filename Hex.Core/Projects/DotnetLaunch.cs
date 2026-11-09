using System.Text.Json;

namespace Hex.Core.Projects;

/// <summary>
/// A profile of a project's Properties/launchSettings.json that runs the project, its name, the command line its program
/// gets, the folder it runs in (null for the project's own) and what it puts in the environment.
/// </summary>
internal sealed record DotnetLaunchProfile(string Name, string CommandLine, string? WorkingDirectory, IReadOnlyDictionary<string, string> Environment)
{
    /// <summary>ASP.NET Core's listening URLs; null leaves an existing value untouched when editing other fields.</summary>
    public string? ApplicationUrl { get; init; }

    /// <summary>SDK-generated launch values, followed by explicit profile overrides and native environment expansion.</summary>
    public IReadOnlyDictionary<string, string> RuntimeEnvironment()
    {
        var values = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        { ["DOTNET_LAUNCH_PROFILE"] = Name };
        if (ApplicationUrl is { Length: > 0 }) values["ASPNETCORE_URLS"] = System.Environment.ExpandEnvironmentVariables(ApplicationUrl);
        foreach (var (name, value) in Environment) values[name] = System.Environment.ExpandEnvironmentVariables(value);
        return values;
    }

    /// <summary>The command line split into its arguments, the way the program gets them.</summary>
    public List<string> Arguments => CommandLine.Length > 0 ? DotnetCommandLine.Words(CommandLine) : [];

    /// <summary>The folder it runs in made whole from the project's, null for the project's own folder.</summary>
    public string? WorkingFolder(string project)
    {
        if (WorkingDirectory is not { Length: > 0 } said)
            return null;

        string folder = Path.GetDirectoryName(Path.GetFullPath(project))!;
        string expanded = System.Environment.ExpandEnvironmentVariables(said.Replace("$(ProjectDir)", folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        return Path.GetFullPath(Path.Combine(folder, expanded));
    }
}

/// <summary>
/// How a project's program is started, by F5 under the debugger and by Ctrl+F5 and Run without it, the one place both
/// go to. Visual Studio's launch profiles, the profiles of Properties/launchSettings.json that run the project, the one
/// picked beside the Debug or Release button or else the first. Hex debugging Hex gets --window as well, or the Hex it
/// starts hands its folder to the Hex that's running and leaves.
/// </summary>
internal static class DotnetLaunch
{
    private const string WINDOW = "--window";

    // What a profile that runs the project says it is, the kind dotnet run takes
    private const string PROJECT = "Project";

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Where a project's launch settings are, whether they're there or not.</summary>
    public static string SettingsOf(string project) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(project))!, "Properties", "launchSettings.json");

    /// <summary>The profiles of a project that run it, in the order its launchSettings.json has them, none without one.</summary>
    public static List<DotnetLaunchProfile> Profiles(string project)
    {
        string settings = SettingsOf(project);
        if (!File.Exists(settings))
            return [];

        try
        {
            return ProfilesIn(File.ReadAllText(settings));
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>The profiles that run the project out of the text of a launchSettings.json.</summary>
    internal static List<DotnetLaunchProfile> ProfilesIn(string json)
    {
        var found = new List<DotnetLaunchProfile>();
        try
        {
            using JsonDocument read = JsonDocument.Parse(json, Lenient);
            if (read.RootElement.ValueKind != JsonValueKind.Object || !read.RootElement.TryGetProperty("profiles", out JsonElement profiles) || profiles.ValueKind != JsonValueKind.Object)
                return found;

            foreach (JsonProperty profile in profiles.EnumerateObject())
            {
                JsonElement values = profile.Value;
                if (values.ValueKind != JsonValueKind.Object)
                    continue;
                if (values.TryGetProperty("commandName", out JsonElement command) && command.GetString() != PROJECT)
                    continue;

                var environment = new Dictionary<string, string>(StringComparer.Ordinal);
                if (values.TryGetProperty("environmentVariables", out JsonElement variables) && variables.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty variable in variables.EnumerateObject())
                        environment[variable.Name] = variable.Value.ValueKind == JsonValueKind.String ? variable.Value.GetString()! : variable.Value.GetRawText();
                }

                found.Add(new DotnetLaunchProfile(profile.Name, Text(values, "commandLineArgs") ?? string.Empty, Text(values, "workingDirectory"), environment)
                { ApplicationUrl = Text(values, "applicationUrl") });
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            // A launchSettings.json that isn't JSON has no profiles as far as this goes, dotnet run says what's wrong with it
        }

        return found;
    }

    private static string? Text(JsonElement values, string name) =>
        values.TryGetProperty(name, out JsonElement said) && said.ValueKind == JsonValueKind.String ? said.GetString() : null;

    /// <summary>The profile a project's program starts with, the one picked when it's still there, else the first, null for none.</summary>
    public static DotnetLaunchProfile? For(string project, string? picked)
    {
        List<DotnetLaunchProfile> profiles = Profiles(project);
        return profiles.Find(profile => profile.Name == picked) ?? profiles.FirstOrDefault();
    }

    /// <summary>The arguments a project's program is started with, a program of Hex's own with --window among them.</summary>
    public static List<string> ArgumentsFor(string program, DotnetLaunchProfile? profile, string? runningExe)
    {
        List<string> arguments = profile?.Arguments ?? [];
        bool isHex = runningExe is not null && Path.GetFileNameWithoutExtension(program).Equals(Path.GetFileNameWithoutExtension(runningExe), StringComparison.OrdinalIgnoreCase);
        if (isHex && !arguments.Contains(WINDOW, StringComparer.OrdinalIgnoreCase))
            arguments.Insert(0, WINDOW);
        return arguments;
    }

    /// <summary>The arguments of the first profile, what F5 started a program with before profiles could be picked.</summary>
    public static List<string> ArgumentsFor(string project, string program, string? runningExe) =>
        ArgumentsFor(program, For(project, null), runningExe);

    /// <summary>
    /// A profile written into a project's launchSettings.json, made with the profile when it isn't there, the file and its
    /// Properties folder made when they aren't either, the way Visual Studio's Debug page makes them. Everything else the
    /// file has (other profiles, browser settings, what's in it Hex knows nothing about) stays.
    /// </summary>
    public static void Save(string project, DotnetLaunchProfile profile)
    {
        string settings = SettingsOf(project);
        string? before = File.Exists(settings) ? File.ReadAllText(settings) : null;
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, WithProfile(before, profile));
    }

    /// <summary>The text of a launchSettings.json with a profile set in it, a new file's when there's none.</summary>
    internal static string WithProfile(string? json, DotnetLaunchProfile profile)
    {
        System.Text.Json.Nodes.JsonObject root;
        try
        {
            root = json is null ? [] : System.Text.Json.Nodes.JsonNode.Parse(json, documentOptions: Lenient) as System.Text.Json.Nodes.JsonObject ?? [];
        }
        catch (JsonException)
        {
            root = [];
        }

        if (root["profiles"] is not System.Text.Json.Nodes.JsonObject profiles)
            root["profiles"] = profiles = [];
        if (profiles[profile.Name] is not System.Text.Json.Nodes.JsonObject values)
            profiles[profile.Name] = values = new System.Text.Json.Nodes.JsonObject { ["commandName"] = PROJECT };

        Set(values, "commandLineArgs", profile.CommandLine);
        Set(values, "workingDirectory", profile.WorkingDirectory);
        if (profile.ApplicationUrl is not null) Set(values, "applicationUrl", profile.ApplicationUrl);
        if (profile.Environment.Count == 0)
        {
            values.Remove("environmentVariables");
        }
        else
        {
            var variables = new System.Text.Json.Nodes.JsonObject();
            foreach (var (name, value) in profile.Environment)
                variables[name] = value;
            values["environmentVariables"] = variables;
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings() + System.Environment.NewLine;
    }

    // A text of a profile set, or taken out when there's nothing to it
    private static void Set(System.Text.Json.Nodes.JsonObject values, string name, string? value)
    {
        if (value is { Length: > 0 })
            values[name] = value;
        else
            values.Remove(name);
    }

    /// <summary>Environment variables as one line, NAME=value each, a semicolon between them, the way the Properties panel shows them.</summary>
    public static string EnvironmentLine(IReadOnlyDictionary<string, string> environment) =>
        string.Join("; ", environment.Select(pair => $"{pair.Key}={pair.Value}"));

    /// <summary>The line read back into environment variables, a piece without an = or a name left out.</summary>
    public static Dictionary<string, string> EnvironmentOf(string line)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string piece in line.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equals = piece.IndexOf('=');
            if (equals > 0)
                environment[piece[..equals].Trim()] = piece[(equals + 1)..].Trim();
        }

        return environment;
    }
}
