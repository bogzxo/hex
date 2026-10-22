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
    private readonly Dictionary<string, (DateTime Written, List<HexTask> Tasks)> tasksRead = [];

    /// <summary>The tasks of the workspace, the Run menu's.</summary>
    public HexTasks(DawnContext with)
    {
        context = with;
        UICommands commands = context.Commands;
        commands.Add("Run/tasks", context.Strings.Said("tasks.run_task"), ChooseTask, icon: "tasklist");
        commands.Add("Run/tasks", context.Strings.Said("tasks.run_build_task"), RunBuildTask, "Ctrl+Shift+B", "tools", UIKeyUse.BoundWhileTyping);
        commands.Add("Run/tasks", context.Strings.Said("tasks.configure_tasks"), ConfigureTasks, icon: "gear");
        commands.Add("Run/tasks", context.Strings.Said("tasks.set_up_run"), () => SetUpRun(), icon: "debug-alt");
    }

    /// <summary>The tasks.hor of a workspace's folder, the one in the folder before the one in .hex, null for none.</summary>
    private static string? TasksFileIn(string root)
    {
        string plain = Path.Combine(root, TASKS_FILE), hidden = Path.Combine(root, ".hex", TASKS_FILE);
        return File.Exists(plain) ? plain : File.Exists(hidden) ? hidden : null;
    }

    /// <summary>Every task of every folder of the workspace, in the order the files have them.</summary>
    private List<HexTask> Tasks()
    {
        var all = new List<HexTask>();
        foreach (string root in context.Workspace.Roots)
        {
            if (TasksFileIn(root) is { } file)
                all.AddRange(TasksOf(file, root));
        }

        return all;
    }

    // HIDLSerializer reads and writes a task by its members, the trimmer of a published Hex has to be told to keep them
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors, typeof(HexTask))]
    internal List<HexTask> TasksOf(string file, string root)
    {
        DateTime written = File.GetLastWriteTimeUtc(file);
        if (tasksRead.TryGetValue(file, out var read) && read.Written == written)
            return read.Tasks;

        var tasks = new List<HexTask>();
        try
        {
            HIDLRuntime runtime = new() { Output = null };
            var (success, message) = runtime.Evaluate(File.ReadAllText(file));
            if (!success || runtime.UserScope.Lookup("tasks") is not ObjectValue said)
                throw new Exception(success ? "it has to declare an object called 'tasks'" : message);

            foreach (var (name, value) in said.Properties)
            {
                var task = new HexTask { Name = name, Root = root };
                HIDLSerializer.Populate(task, value);
                if (task.Command.Length > 0)
                    tasks.Add(task);
            }
        }
        catch (Exception e)
        {
            context.Notices.Say(context.Strings.Said("tasks.file_not_read", ("file", Path.GetFileName(file)), ("reason", e.Message)), error: true);
        }

        tasksRead[file] = (written, tasks);
        return tasks;
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

    /// <summary>Ctrl+Shift+B, the build task, or the list when there is none.</summary>
    private void RunBuildTask()
    {
        if (DefaultTask("build") is { } build)
            Run(build);
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
        string line = CommandLineOf(task, context.Terminals.UsualProfileId);
        if (task.Terminal.Equals(HexTask.DUSK, StringComparison.OrdinalIgnoreCase))
            context.Terminals.RunInDusk(task.WorkingFolder(), line);
        else
            context.Terminals.RunInTab(TabOf(task), task.Name, task.WorkingFolder(), line);
        context.Notices.Say(context.Strings.Said("tasks.running", ("name", task.Name)));
    }

    /// <summary>
    /// The command line a task runs as in a shell of a profile (its id, pwsh, powershell, cmd, bash), the one it depends on
    /// in front of it when it says one, the second only if the first worked. Windows PowerShell has no double ampersand,
    /// it asks $? instead.
    /// </summary>
    internal string CommandLineOf(HexTask task, string shell)
    {
        if (task.DependsOn.Length == 0 || task.DependsOn == task.Name || TasksFileIn(task.Root) is not { } file
            || TasksOf(file, task.Root).FirstOrDefault(other => other.Name == task.DependsOn) is not { } first)
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

    /// <summary>A tasks.hor to start from, dotnet's when the folder has a project or a solution in it, a stand-in otherwise.</summary>
    // HIDLSerializer reads and writes a task by its members, the trimmer of a published Hex has to be told to keep them
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors, typeof(HexTask))]
    internal static string TasksTemplate(string root)
    {
        bool dotnet = Directory.EnumerateFiles(root, "*.sln").Any() || Directory.EnumerateFiles(root, "*.csproj").Any();
        var tasks = new HIDLObject();
        if (dotnet)
        {
            tasks.Add("build", HIDLSerializer.ToObject(new HexTask { Command = "dotnet build", Group = "build", Default = true }));
            tasks.Add("test", HIDLSerializer.ToObject(new HexTask { Command = "dotnet test", Group = "test" }));
            tasks.Add("run", HIDLSerializer.ToObject(new HexTask { Command = "dotnet run", Group = "run" }));
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
}
