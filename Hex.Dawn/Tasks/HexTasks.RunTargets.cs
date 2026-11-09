using Hex.Core.Projects;

namespace Hex.Dawn;

/// <summary>
/// One of what the arrow beside Run offers to run, what it is (null for the file on screen), what the button says for it
/// and a line about it, and the task or the project it is.
/// </summary>
internal sealed record HexRunChoice(HexRunTarget? Target, string Label, string Detail, HexTask? Task = null, string? Project = null);

// What F5 and the Run button run. The arrow beside the button offers every run task of the workspace's tasks.hor files,
// every .NET project of its folders that makes a program, the solution's startup projects the way Visual Studio's list
// has them (a project a task runs too, it was left out and F5 had only the task to go by), and the file on screen, which
// is what F5 always ran. What's picked is kept with the workspace (HexWorkspace.RunTarget).
internal sealed partial class HexTasks
{
    // How far down a workspace's folder its projects are looked for
    private const int PROJECT_DEPTH = 3;

    // The tabs of the run tasks started that haven't said they're done, what Shift+F5 stops
    private readonly HashSet<string> runsGoing = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether a run task is going in a tab of the panel, a program run without the debugger, by Ctrl+F5 or by F5 running
    /// a task. One whose tab was closed is let go of.
    /// </summary>
    internal bool RunGoing
    {
        get
        {
            if (runsGoing.Count == 0)
                return false;

            foreach (string tab in runsGoing.ToArray())
            {
                if (!context.Terminals.TabRunning(tab))
                    runsGoing.Remove(tab);
            }

            return runsGoing.Count > 0;
        }
    }

    /// <summary>
    /// Shift+F5 with nothing being debugged, every run task that's going stopped with a Ctrl+C in its tab, the way Visual
    /// Studio's Stop ends a program it started without the debugger too. Whether there was one.
    /// </summary>
    internal bool StopRuns()
    {
        if (!RunGoing)
            return false;

        foreach (string tab in runsGoing)
            context.Terminals.InterruptTab(tab);
        // The tab's key ends with the task's name (TabOf)
        context.Notices.Say(context.Strings.Said("tasks.stopping_run", ("name", runsGoing.First().Split('\n')[^1])));
        return true;
    }

    /// <summary>Everything the arrow offers, the file on screen first.</summary>
    internal List<HexRunChoice> RunChoices()
    {
        var choices = new List<HexRunChoice> { new(null, context.Strings.Said("run_target.current_file"), context.Strings.Said("run_target.current_file_detail")) };
        foreach (var (file, root) in TasksFiles())
        {
            foreach (HexTask task in TasksOf(file, root).Where(task => task.Group == "run"))
                choices.Add(new HexRunChoice(new HexRunTarget(HexRunTarget.TASK, root, task.Name), TaskLabel(task), $"{context.Workspace.Relative(file)}, {task.Command}", task));
        }

        foreach (string project in Programs())
            choices.Add(new HexRunChoice(new HexRunTarget(HexRunTarget.PROJECT, project, Path.GetFileNameWithoutExtension(project)), Path.GetFileNameWithoutExtension(project), context.Workspace.Relative(project), Project: project));

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

    /// <summary>
    /// The task a project is run with when nothing runs it already, dotnet run in its folder, which builds it first. In
    /// Release (or whatever else was picked beside the Run button) when it isn't Debug. A launch profile hands dotnet run
    /// its name, dotnet run puts its arguments and its environment on the program itself, and the task runs in the
    /// profile's working directory, which dotnet run leaves alone.
    /// </summary>
    internal static HexTask ProjectTask(string project, string configuration = HexWorkspace.DEBUG, DotnetLaunchProfile? profile = null)
    {
        string full = Path.GetFullPath(project);
        string root = Path.GetDirectoryName(full)!;
        string folder = profile?.WorkingFolder(full) ?? root;
        string target = PlatformPaths.Same(folder, root) ? Path.GetFileName(full) : Path.GetRelativePath(folder, full);
        return new HexTask
        {
            Name = Path.GetFileNameWithoutExtension(project),
            Root = root,
            Folder = PlatformPaths.Same(folder, root) ? "." : folder,
            Command = $"dotnet run --project \"{target}\"{Configured(configuration)}{(profile is null ? string.Empty : $" --launch-profile \"{profile.Name}\"")}",
            Group = "run"
        };
    }

    /// <summary>
    /// A project run without the debugger, Ctrl+F5 and Run on its row, in the configuration and with the launch profile
    /// picked beside the Run button, the same profile F5 debugs it with (HexDebugging).
    /// </summary>
    internal void RunProject(string project) =>
        Run(ProjectTask(project, context.Workspace.Configuration, DotnetLaunch.For(project, context.Workspace.LaunchProfileOf(project))));

    /// <summary>
    /// A dotnet command on a solution or a project from its own folder, build, clean or build again from nothing, the way
    /// Solution Explorer's menus have them, in the configuration picked beside the Run button.
    /// </summary>
    internal static HexTask DotnetTask(string verb, string target, string configuration, string name, bool rebuild = false) => new()
    {
        Name = name,
        Root = Path.GetDirectoryName(Path.GetFullPath(target))!,
        Command = $"dotnet {verb} \"{Path.GetFileName(target)}\" -c {configuration}{(rebuild ? " --no-incremental" : string.Empty)}",
        Group = "build"
    };

    // What a dotnet command says for a configuration, nothing for Debug, what it builds anyway
    private static string Configured(string configuration) =>
        configuration.Equals(HexWorkspace.DEBUG, StringComparison.OrdinalIgnoreCase) ? string.Empty : $" -c {configuration}";

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
