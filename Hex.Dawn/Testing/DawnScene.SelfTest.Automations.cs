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
        string writeKey = string.Empty, foreverKey = string.Empty;
        int tabsBefore = 0;

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

        TestPushAll(test);
    }
}
