using Hex.Core.Projects;

namespace Hex.Dawn;

// Hex building itself from a task. A build that writes into the folder Hex runs from would fail on its locked exe and
// dlls, so before such a task runs they're moved aside (HexSelfBuild), and once it built a card offers to restart into
// the new build, the same workspace in the new exe. Whether a task writes there is asked of the command line when it
// says -p:OutDir, and of the SDK's evaluation of what it builds otherwise. A build that failed has the old files put back.
internal sealed partial class HexTasks
{
    // What each task moved aside the last time it ran, by its tab, put back if the build didn't write them
    private readonly Dictionary<string, List<string>> movedAside = [];

    /// <summary>Whether a new build of Hex is in its folder, what Restart into the New Build starts.</summary>
    internal bool NewBuildReady { get; private set; }

    /// <summary>Hex closed and started again from its new exe on the same workspace, the scene's own way of closing.</summary>
    internal Action<string, IReadOnlyList<string>>? RestartInto { get; set; }

    /// <summary>The commands of building Hex itself, made with the rest of the tasks'.</summary>
    private void AddSelfBuildCommands()
    {
        context.Commands.Add("Run/tasks", context.Strings.Said("tasks.restart_new_build"), RestartIntoNewBuild, icon: "debug-restart")
            .IsEnabled = () => NewBuildReady;
    }

    /// <summary>
    /// Runs a task, first moving Hex's own files aside when its build writes into the folder Hex runs from. That's asked
    /// off the editor's thread when it needs the SDK, and the task runs once it's known.
    /// </summary>
    private void RunMindingSelf(HexTask task, Action run)
    {
        var lines = new List<(string Command, string Folder)> { (task.Command, task.WorkingFolder()) };
        if (DependencyOf(task) is { } first)
            lines.Insert(0, (first.Command, first.WorkingFolder()));

        string running = HexSelfBuild.RunningFolder;
        bool mayWrite = false, surely = false;
        foreach (var (command, folder) in lines)
        {
            foreach (DotnetCommandLine line in DotnetCommandLine.Read(command, folder))
            {
                if (line.OutputFolder is { } output)
                    surely |= HexSelfBuild.IsRunningFolder(output);
                else
                    mayWrite |= PlatformPaths.IsUnder(running, folder) || line.Target is { } target && PlatformPaths.IsUnder(running, Directory.Exists(target) ? target : Path.GetDirectoryName(target)!);
            }
        }

        if (surely || !mayWrite)
        {
            MoveAsideFor(task, surely);
            run();
            return;
        }

        context.Notices.Say(context.Strings.Said("tasks.checking_output", ("name", task.Name)));
        context.Work.InBackground(context.Strings.Said("tasks.checking_output", ("name", task.Name)), () =>
        {
            foreach (var (command, folder) in lines)
            {
                if (HexSelfBuild.OutputFoldersOf(command, folder, DotnetProjects.Shared).GetAwaiter().GetResult().Any(HexSelfBuild.IsRunningFolder))
                    return true;
            }

            return false;
        }, writes =>
        {
            MoveAsideFor(task, writes);
            run();
        }, quiet: true);
    }

    // Hex's locked files moved aside for a task that writes over them, said in the output
    private void MoveAsideFor(HexTask task, bool writesHere)
    {
        movedAside.Remove(TabOf(task));
        if (!writesHere)
            return;

        List<string> moved = HexSelfBuild.MoveAside(HexSelfBuild.RunningFolder);
        movedAside[TabOf(task)] = moved;
        if (moved.Count > 0)
            context.Notices.Note(context.Strings.Said("tasks.moved_aside", ("count", HexText.Count("file", moved.Count)), ("folder", HexSelfBuild.RunningFolder)));
    }

    /// <summary>A task that built over Hex ended, the files it didn't write put back, and a new Hex offered when there is one.</summary>
    private void SelfBuildEnded(HexTask task, int exit)
    {
        if (!movedAside.Remove(TabOf(task), out List<string>? moved))
            return;

        // The exe was moved aside and is there again, the build wrote a new one. Its time is no help, MSBuild copies
        // a file with the time it had in obj, and an incremental build of nothing new is an exe older than this Hex
        bool rebuilt = HexSelfBuild.RunningExe is { } exe && moved.Contains(exe, PlatformPaths.Comparer) && File.Exists(exe);
        int back = HexSelfBuild.PutBack(moved);
        if (back > 0)
            context.Notices.Note(context.Strings.Said("tasks.put_back", ("count", HexText.Count("file", back))));

        if (exit != 0 || !rebuilt)
            return;

        NewBuildReady = true;
        context.Notices.Notify(context.Strings.Said("tasks.new_build_ready"), false,
            (context.Strings.Said("tasks.restart_new_build"), RestartIntoNewBuild));
    }

    /// <summary>Restart into the New Build, the exe in Hex's folder started on the same workspace and this one closed.</summary>
    private void RestartIntoNewBuild()
    {
        if (HexSelfBuild.RunningExe is not { } exe || !File.Exists(exe))
        {
            context.Notices.Say(context.Strings.Said("tasks.no_new_build"), error: true);
            return;
        }

        // A window of its own, a folder handed to the Hex that's running would land back in this one
        var arguments = new List<string> { "--window" };
        if (context.Workspace.Folders.FirstOrDefault() is { } folder)
            arguments.AddRange(["--folder", folder]);
        RestartInto?.Invoke(exe, arguments);
    }
}
