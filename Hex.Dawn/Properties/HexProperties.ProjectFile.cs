using Hex.Core.Projects;

using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Dawn;

// A project file's own rows, Visual Studio's project Properties page made small. Its target framework, output type,
// assembly name, root namespace and nullable as the SDK evaluates them (so what a Directory.Build.props sets shows, and
// says where from), each written back into the .csproj with ProjectFileEdits when it's changed. Then its launch profile,
// the one F5 and Ctrl+F5 start it with, and that profile's arguments, working directory, URLs and environment, written into
// Properties/launchSettings.json, which is made the first time something's set the way Visual Studio's Debug page does.
internal sealed partial class HexProperties
{
    private const string FRAMEWORK = "target_framework", OUTPUT_TYPE = "output_type", ASSEMBLY = "assembly_name", ROOT_NAMESPACE = "root_namespace", NULLABLE = "nullable";
    private const string PROFILE = "launch_profile", ARGUMENTS = "arguments", WORKING = "working_directory", ENVIRONMENT = "environment", APPLICATION_URLS = "application_urls";

    // The properties of the project the rows are, by the row, and the MSBuild property each one is
    private static readonly Dictionary<string, string> ProjectProperties = new(StringComparer.Ordinal)
    {
        [FRAMEWORK] = "TargetFramework",
        [OUTPUT_TYPE] = "OutputType",
        [ASSEMBLY] = "AssemblyName",
        [ROOT_NAMESPACE] = "RootNamespace",
        [NULLABLE] = "Nullable"
    };

    // Whether the rows are a project file's own rather than the files of a project
    private bool projectFileShown;

    // What's waiting to be written into the project while the last change is, the newest of each property
    private readonly Dictionary<string, string?> projectWaiting = new(StringComparer.Ordinal);

    /// <summary>Whether the rows are a project file's own, for a test.</summary>
    internal bool ShowsProjectFile => projectFileShown;

    /// <summary>
    /// A project file's own rows, when what's shown is one SDK project file, put first the way Visual Studio's project
    /// Properties has nothing else. Whether it was.
    /// </summary>
    private bool ProjectFileRows(List<UISetting> all)
    {
        var rows = new List<UISetting>();
        projectFileShown = false;
        if (shown.Count != 1 || !File.Exists(shown[0]) || !DotnetProjects.IsSdkProject(shown[0]))
            return false;

        projectFileShown = true;
        notes.Clear();
        shownProject = shown[0];
        shownEvaluation = null;

        string group = Said("project_settings");
        rows.Add(new UISetting(FRAMEWORK, Said(FRAMEWORK), UIFormKind.Text, Said("target_framework_help"), group, Placeholder: "net11.0"));
        rows.Add(new UISetting(OUTPUT_TYPE, Said(OUTPUT_TYPE), UIFormKind.Choice, Said("output_type_help"), group, Choices: ["Library", "Exe", "WinExe"],
            ChoiceLabels: [Said("output_library"), Said("output_exe"), Said("output_winexe")]));
        rows.Add(new UISetting(ASSEMBLY, Said(ASSEMBLY), UIFormKind.Text, Said("assembly_name_help"), group, Placeholder: Path.GetFileNameWithoutExtension(shownProject)));
        rows.Add(new UISetting(ROOT_NAMESPACE, Said(ROOT_NAMESPACE), UIFormKind.Text, Said("root_namespace_help"), group, Placeholder: Path.GetFileNameWithoutExtension(shownProject)));
        rows.Add(new UISetting(NULLABLE, Said(NULLABLE), UIFormKind.Choice, Said("nullable_help"), group, Choices: ["disable", "enable", "warnings", "annotations"],
            ChoiceLabels: [Said("nullable_disable"), Said("nullable_enable"), Said("nullable_warnings"), Said("nullable_annotations")]));
        foreach (string key in ProjectProperties.Keys)
            values[key] = new StringValue(string.Empty);

        LaunchRows(rows);
        all.InsertRange(0, rows);

        if (Dotnet.Known(shownProject) is { } known)
            ShowProjectFile(known);
        else
            EvaluateProject(shownProject, generation);
        return true;
    }

