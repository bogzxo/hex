using System.Diagnostics.CodeAnalysis;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

// The tasks of a workspace, a tasks.hor in the folder of it (or in a .hex folder in it), one object a task. Run Task in
// the Run menu or the palette asks which, Ctrl+Shift+B runs the build task and F5 on a file that isn't HIDL the run task,
// when there is one (and Set Up F5 when there isn't, HexTasks.Setup). A task runs in a terminal tab named after it, the
// same tab again the next time while its shell is still there, so a build run twice doesn't leave two tabs behind, or
// in a Dusk window of its own when it says so. One that depends on another runs that first, and goes on only if it worked.
internal sealed partial class HexTasks
{
    internal const string TASKS_FILE = "tasks.hor";

    private readonly DawnContext context;

    // what the files said, kept until one of them is written again
    private readonly Dictionary<string, (DateTime Written, List<HexTask> Tasks, List<HexAutomation> Automations)> tasksRead = [];

    /// <summary>How each task's last run ended, by its tab (TabOf), gone while it runs again. For an agent asking how a build went.</summary>
    internal readonly Dictionary<string, int> Exits = new(StringComparer.Ordinal);

    /// <summary>The tasks of the workspace, the Run menu's.</summary>
    public HexTasks(DawnContext with)
    {
        context = with;
        UICommands commands = context.Commands;
        commands.Add("Terminal/tasks", context.Strings.Said("tasks.run_task"), ChooseTask, icon: "tasklist");
        commands.Add("Terminal/tasks", context.Strings.Said("tasks.run_build_task"), RunBuildTask, "Ctrl+Shift+B", "tools", UIKeyUse.BoundWhileTyping);
        commands.Add("Terminal/tasks", context.Strings.Said("tasks.configure_tasks"), ConfigureTasks, icon: "gear");
        commands.Add("Run/tasks", context.Strings.Said("tasks.set_up_run"), () => SetUpRun(), icon: "settings");
        AddSelfBuildCommands();
        AddAutomationCommands();
    }

    /// <summary>The tasks.hor of a workspace's folder, the one in the folder before the one in .hex, null for none.</summary>
    private static string? TasksFileIn(string root)
    {
        string plain = Path.Combine(root, TASKS_FILE), hidden = Path.Combine(root, ".hex", TASKS_FILE);
        return File.Exists(plain) ? plain : File.Exists(hidden) ? hidden : null;
    }

    /// <summary>
    /// Every task of the file on screen's nearest tasks.hor (its project's) first, then every folder of the workspace's,
    /// in the order the files have them.
    /// </summary>
    internal List<HexTask> Tasks()
    {
        var all = new List<HexTask>();
        foreach (var (file, root) in TasksFiles())
            all.AddRange(TasksOf(file, root));

        return all;
    }

    /// <summary>
    /// The tasks.hor files there are with the folder of each, the file on screen's nearest first, then every folder of
    /// the workspace's, then the folder its .hex-workspace is saved in. A workspace of several repositories has the
    /// tasks over all of them there, next to the file that opens them all.
    /// </summary>
    internal List<(string File, string Root)> TasksFiles()
    {
        var files = new List<(string, string)>();
        var read = new HashSet<string>(PlatformPaths.Comparer);
        string? saved = context.Workspace.File is { } workspaceFile ? Path.GetDirectoryName(Path.GetFullPath(workspaceFile)) : null;
        foreach (string? root in (string?[])[TasksFolderFor(context.Documents.Current.Path), .. context.Workspace.Roots, saved])
        {
            if (root is not null && read.Add(Path.GetFullPath(root)) && TasksFileIn(root) is { } file)
                files.Add((file, root));
        }

        return files;
    }

    /// <summary>The tasks of a tasks.hor, read again once it was written to.</summary>
    internal List<HexTask> TasksOf(string file, string root) => Read(file, root).Tasks;

    /// <summary>The automations of a tasks.hor, read again once it was written to.</summary>
    internal List<HexAutomation> AutomationsOf(string file, string root) => Read(file, root).Automations;

