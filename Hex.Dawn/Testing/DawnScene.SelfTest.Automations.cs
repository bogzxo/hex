using Horizon.Core.Threading;

namespace Hex.Dawn;

// The self test of the automations of a tasks.hor. They're in the command palette, one of HIDL runs off the editor's
// thread with a program run and waited for, what it asks of the editor done on the editor's, and its lines on its card.
// One that goes on is stopped from its work, and one of a command line runs in a terminal tab named after it. Then the
// workspace's own Push all, against repositories made for it (DawnScene.SelfTest.PushAll).
internal sealed partial class DawnScene
{
    private const string AUTOMATIONS_FILE = """
        let automations = {
            write_it: { title: "Write it", run: hex => write_it(hex) },
            forever: { title: "Wait forever", run: hex => { while (!hex.cancelled()) hex.sleep(20) } },
            echo: { title: "Echo it", command: "echo automation ran" },
        };

        func write_it(hex) {
            let git = hex.run("git", ["--version"])
            hex.print("git said " + git.output)
            hex.progress(0.5, "writing")
            hex.write("wrote.txt", `{git.exit} {hex.file()} {hex.matches(git.output, "^GIT VERSION")}`)
        }
        """;

    private void TestAutomations(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "automations");
        string file = Path.Combine(folder, HexTasks.TASKS_FILE), note = Path.Combine(folder, "note.txt"), wrote = Path.Combine(folder, "wrote.txt");
        string nested = Path.Combine(folder, "nested"), nestedNote = Path.Combine(nested, "note.txt"), nestedFile = Path.Combine(nested, HexTasks.TASKS_FILE);
        string extraRoot = Path.Combine(testFolder, $"automations-workspace-root-{Guid.NewGuid():N}"), extraFile = Path.Combine(extraRoot, HexTasks.TASKS_FILE);
        string writeKey = string.Empty, foreverKey = string.Empty;
        int tabsBefore = 0;
        bool automationListReused = false;
        bool addedWorkspaceFolder = false, addedExtraRoot = false;

