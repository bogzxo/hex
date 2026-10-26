using Hex.Core.Projects;

using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The project rows of the properties of files, for a file (or a folder, or several) in an MSBuild SDK style project,
// Visual Studio's Build Action and Copy to Output Directory. What the project says is asked of the SDK itself
// (DotnetProjects, off the editor's thread and kept till the project changes), so its globs, its SDK's defaults and its
// Directory.Build.props all count, and a change is written into the .csproj the way Visual Studio writes it and read
// back from the SDK again. A setting that comes from an import says which, the import itself is never written.
internal sealed partial class HexProperties
{
    private const string PROJECT = "project", BUILD_ACTION = "build_action", COPY = "copy_to_output", OUTPUT = "output";

    // What a choice says while the files picked don't agree
    private const string MIXED = "mixed";

    private static DotnetProjects Dotnet => DotnetProjects.Shared;

    // The project of the files shown and what it evaluated to last, null for none
    private string? shownProject;
    private DotnetEvaluation? shownEvaluation;

    // The files of the project the rows change, every file under a folder that's picked
    private List<string> projectFiles = [];

    // Whether a change is being written, the rows wait for it
    private bool writing;

    /// <summary>The project the rows are of, for a test.</summary>
    internal string? ShownProject => shownProject;

    /// <summary>Whether the project is being evaluated or written, for a test to wait on.</summary>
    internal bool ProjectBusy => writing || shownProject is not null && shownEvaluation is null;

    private void ProjectRows(List<UISetting> rows)
    {
        notes.Clear();
        shownEvaluation = null;
        shownProject = shown.Count > 0 ? DotnetProjects.ProjectOf(shown[0]) : null;
        if (shownProject is null || shown.Any(path => !PlatformPaths.Same(DotnetProjects.ProjectOf(path) ?? string.Empty, shownProject)))
        {
            shownProject = null;
            return;
        }

        string group = Said("project_group");
        rows.Add(new UISetting(PROJECT, Said(PROJECT), UIFormKind.Text, Said("project_help"), group, Buttons: [Said("open_project")], ReadOnly: true));
        values[PROJECT] = new StringValue(context.Workspace.Relative(shownProject));

        // The kinds are what the project offers, the common ones until it's been asked
        DotnetEvaluation? known = Dotnet.Known(shownProject);
        string[] kinds = [.. (known?.ItemTypes ?? [.. DotnetEvaluation.CommonItemTypes]), MIXED];
        rows.Add(new UISetting(BUILD_ACTION, Said(BUILD_ACTION), UIFormKind.Choice, Said("build_action_help"), group, Choices: kinds,
            ChoiceLabels: [.. kinds.Select(kind => kind == MIXED ? Said("mixed") : kind)]));
        rows.Add(new UISetting(COPY, Said(COPY), UIFormKind.Choice, Said("copy_help"), group,
            Choices: [DotnetEvaluation.NEVER, DotnetEvaluation.ALWAYS, DotnetEvaluation.IF_NEWER, MIXED],
            ChoiceLabels: [Said("copy_never"), Said("copy_always"), Said("copy_if_newer"), Said("mixed")],
            ChoiceHelp: [Said("copy_never_help"), Said("copy_always_help"), Said("copy_if_newer_help"), string.Empty]));
        rows.Add(ReadOnly(OUTPUT, group, "output_help"));

        if (known is not null)
            ShowEvaluation(known);
        else
        {
            values[BUILD_ACTION] = values[COPY] = new StringValue(MIXED);
            values[OUTPUT] = new StringValue(Said("evaluating"));
            EvaluateProject(shownProject, generation);
        }
    }

    /// <summary>The project asked of the SDK off the editor's thread, its rows shown once it answers.</summary>
    private void EvaluateProject(string project, int asked)
    {
        context.Work.InBackground(Said("evaluating"), () =>
        {
            try
            {
                return (Evaluation: Dotnet.Evaluate(project).GetAwaiter().GetResult(), Why: (string?)null);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or InvalidOperationException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                return (Evaluation: (DotnetEvaluation?)null, Why: e.Message);
            }
        }, done =>
        {
            if (asked != generation || !PlatformPaths.Same(project, shownProject ?? string.Empty))
                return;

            if (done.Evaluation is null)
            {
                values[OUTPUT] = new StringValue(Said("not_evaluated", ("why", FirstLine(done.Why ?? "?"))));
                page.Refresh(OUTPUT);
                return;
            }

            // The project offers kinds of its own (a WPF project's Page), the rows are made with them
            bool newKinds = shownEvaluation is null && !done.Evaluation.ItemTypes.SequenceEqual(DotnetEvaluation.CommonItemTypes);
            ShowEvaluation(done.Evaluation);
            if (newKinds)
            {
                rowsStale = true;
                context.Window.RefreshRight();
            }
        }, quiet: true);
    }

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? text;

