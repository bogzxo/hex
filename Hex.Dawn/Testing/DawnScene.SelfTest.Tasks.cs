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
    }
}
