using Hex.Core.Projects;

namespace Hex.Dawn;

/// <summary>
/// One of what the arrow beside Run offers to run, what it is (null for the file on screen), what the button says for it
/// and a line about it, and the task or the project it is.
/// </summary>
internal sealed record HexRunChoice(HexRunTarget? Target, string Label, string Detail, HexTask? Task = null, string? Project = null);

// What F5 and the Run button run. The arrow beside the button offers every run task of the workspace's tasks.hor files,
// every .NET project of its folders that makes a program and isn't run by one of those tasks already, and the file on
// screen, which is what F5 always ran. What's picked is kept with the workspace (HexWorkspace.RunTarget).
internal sealed partial class HexTasks
{
    // How far down a workspace's folder its projects are looked for
    private const int PROJECT_DEPTH = 3;

    /// <summary>Everything the arrow offers, the file on screen first.</summary>
    internal List<HexRunChoice> RunChoices()
    {
        var choices = new List<HexRunChoice> { new(null, context.Strings.Said("run_target.current_file"), context.Strings.Said("run_target.current_file_detail")) };
        var runTasks = new List<HexTask>();
        foreach (var (file, root) in TasksFiles())
        {
            foreach (HexTask task in TasksOf(file, root).Where(task => task.Group == "run"))
            {
                runTasks.Add(task);
                choices.Add(new HexRunChoice(new HexRunTarget(HexRunTarget.TASK, root, task.Name), TaskLabel(task), $"{context.Workspace.Relative(file)}, {task.Command}", task));
            }
        }

        foreach (string project in Programs())
        {
            if (!runTasks.Any(task => Runs(task, project)))
                choices.Add(new HexRunChoice(new HexRunTarget(HexRunTarget.PROJECT, project, Path.GetFileNameWithoutExtension(project)), Path.GetFileNameWithoutExtension(project), context.Workspace.Relative(project), Project: project));
        }

        return choices;
    }

    /// <summary>
    /// What's picked to run as it is now, null for the file on screen, or for a pick whose task or project isn't there
    /// any more (it was taken out of its tasks.hor), which is the file on screen again until something else is picked.
    /// </summary>
    internal HexRunChoice? ChosenRun()
    {
        switch (context.Workspace.RunTarget)
        {
            case { Kind: HexRunTarget.TASK } target when TasksFileIn(target.Path) is { } file && TasksOf(file, target.Path).Find(task => task.Name == target.Name) is { } task:
                return new HexRunChoice(target, TaskLabel(task), $"{context.Workspace.Relative(file)}, {task.Command}", task);
            case { Kind: HexRunTarget.PROJECT } target when File.Exists(target.Path):
                return new HexRunChoice(target, target.Name, context.Workspace.Relative(target.Path), Project: target.Path);
            default:
                return null;
        }
    }

    /// <summary>The task a project is run with when nothing runs it already, dotnet run in its folder, which builds it first.</summary>
    internal static HexTask ProjectTask(string project) => new()
    {
        Name = Path.GetFileNameWithoutExtension(project),
        Root = Path.GetDirectoryName(Path.GetFullPath(project))!,
        Command = $"dotnet run --project \"{Path.GetFileName(project)}\"",
        Group = "run"
    };

    // What the button says for a task, the folder's name for a task that's only called run, the task's own name otherwise
    private static string TaskLabel(HexTask task) =>
        task.Name == "run" && Path.GetFileName(Path.GetFullPath(task.Root).TrimEnd('\\', '/')) is { Length: > 0 } folder ? folder : task.Name;

    /// <summary>
    /// Whether a run task runs a project already, it names the project's file, or it's a plain dotnet run in the project's
    /// own folder.
    /// </summary>
    internal static bool Runs(HexTask task, string project)
    {
        string name = Path.GetFileName(project);
        if (task.Command.Contains(name, StringComparison.OrdinalIgnoreCase))
            return true;

        return task.Command.Contains("dotnet run", StringComparison.OrdinalIgnoreCase) && !task.Command.Contains("--project", StringComparison.OrdinalIgnoreCase)
            && PlatformPaths.Same(task.WorkingFolder(), Path.GetDirectoryName(Path.GetFullPath(project))!);
    }

    /// <summary>The .NET projects of the workspace's folders that make a program and aren't tests, each once.</summary>
    private List<string> Programs()
    {
        var found = new List<string>();
        var seen = new HashSet<string>(PlatformPaths.Comparer);
        foreach (string root in context.Workspace.Roots)
        {
            IEnumerable<string> projects = DotnetSolutions.BuildTargetIn(root) is { } target ? DotnetSolutions.ProjectsOf(target) : [];
            foreach (string project in projects.Concat(DotnetSolutions.ProjectsUnder(root, PROJECT_DEPTH)))
            {
                if (File.Exists(project) && seen.Add(Path.GetFullPath(project)) && DotnetSolutions.IsProgram(project) && !DotnetSolutions.IsTests(project))
                    found.Add(Path.GetFullPath(project));
            }
        }

        return found;
    }
}