    // The rows as the project says the files are, one value when they agree and Mixed when they don't
    private void ShowEvaluation(DotnetEvaluation evaluation)
    {
        shownEvaluation = evaluation;
        projectFiles = FilesOf(evaluation);
        var items = projectFiles.Select(evaluation.BuildItemOf).ToList();

        string[] kinds = [.. items.Select(item => item?.Type ?? string.Empty).Distinct(StringComparer.OrdinalIgnoreCase)];
        string[] copies = [.. items.Select(item => item?.CopyToOutput ?? DotnetEvaluation.NEVER).Distinct()];
        values[BUILD_ACTION] = new StringValue(kinds.Length == 1 && kinds[0].Length > 0 ? kinds[0] : MIXED);
        values[COPY] = new StringValue(copies.Length == 1 ? copies[0] : MIXED);

        // Where a copied file lands, or the project's output when nothing's copied
        string output = evaluation.OutputFolder;
        values[OUTPUT] = new StringValue(projectFiles.Count == 1 && items[0] is { } item && item.CopyToOutput != DotnetEvaluation.NEVER && output.Length > 0
            ? Path.Combine(output, item.Metadata.GetValueOrDefault("Link") is { Length: > 0 } link ? link : DotnetProjects.RelativeTo(evaluation.Project, projectFiles[0]))
            : output.Length > 0 ? output : Said("no_output"));

        notes.Clear();
        if (projectFiles.Count == 1)
        {
            if (DotnetProjects.ImportedFrom(evaluation, projectFiles[0], ProjectFileEdits.COPY_TO_OUTPUT) is { } from)
                notes[COPY] = from;
            if (items[0]?.DefinedIn is { Length: > 0 } defined && !PlatformPaths.Same(defined, evaluation.Project) && !defined.Contains(Path.DirectorySeparatorChar + "Sdks" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                notes[BUILD_ACTION] = defined;
        }

        page.Refresh(BUILD_ACTION);
        page.Refresh(COPY);
        page.Refresh(OUTPUT);
    }

    // The files the project rows are of, the ones picked and every file of the project under a folder that's picked
    private List<string> FilesOf(DotnetEvaluation evaluation)
    {
        var files = new List<string>();
        foreach (string path in shown)
        {
            if (File.Exists(path))
                files.Add(path);
            else if (Directory.Exists(path))
                files.AddRange(evaluation.Items.Select(item => item.FullPath).Where(file => PlatformPaths.IsUnder(file, path) && File.Exists(file)));
        }

        return [.. files.Distinct(PlatformPaths.Comparer)];
    }

    /// <summary>A Build Action or a Copy to Output Directory picked, written into the project at once off the editor's thread.</summary>
    private void ChangeProject(string key, string value)
    {
        if (shownProject is not { } project || value == MIXED || projectFiles.Count == 0)
            return;

        string? action = key == BUILD_ACTION ? value : null, copy = key == COPY ? value : null;
        List<string> files = [.. projectFiles];
        int asked = generation;
        writing = true;
        values[key] = new StringValue(value);
        context.Work.InBackground(Said("writing_project"), () =>
        {
            try
            {
                return (Result: Dotnet.Apply(project, files, action, copy).GetAwaiter().GetResult(), Why: (string?)null);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or InvalidOperationException or FormatException or UnauthorizedAccessException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
            {
                return (Result: (DotnetSettingsResult?)null, Why: e.Message);
            }
        }, done =>
        {
            writing = false;
            if (done.Result is not { } result)
            {
                context.Notices.Say(Said("not_written", ("project", Path.GetFileName(project)), ("why", FirstLine(done.Why ?? "?"))), error: true);
            }
            else if (result.NotTaken.Count > 0)
            {
                string from = DotnetProjects.ImportedFrom(result.Evaluation, result.NotTaken[0], ProjectFileEdits.COPY_TO_OUTPUT) is { } import ? Path.GetFileName(import) : Said("an_import");
                context.Notices.Notify(Said("not_taken", ("count", HexText.Count("file", result.NotTaken.Count)), ("file", from)), error: true);
            }
            else
            {
                context.Notices.Say(Said(result.Written ? "written" : "already", ("project", Path.GetFileName(project)), ("count", HexText.Count("file", files.Count))));
            }

            if (asked == generation && done.Result is { } shownResult)
                ShowEvaluation(shownResult.Evaluation);
        });
    }
}