        test.Run(() =>
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(folder);
            File.WriteAllText(note, "a file to have open next to the automations\n");
            File.WriteAllText(file, AUTOMATIONS_FILE);
            Open(note);
        });
        test.Wait();
        test.Check("automations: each is in the command palette, Automation and its title", () =>
            AllCommands() is var all && all.Any(command => command.Text == "Automation: Write it") && all.Any(command => command.Text == "Automation: Echo it")
            && all.Any(command => command.Text == "Automation: Wait forever"));
        test.Check("automations: and Run Automation is in the Terminal menu", () => AllCommands().Any(command => command.Text.Contains("Run Automation")));
        test.Run(() =>
        {
            List<HexAutomation> known = tasks.Automations();
            automationListReused = ReferenceEquals(known, tasks.Automations());
        });
        test.Check("automations: unchanged task files reuse the discovered list", () => automationListReused);

        // Picked from the palette, it runs off the editor's thread and its hex calls come back to it
        test.Run(() =>
        {
            HexAutomation automation = tasks.AutomationsOf(file, folder).Single(automation => automation.Name == "write_it");
            writeKey = HexTasks.AutomationKey(automation);
            AllCommands().Single(command => command.Text == "Automation: Write it").Picked();
        });
        test.WaitFor(() => work.Items.Any(item => item.Key == writeKey && !item.IsActive), 20.0f);
        test.Check("automations: one of HIDL ran to its end as a piece of work", () =>
            work.Items.FirstOrDefault(item => item.Key == writeKey) is { State: WorkState.Succeeded });
        test.Check("automations: hex.run ran a program and waited for it, hex.file and hex.write went through the editor's thread", () =>
            File.Exists(wrote) && File.ReadAllText(wrote) == $"0 {note} true");
        test.Check("automations: what it printed is on its card", () =>
            work.Items.First(item => item.Key == writeKey).Output.Any(line => line.StartsWith("git said git version", StringComparison.Ordinal)));

        // One that goes on until it's stopped, and the editor goes on while it does
        test.Run(() =>
        {
            HexAutomation automation = tasks.AutomationsOf(file, folder).Single(automation => automation.Name == "forever");
            foreverKey = HexTasks.AutomationKey(automation);
            tasks.RunAutomation(automation);
        });
        test.Settle(0.5f);
        test.Check("automations: one that goes on is going, the editor with it", () => work.Items.FirstOrDefault(item => item.Key == foreverKey) is { State: WorkState.Running });
        test.Run(() => work.Items.First(item => item.Key == foreverKey).Cancel());
        test.WaitFor(() => work.Items.Any(item => item.Key == foreverKey && !item.IsActive), 5.0f);
        test.Check("automations: and stopped from its work it stops", () => work.Items.FirstOrDefault(item => item.Key == foreverKey) is { State: WorkState.Cancelled });

        // A command line in a terminal tab of its own, named after it
        test.Run(() =>
        {
            tabsBefore = terminalPanel.Tabs.Count;
            tasks.RunAutomation(tasks.AutomationsOf(file, folder).Single(automation => automation.Name == "echo"));
        });
        test.Settle(0.3f);
        test.Check("automations: one of a command line runs in a terminal tab named after it", () =>
            terminalPanel.Tabs.Count == tabsBefore + 1 && terminalPanel.Current is { Title: "Echo it" });

        test.Run(() =>
        {
            if (terminalPanel.Current is { Title: "Echo it" } tab)
                terminalPanel.CloseTab(tab);
            CloseNow();
        });
        test.Settle(0.3f);

        test.Run(() =>
        {
            Open(note);
            tasks.Automations(); // Warm the current document's sources before changing the file.
            File.WriteAllText(file, File.ReadAllText(file).Replace("title: \"Write it\"", "title: \"Write updated\"", StringComparison.Ordinal));
        });
        test.Settle(1.2f);
        test.Check("automations: editing tasks.hor refreshes its automation titles", () =>
            tasks.Automations().Any(automation => automation.Name == "write_it" && automation.Title == "Write updated"));

        test.Run(() =>
        {
            Directory.CreateDirectory(nested);
            Directory.CreateDirectory(extraRoot);
            File.WriteAllText(nestedNote, "a file below the workspace task file\n");
            File.WriteAllText(extraFile, "let automations = { extra: { title: \"Extra root\", command: \"echo extra\" } };");
            addedWorkspaceFolder = !explorer.workspace.Folders.Contains(folder, PlatformPaths.Comparer);
            if (addedWorkspaceFolder)
                explorer.workspace.Folders.Add(folder);
            addedExtraRoot = !explorer.workspace.Folders.Contains(extraRoot, PlatformPaths.Comparer);
            if (addedExtraRoot)
                explorer.workspace.Folders.Add(extraRoot);
            Open(nestedNote);
        });
        test.Settle(0.3f);
        test.Check("automations: the workspace root task file is kept with a nested document", () =>
            tasks.Automations().Count(automation => automation.File == file) == 3
            && tasks.Automations().Any(automation => automation.File == extraFile));

        test.Run(() => File.WriteAllText(nestedFile, "let automations = { nested: { title: \"Nested\", command: \"echo nested\" } };"));
        test.Check("automations: a nearer tasks.hor is found without dropping the workspace root file", () =>
            tasks.Automations().Count(automation => automation.File == nestedFile) == 1
            && tasks.Automations().Count(automation => automation.File == file) == 3
            && tasks.Automations().Any(automation => automation.File == extraFile));

        test.Run(() => File.Delete(nestedFile));
        test.Check("automations: deleting a nearer tasks.hor restores the workspace root file", () =>
            tasks.Automations().All(automation => automation.File != nestedFile)
            && tasks.Automations().Count(automation => automation.File == file) == 3
            && tasks.Automations().Any(automation => automation.File == extraFile));

        test.Run(() =>
        {
            explorer.workspace.Folders.Remove(extraRoot);
        });
        test.Check("automations: changing workspace roots rebuilds the discovered sources", () =>
            tasks.Automations().Count(automation => automation.File == file) == 3
            && tasks.Automations().All(automation => automation.File != extraFile));

        test.Run(() =>
        {
            if (addedWorkspaceFolder)
                explorer.workspace.Folders.Remove(folder);
            if (addedExtraRoot)
                explorer.workspace.Folders.Remove(extraRoot);
            Open(note);
            if (Directory.Exists(nested))
                Directory.Delete(nested, recursive: true);
            if (Directory.Exists(extraRoot))
                Directory.Delete(extraRoot, recursive: true);
        });
        test.Settle(0.3f);

        TestPushAll(test);
    }
}
