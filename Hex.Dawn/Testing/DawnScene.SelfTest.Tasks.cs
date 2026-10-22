namespace Hex.Dawn;

// The self test of the tasks, a tasks.hor written out of the template and read back, a task run in a terminal tab named
// after it with what it said about a compiler's error in the Problems panel, and the same tab again on the second run.
internal sealed partial class DawnScene
{
    private void TestTasks(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "tasks");
        string file = Path.Combine(folder, HexTasks.TASKS_FILE);
        HexTask? said = null;
        int tabsBefore = 0;

        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Thing.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
            File.WriteAllText(file, HexTasks.TasksTemplate(folder));
        });
        test.Check("tasks: the template of a dotnet folder reads back as its build, test and run", () =>
            tasks.TasksOf(file, folder) is var read && read.Select(task => task.Name).SequenceEqual(["build", "test", "run"])
            && read[0] is { Command: "dotnet build", Group: "build", Default: true } && read[0].WorkingFolder() == Path.GetFullPath(folder));

        // A task that says what a compiler would, written without the shell's echo of it matching (the error is in a file)
        test.Run(() =>
        {
            File.WriteAllText(Path.Combine(folder, "errors.txt"), "C:\\hex\\Program.cs(12,5): error CS1002: ; expected\n");
            File.WriteAllText(file, "let tasks = {\n    say: { command: \"cat errors.txt\", group: \"build\" }\n};\n");
            said = tasks.TasksOf(file, folder).Single();
            ClearTerminalProblems();
            tabsBefore = terminalPanel.Tabs.Count;
            tasks.Run(said);
        });
        test.WaitFor(() => problemList.Any(problem => problem.Text.Contains("CS1002")), 10.0f);
        test.Check("tasks: a task runs in a tab of its own, named after it", () =>
            terminalPanel.Tabs.Count == tabsBefore + 1 && terminalPanel.Current is { Title: "say" } current && Equals(current.Tag, HexTasks.TabOf(said!)));
        test.Check("tasks: and what it says about an error is in the Problems panel", () =>
            problemList.Any(problem => problem.Text == "CS1002: ; expected" && problem.Line == 11));

        test.Run(() => tasks.Run(said!));
        test.Wait();
        test.Check("tasks: run again it is the same tab", () => terminalPanel.Tabs.Count == tabsBefore + 1 && terminalPanel.Current?.Title == "say");
        test.Check("tasks: the Run menu has them", () => AllCommands().Any(command => command.Text.Contains("Run Build Task")));

        test.Run(() =>
        {
            if (terminalPanel.Tabs.FirstOrDefault(tab => Equals(tab.Tag, HexTasks.TabOf(said!))) is { } tab)
                terminalPanel.CloseTab(tab);
            ClearTerminalProblems();
        });
        test.Settle(0.3f);

        TestRunSetup(test);
    }

    /// <summary>Set Up F5, F5 on a D file of a folder with no tasks, what it guesses, what it writes and where it runs it.</summary>
    private void TestRunSetup(HexPointerScript test)
    {
        // The tasks.hor is the one of the workspace's folder the file is in, made by the setup and taken away after
        string folder = Path.Combine(testFolder, "run_setup"), program = Path.Combine(folder, "app.d");
        string root = string.Empty, file = string.Empty;
        test.Run(() =>
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(folder);
            File.WriteAllText(program, "import std.stdio;\n\nvoid main() { writeln(\"hi\"); }\n");
            root = tasks.RootFor(program);
            file = Path.Combine(root, HexTasks.TASKS_FILE);
            if (File.Exists(file))
                File.Delete(file);
            Open(program);
        });
        test.Wait();
        test.Run(RunOrPlay);
        test.Settle(0.3f);
        test.Check("tasks: F5 on a D file with no run task sets F5 up, the program guessed and its lines filled in", () =>
            tasks.SetupWindow is { Visible: true } && tasks.SetupKind.StartsWith("D, this file", StringComparison.Ordinal)
            && tasks.SetupBuild.Contains("app.d\"") && tasks.SetupBuild.Contains("-of=app") && tasks.SetupRunLine.Contains("app"));
        test.Run(() => Shot("run-setup"));
        test.Settle(0.2f);

        // Saved to run in a Dusk of its own, the build first and the run only if it worked, in the shell's own way
        test.Run(() =>
        {
            DuskAsked = null;
            tasks.FillSetup("echo built", "echo ran", inDusk: true);
            tasks.PressSetup(run: true);
        });
        test.Wait();
        test.Check("tasks: saved, the folder's tasks.hor has a build and a run that depends on it, to run in Dusk", () =>
            tasks.SetupWindow is null && tasks.TasksOf(file, root) is var written
            && written.Find(task => task.Name == "build") is { Command: "echo built", Group: "build", Default: true }
            && written.Find(task => task.Name == "run") is { Command: "echo ran", Group: "run", DependsOn: "build", Terminal: HexTask.DUSK });
        test.Check("tasks: and run, Dusk is asked to build it and then run it in the folder", () =>
            DuskAsked is { } asked && PlatformPaths.Same(asked.Folder, root) && asked.CommandLine.StartsWith("echo built", StringComparison.Ordinal) && asked.CommandLine.Contains("echo ran"));
        test.Check("tasks: the run goes on only if the build worked, the way each shell says it", () =>
            tasks.TasksOf(file, root).Find(task => task.Name == "run") is { } run
            && tasks.CommandLineOf(run, "pwsh") == "echo built && echo ran"
            && tasks.CommandLineOf(run, "powershell") == "echo built; if ($?) { echo ran }"
            && tasks.CommandLineOf(run, "cmd") == "echo built && echo ran");

        // Set up again, it starts from what the file says, and the panel here runs it in a tab of its own
        test.Run(() => tasks.SetUpRun(program));
        test.Settle(0.2f);
        test.Check("tasks: set up again it starts from the file's tasks", () => tasks.SetupBuild == "echo built" && tasks.SetupRunLine == "echo ran");
        test.Run(() =>
        {
            tasks.FillSetup("echo built", "echo ran", inDusk: false);
            tasks.PressSetup(run: true);
        });
        test.Settle(0.3f);
        test.Check("tasks: in the panel it's a terminal tab of its own", () => terminalPanel.Current is { Title: "run" });

        test.Run(() =>
        {
            if (terminalPanel.Current is { Title: "run" } tab)
                terminalPanel.CloseTab(tab);
            CloseNow();
            if (File.Exists(file))
                File.Delete(file);
        });
        test.Settle(0.3f);
    }
}