    /// <summary>
    /// A tasks.hor run and its tasks and automations read out of it, kept until it's written again. It has to declare one
    /// or the other, a file of only automations is as good as one of only tasks.
    /// </summary>
    // HIDLSerializer reads and writes a task by its members, the trimmer of a published Hex has to be told to keep them
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors, typeof(HexTask))]
    private (List<HexTask> Tasks, List<HexAutomation> Automations) Read(string file, string root)
    {
        DateTime written = File.GetLastWriteTimeUtc(file);
        if (tasksRead.TryGetValue(file, out var read) && read.Written == written)
            return (read.Tasks, read.Automations);

        var tasks = new List<HexTask>();
        var automations = new List<HexAutomation>();
        try
        {
            HIDLRuntime runtime = new() { Output = null };
            var (success, message) = runtime.Evaluate(File.ReadAllText(file));
            IRuntimeValue? saidTasks = runtime.UserScope.Lookup("tasks"), saidAutomations = runtime.UserScope.Lookup("automations");
            if (!success || (saidTasks is not ObjectValue && saidAutomations is not ObjectValue))
                throw new Exception(success ? "it has to declare an object called 'tasks' or one called 'automations'" : message);

            if (saidTasks is ObjectValue taskObject)
            {
                foreach (var (name, value) in taskObject.Properties)
                {
                    var task = new HexTask { Name = name, Root = root };
                    HIDLSerializer.Populate(task, value);
                    if (task.Command.Length > 0)
                        tasks.Add(task);
                }
            }

            if (saidAutomations is ObjectValue automationObject)
                automations.AddRange(HexAutomation.Read(automationObject, root, file));
        }
        catch (Exception e)
        {
            context.Notices.Say(context.Strings.Said("tasks.file_not_read", ("file", Path.GetFileName(file)), ("reason", e.Message)), error: true);
        }

        tasksRead[file] = (written, tasks, automations);
        return (tasks, automations);
    }

    /// <summary>The task of a group run without asking, the one that says it's the default or else the first, null for none.</summary>
    internal HexTask? DefaultTask(string group)
    {
        List<HexTask> tasks = Tasks();
        return tasks.FirstOrDefault(task => task.Default && task.Group == group) ?? tasks.FirstOrDefault(task => task.Group == group);
    }

    /// <summary>Run Task, every task to pick from, or a tasks.hor offered when there are none.</summary>
    private void ChooseTask()
    {
        List<HexTask> tasks = Tasks();
        if (tasks.Count == 0)
        {
            ConfigureTasks();
            return;
        }

        context.Windows.Choose(context.Strings.Said("tasks.choose_title"), context.Strings.Said("tasks.choose_prompt"),
            [.. tasks.Select(task => ($"{task.Name}   {task.Command}", (Action)(() => Run(task))))]);
    }

    /// <summary>
    /// Ctrl+Shift+B, the build task, or Set Up F5 for the file on screen when there is none, which knows a dub project
    /// from a lone file. It wrote a tasks.hor that echoed "nothing to build yet" in a dub project F5 knew how to build.
    /// </summary>
    private void RunBuildTask()
    {
        if (DefaultTask("build") is { } build)
            Run(build);
        else if (context.Documents.Current is { IsText: true, Path: not null })
            SetUpRun();
        else
            ChooseTask();
    }

    /// <summary>What the terminal tab of a task is known by, its folder and its name.</summary>
    internal static string TabOf(HexTask task) => $"task\n{task.Root}\n{task.Name}";

    /// <summary>
    /// Runs a task in its tab, the one it had before while that shell is still going, a new one otherwise, or in a Dusk
    /// window of its own when it says so. The task it depends on first, on the same line, and it only if that worked.
    /// </summary>
    internal void Run(HexTask task)
    {
        // Every file of code with changes saved first, the build reads the disk. F5 built what was there before the line
        // just typed, every IDE saves first
        context.Documents.SaveAll(open => open.IsText && open.Path is not null && (open.Modified || open.CodeEdited));

        string line = CommandLineOf(task, context.Terminals.UsualProfileId);
        Exits.Remove(TabOf(task));
        if (task.Terminal.Equals(HexTask.DUSK, StringComparison.OrdinalIgnoreCase))
        {
            context.Terminals.RunInDusk(task.WorkingFolder(), line);
            context.Notices.Say(context.Strings.Said("tasks.running", ("name", task.Name)));
            return;
        }

        // A build over Hex's own folder has Hex's files moved aside first, it's known how it ends in a tab of the panel
        RunMindingSelf(task, () =>
        {
            context.Terminals.RunInTab(TabOf(task), task.Name, task.WorkingFolder(), line, (exit, said) => Ended(task, exit, said));
            context.Notices.Say(context.Strings.Said("tasks.running", ("name", task.Name)));
        });
    }

    /// <summary>
    /// A task's shell said it was done, how it went said along the bottom. One that failed with errors a compiler said has
    /// them brought up in the Problems panel, the way Visual Studio's Error List comes up after a build that broke. It
    /// said "running build" long after the build was over. What it wrote is looked at for a fix (HexTasks.Fixes).
    /// </summary>
    private void Ended(HexTask task, int exit, string said)
    {
        Exits[TabOf(task)] = exit;
        SelfBuildEnded(task, exit);
        int errors = context.Terminals.TerminalErrors;
        if (exit == 0)
        {
            context.Notices.Say(context.Strings.Said("tasks.ended", ("name", task.Name)));
            return;
        }

        context.Notices.Say(errors > 0
            ? context.Strings.Said("tasks.failed_with", ("name", task.Name), ("errors", HexText.Count("error", errors)))
            : context.Strings.Said("tasks.failed", ("name", task.Name), ("code", exit.ToString())), error: true);
        if (errors > 0)
            context.Panels.ShowProblems();
        OfferFix(task, said);
    }

    /// <summary>
    /// The command line a task runs as in a shell of a profile (its id, pwsh, powershell, cmd, bash), the one it depends on
    /// in front of it when it says one, the second only if the first worked. Windows PowerShell has no double ampersand,
    /// it asks $? instead.
    /// </summary>
    internal string CommandLineOf(HexTask task, string shell)
    {
        if (DependencyOf(task) is not { } first)
            return task.Command;

        // Run from the task's own folder, the one before it may run somewhere else
        string before = first.Folder == task.Folder ? first.Command : $"{ChangeFolder(first.WorkingFolder(), shell)} {Then(shell)} {first.Command}";
        string after = first.Folder == task.Folder ? task.Command : $"{ChangeFolder(task.WorkingFolder(), shell)} {Then(shell)} {task.Command}";
        return Chain(shell, before, after);
    }

    /// <summary>Two command lines, the second only if the first worked, in the shell of a profile.</summary>
    internal static string Chain(string shell, string first, string then) =>
        shell.Equals("powershell", StringComparison.OrdinalIgnoreCase) ? $"{first}; if ($?) {{ {then} }}" : $"{first} && {then}";

    private static string Then(string shell) => shell.Equals("powershell", StringComparison.OrdinalIgnoreCase) ? ";" : "&&";

    private static string ChangeFolder(string folder, string shell) =>
        shell.Equals("cmd", StringComparison.OrdinalIgnoreCase) ? $"cd /d \"{folder}\"" : $"cd \"{folder}\"";

    /// <summary>Configure Tasks, the workspace's tasks.hor opened, written first with a guess at what it wants when there isn't one.</summary>
    private void ConfigureTasks()
    {
        string root = context.Workspace.Roots.FirstOrDefault() ?? System.Environment.CurrentDirectory;
        string file = TasksFileIn(root) ?? Path.Combine(root, TASKS_FILE);
        if (!File.Exists(file))
        {
            try
            {
                File.WriteAllText(file, TasksTemplate(root));
            }
            catch (Exception e)
            {
                context.Notices.Say(context.Strings.Said("tasks.file_not_written", ("file", TASKS_FILE), ("reason", e.Message)), error: true);
                return;
            }
        }

        context.Documents.Open(file);
    }

    /// <summary>
    /// A tasks.hor to start from, dotnet's when the folder has a solution or a project (built, tested, and its startup
    /// project run after the build), a stand-in otherwise.
    /// </summary>
    // HIDLSerializer reads and writes a task by its members, the trimmer of a published Hex has to be told to keep them
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors, typeof(HexTask))]
    internal static string TasksTemplate(string root)
    {
        // The solution (or the project) the folder builds, one a level or two down too, and its startup project to run
        string? target = Hex.Core.Projects.DotnetSolutions.BuildTargetIn(root)
            ?? Directory.EnumerateFiles(root, "*.sln").Concat(Directory.EnumerateFiles(root, "*.slnx")).Concat(Directory.EnumerateFiles(root, "*.csproj")).FirstOrDefault();
        var tasks = new HIDLObject();
        if (target is not null)
        {
            string built = Argument(root, target);
            var projects = Hex.Core.Projects.DotnetSolutions.ProjectsOf(target);
            string? startup = Hex.Core.Projects.DotnetSolutions.StartupProject(projects, Path.GetFileNameWithoutExtension(target));
            tasks.Add("build", HIDLSerializer.ToObject(new HexTask { Command = $"dotnet build {built}", Group = "build", Default = true }));
            tasks.Add("test", HIDLSerializer.ToObject(new HexTask { Command = $"dotnet test {built}", Group = "test" }));
            tasks.Add("run", HIDLSerializer.ToObject(startup is null
                ? new HexTask { Command = "dotnet run", Group = "run" }
                : new HexTask { Command = $"dotnet run --project {Argument(root, startup)} --no-build", Group = "run", DependsOn = "build" }));
        }
        else
        {
            tasks.Add("build", HIDLSerializer.ToObject(new HexTask { Command = "echo nothing to build yet", Group = "build", Default = true }));
        }

        return new HIDLDocument()
            .Comment("The tasks of this folder, each a command line Dawn runs in a terminal tab of its own. Run Task in the Run menu")
            .Comment("picks one, Ctrl+Shift+B runs the build task and F5 on a file that isn't HIDL the run task. What a compiler")
            .Comment("says in one goes in the Problems panel.")
            .Let("tasks", tasks)
            .Write();
    }

    // A file of the folder as a command line run there names it, from the folder with forward slashes, quoted with a space
    private static string Argument(string root, string file)
    {
        string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        return relative.Contains(' ') ? $"\"{relative}\"" : relative;
    }
}
