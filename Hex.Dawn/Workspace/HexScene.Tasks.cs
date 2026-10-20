using System.Diagnostics.CodeAnalysis;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

using Key = Silk.NET.Input.Key;

namespace Hex.Dawn;

/// <summary>
/// One task of a workspace's tasks.hor, a command line run in a terminal tab of its own, VS Code's tasks.json without the
/// json. What a compiler says in it goes in the Problems panel the way it does from any terminal, the host's matchers see
/// to that.
/// </summary>
internal sealed class HexTask
{
    [HidlComment("The command line, run by the usual shell.")]
    public string Command { get; set; } = string.Empty;

    [HidlComment("Where it runs, from the workspace's folder. The folder itself when it doesn't say.")]
    public string Folder { get; set; } = ".";

    [HidlComment("build, test or run. Ctrl+Shift+B runs the build one, F5 on a file that isn't HIDL the run one.")]
    public string Group { get; set; } = string.Empty;

    [HidlComment("Whether it is the one of its group run without asking, the first of a group is when none says.")]
    public bool Default { get; set; }

    /// <summary>What it is called, its key in the file.</summary>
    [HidlIgnore]
    public string Name { get; set; } = string.Empty;

    /// <summary>The folder of the tasks.hor it came out of.</summary>
    [HidlIgnore]
    public string Root { get; set; } = string.Empty;

    /// <summary>Where it runs, the folder made whole.</summary>
    public string WorkingFolder() => Path.GetFullPath(Path.Combine(Root, Folder));
}

// The tasks of a workspace, a tasks.hor in the folder of it (or in a .hex folder in it), one object a task. Run Task in
// the Run menu or the palette asks which, Ctrl+Shift+B runs the build task and F5 on a file that isn't HIDL the run task,
// when there is one. A task runs in a terminal tab named after it, the same tab again the next time while its shell is
// still there, so a build run twice doesn't leave two tabs behind.
internal sealed partial class HexScene
{
    private const string TASKS_FILE = "tasks.hor";

    // what the files said, kept until one of them is written again
    private readonly Dictionary<string, (DateTime Written, List<HexTask> Tasks)> tasksRead = [];

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
        foreach (string root in WorkspaceRoots())
        {
            if (TasksFileIn(root) is { } file)
                all.AddRange(TasksOf(file, root));
        }

        return all;
    }

    // HIDLSerializer reads and writes a task by its members, the trimmer of a published Hex has to be told to keep them
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors, typeof(HexTask))]
    private List<HexTask> TasksOf(string file, string root)
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
            Say($"{Path.GetFileName(file)} wasn't read, {e.Message}", error: true);
        }

        tasksRead[file] = (written, tasks);
        return tasks;
    }

    /// <summary>The task of a group run without asking, the one that says it's the default or else the first, null for none.</summary>
    private HexTask? DefaultTask(string group)
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

        ChooseFrom("Run task", "The tasks of the workspace's tasks.hor, each runs in a terminal of its own",
            [.. tasks.Select(task => ($"{task.Name}   {task.Command}", (Action)(() => RunTask(task))))]);
    }

    /// <summary>Ctrl+Shift+B, the build task, or the list when there is none.</summary>
    private void RunBuildTask()
    {
        if (DefaultTask("build") is { } build)
            RunTask(build);
        else
            ChooseTask();
    }

    /// <summary>Runs a task in its tab, the one it had before while that shell is still going, a new one otherwise.</summary>
    private void RunTask(HexTask task)
    {
        ShowPanel(CONSOLE_PAGE);
        string line = task.Command + "\r";

        TerminalTab? tab = terminalPanel.Tabs.FirstOrDefault(open => open.Tag is HexTask was && was.Name == task.Name && was.Root == task.Root);
        if (tab?.Views.FirstOrDefault() is { IsRunning: true } view)
        {
            terminalPanel.Select(tab);
            terminalPanel.FocusPane(view);
            view.Send(line);
        }
        else
        {
            if (tab is not null)
                terminalPanel.CloseTab(tab);
            tab = terminalPanel.NewTab(terminals.UsualProfile, task.WorkingFolder());
            tab.Tag = task;
            tab.Renamed = task.Name;
            tab.Views.FirstOrDefault()?.SendWhenReady(line);
        }

        Say($"running {task.Name}");
    }

    /// <summary>Configure Tasks, the workspace's tasks.hor opened, written first with a guess at what it wants when there isn't one.</summary>
    private void ConfigureTasks()
    {
        string root = WorkspaceRoots().FirstOrDefault() ?? System.Environment.CurrentDirectory;
        string file = TasksFileIn(root) ?? Path.Combine(root, TASKS_FILE);
        if (!File.Exists(file))
        {
            try
            {
                File.WriteAllText(file, TasksTemplate(root));
            }
            catch (Exception e)
            {
                Say($"{TASKS_FILE} couldn't be written, {e.Message}", error: true);
                return;
            }
        }

        Open(file);
    }

    /// <summary>A tasks.hor to start from, dotnet's when the folder has a project or a solution in it, a stand-in otherwise.</summary>
    // HIDLSerializer reads and writes a task by its members, the trimmer of a published Hex has to be told to keep them
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors, typeof(HexTask))]
    private static string TasksTemplate(string root)
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

    private void BuildTasks()
    {
        commands.Add("Run/tasks", "Run Task...", ChooseTask, icon: "tasklist");
        commands.Add("Run/tasks", "Run Build Task", RunBuildTask, "Ctrl+Shift+B", "tools", HexKeyUse.BoundWhileTyping);
        commands.Add("Run/tasks", "Configure Tasks", ConfigureTasks, icon: "gear");
    }
}