    /// <summary>The project's properties as the SDK says they are, and where the ones its own file doesn't say come from.</summary>
    private void ShowProjectFile(DotnetEvaluation evaluation)
    {
        shownEvaluation = evaluation;
        string own = string.Empty;
        try
        {
            own = File.ReadAllText(evaluation.Project);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        notes.Clear();
        foreach (var (key, property) in ProjectProperties)
        {
            string value = evaluation.Properties.GetValueOrDefault(property) ?? string.Empty;
            if (key == FRAMEWORK && value.Length == 0)
                value = evaluation.Properties.GetValueOrDefault("TargetFrameworks") ?? string.Empty;
            if (key == NULLABLE && value.Length == 0)
                value = "disable";
            values[key] = new StringValue(value);

            if (ProjectFileEdits.Property(own, property) is null && ImportSaying(evaluation.Folder, property) is { } import)
                notes[key] = import;
            page.Refresh(key);
        }
    }

    // The nearest Directory.Build.props over a project that says a property, where a value the project doesn't say came from
    private static string? ImportSaying(string folder, string property)
    {
        for (string? at = folder; at is not null; at = Path.GetDirectoryName(at))
        {
            string props = Path.Combine(at, "Directory.Build.props");
            try
            {
                if (File.Exists(props) && ProjectFileEdits.Property(File.ReadAllText(props), property) is not null)
                    return props;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    /// <summary>
    /// A property of the project changed on its row, written into the .csproj off the editor's thread, one change at a
    /// time (a name typed slowly is a few), and the rows shown again from what the SDK says after.
    /// </summary>
    private void ChangeProjectFile(string key, string value)
    {
        if (shownProject is not { } project)
            return;

        values[key] = new StringValue(value);
        projectWaiting[key] = value.Trim().Length > 0 ? value.Trim() : null;
        if (!writing)
            WriteProjectFile(project);
    }

    private void WriteProjectFile(string project)
    {
        if (projectWaiting.Count == 0)
            return;

        var (key, value) = projectWaiting.First();
        projectWaiting.Remove(key);

        // A project with several frameworks has them in TargetFrameworks, that's the one written
        string property = ProjectProperties[key];
        if (key == FRAMEWORK && Read(project) is { } text && ProjectFileEdits.Property(text, "TargetFrameworks") is not null && ProjectFileEdits.Property(text, "TargetFramework") is null)
            property = "TargetFrameworks";

        int asked = generation;
        writing = true;
        context.Work.InBackground(Said("writing_project"), () =>
        {
            try
            {
                return (Evaluation: Dotnet.SetProperty(project, property, value).GetAwaiter().GetResult(), Why: (string?)null);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or InvalidOperationException or FormatException or UnauthorizedAccessException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
            {
                return (Evaluation: (DotnetEvaluation?)null, Why: e.Message);
            }
        }, done =>
        {
            writing = false;
            if (done.Evaluation is null)
                context.Notices.Say(Said("not_written", ("project", Path.GetFileName(project)), ("why", FirstLine(done.Why ?? "?"))), error: true);
            else
                context.Notices.Say(Said("property_written", ("property", property), ("value", value ?? Said("property_default")), ("project", Path.GetFileName(project))));

            if (projectWaiting.Count > 0)
                WriteProjectFile(project);
            else if (asked == generation && done.Evaluation is { } evaluation)
                ShowProjectFile(evaluation);
        });
    }

    private static string? Read(string file)
    {
        try
        {
            return File.ReadAllText(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /* The launch profile */

    // launchSettings.json opened, made with the profile the project's name gives it when there isn't one
    private void OpenLaunchSettings(string project)
    {
        string settings = DotnetLaunch.SettingsOf(project);
        try
        {
            if (!File.Exists(settings))
                DotnetLaunch.Save(project, LaunchProfileShown([]));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            context.Notices.Say(Said("not_written", ("project", "launchSettings.json"), ("why", e.Message)), error: true);
            return;
        }

        context.Documents.Open(settings);
    }

    // The launch profile rows, the profile picked (or the one the project's name gives a new launchSettings.json) and what it says
    private void LaunchRows(List<UISetting> rows)
    {
        string project = shownProject!;
        List<DotnetLaunchProfile> profiles = DotnetLaunch.Profiles(project);
        string group = Said("launch_group");
        string[] names = profiles.Count > 0 ? [.. profiles.Select(profile => profile.Name)] : [Path.GetFileNameWithoutExtension(project)];
        rows.Add(new UISetting(PROFILE, Said(PROFILE), UIFormKind.Choice, Said(profiles.Count > 0 ? "launch_profile_help" : "launch_profile_help_none"), group, Choices: names,
            ChoiceLabels: names, Buttons: [Said("open_launch_settings")]));
        rows.Add(new UISetting(ARGUMENTS, Said(ARGUMENTS), UIFormKind.Text, Said("arguments_help"), group, Placeholder: Said("arguments_placeholder")));
        rows.Add(new UISetting(WORKING, Said(WORKING), UIFormKind.Text, Said("working_directory_help"), group, Placeholder: Said("working_directory_placeholder")));
        rows.Add(new UISetting(APPLICATION_URLS, Said(APPLICATION_URLS), UIFormKind.Text, Said("application_urls_help"), group, Placeholder: "http://localhost:5000;https://localhost:5001"));
        rows.Add(new UISetting(ENVIRONMENT, Said(ENVIRONMENT), UIFormKind.Pairs, Said("environment_help"), group, KeyPlaceholder: "NAME", ValuePlaceholder: Said("environment_value")));
        ShowLaunchProfile(profiles);
    }

    // The profile in use, the one picked beside Debug or Release when it's still there, else the first, or a new one
    private DotnetLaunchProfile LaunchProfileShown(List<DotnetLaunchProfile> profiles)
    {
        string project = shownProject!;
        string? picked = context.Workspace.LaunchProfileOf(project);
        return profiles.Find(profile => profile.Name == picked) ?? profiles.FirstOrDefault()
            ?? new DotnetLaunchProfile(Path.GetFileNameWithoutExtension(project), string.Empty, null, new Dictionary<string, string>());
    }

    private void ShowLaunchProfile(List<DotnetLaunchProfile> profiles)
    {
        DotnetLaunchProfile profile = LaunchProfileShown(profiles);
        values[PROFILE] = new StringValue(profile.Name);
        values[ARGUMENTS] = new StringValue(profile.CommandLine);
        values[WORKING] = new StringValue(profile.WorkingDirectory ?? string.Empty);
        values[APPLICATION_URLS] = new StringValue(profile.ApplicationUrl ?? string.Empty);
        values[ENVIRONMENT] = UIFormPairs.Value([.. profile.Environment.Select(pair => (pair.Key, pair.Value))]);
        foreach (string key in (string[])[PROFILE, ARGUMENTS, WORKING, APPLICATION_URLS, ENVIRONMENT])
            page.Refresh(key);
    }

    /// <summary>A launch row changed, the profile picked for F5, or what it says written into launchSettings.json.</summary>
    private void ChangeLaunch(string key, IRuntimeValue value)
    {
        if (shownProject is not { } project)
            return;

        List<DotnetLaunchProfile> profiles = DotnetLaunch.Profiles(project);
        if (key == PROFILE)
        {
            string name = value is StringValue said ? said.Value : string.Empty;
            context.Workspace.PickLaunchProfile(project, profiles.Any(profile => profile.Name == name) ? name : null);
            ShowLaunchProfile(profiles);
            return;
        }

        DotnetLaunchProfile profile = LaunchProfileShown(profiles);
        string text = value is StringValue line ? line.Value.Trim() : string.Empty;
        profile = key switch
        {
            ARGUMENTS => profile with { CommandLine = text },
            WORKING => profile with { WorkingDirectory = text.Length > 0 ? text : null },
            APPLICATION_URLS => profile with { ApplicationUrl = text },
            _ => profile with { Environment = UIFormPairs.Of(value).Where(pair => pair.Key.Trim().Length > 0).ToDictionary(pair => pair.Key.Trim(), pair => pair.Value, StringComparer.Ordinal) }
        };

        bool made = !File.Exists(DotnetLaunch.SettingsOf(project));
        try
        {
            DotnetLaunch.Save(project, profile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            context.Notices.Say(Said("not_written", ("project", "launchSettings.json"), ("why", e.Message)), error: true);
            return;
        }

        context.Notices.Say(Said(made ? "launch_settings_made" : "launch_settings_written", ("profile", profile.Name)));
        ShowLaunchProfile(DotnetLaunch.Profiles(project));

        // The button beside Debug or Release says the profile, a new file has one to say now
        context.Workspace.PickLaunchProfile(project, context.Workspace.LaunchProfileOf(project));
    }
}
