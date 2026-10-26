namespace Hex.Dawn;

// The self test of the arrow beside Run. A folder's tasks.hor has two run tasks, the default one F5 runs for a file of
// it and another. The arrow's menu has both and the file on screen, ticked, the other picked is kept with the
// workspace, the button says it and F5 runs it rather than the default. The file on screen picked again puts it all
// back, F5 runs the default again.
internal sealed partial class DawnScene
{
    private void TestRunTarget(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "run_target");
        string note = Path.Combine(folder, "notes.txt");
        Button Play() => layout.Get<Button>("play_button");
        Button Arrow() => layout.Get<Button>("run_target_button");

        test.Run(() =>
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(folder);
            File.WriteAllText(note, "a file of a folder with run tasks\n");
            File.WriteAllText(Path.Combine(folder, HexTasks.TASKS_FILE), """
                let tasks = {
                    build: { command: "echo built", group: "build" },
                    plain_run: { command: "echo the default ran", group: "run", default: true },
                    say_target: { command: "echo the target ran", group: "run" },
                };
                """);
            Context.Workspace.RunTarget = null;
            Open(note);
        });
        test.Wait();
        test.Check("run target: nothing picked, the button is Run and the arrow is beside it", () =>
            Play() is { Visible: true } play && play.Label == Said("toolbar.run") && Arrow().Visible);

        test.Click(At(Arrow));
        test.Wait();
        test.Check("run target: the arrow's menu has the folder's run tasks and the file on screen, the file ticked", () =>
            contextMenu?.Showing is { } shown
            && shown.Items.Any(item => item.Label.StartsWith("say_target", StringComparison.Ordinal))
            && shown.Items.Any(item => item.Label.StartsWith("plain_run", StringComparison.Ordinal))
            && shown.Items.FirstOrDefault(item => item.Label.StartsWith(Said("run_target.current_file"), StringComparison.Ordinal)) is { IsChecked: { } ticked } && ticked()
            && !shown.Items.Any(item => item.Label.StartsWith("build", StringComparison.Ordinal)));

        test.Run(() =>
        {
            contextMenu!.Showing!.Items.First(item => item.Label.StartsWith("say_target", StringComparison.Ordinal)).OnPicked!();
            contextMenu.Close();
        });
        test.Wait();
        test.Check("run target: picked, it's kept with the workspace", () =>
            Context.Workspace.RunTarget is { Kind: HexRunTarget.TASK, Name: "say_target" } target && PlatformPaths.Same(target.Path, folder)
            && HexRunTarget.From(RunTargetSaid(explorer.workspace), folder) is { Name: "say_target" });
        test.Check("run target: and the button says it", () => Play() is { Visible: true, Label: "say_target" } play && play.Tooltip.Contains("echo the target ran"));

        test.Run(RunOrPlay);
        test.Settle(0.3f);
        test.Check("run target: F5 runs it rather than the folder's default, in its tab", () => terminalPanel.Current is { Title: "say_target" });

        // The file on screen again, the way F5 always went
        test.Run(() =>
        {
            if (terminalPanel.Current is { Title: "say_target" } tab)
                terminalPanel.CloseTab(tab);
            ChooseRunTarget(tasks.RunChoices()[0]);
        });
        test.Wait();
        test.Check("run target: the file on screen picked again, nothing is kept and the button is Run", () =>
            Context.Workspace.RunTarget is null && RunTargetSaid(explorer.workspace) is null && Play().Label == Said("toolbar.run"));
        test.Run(RunOrPlay);
        test.Settle(0.3f);
        test.Check("run target: and F5 runs the folder's default again", () => terminalPanel.Current is { Title: "plain_run" });

        test.Run(() =>
        {
            if (terminalPanel.Current is { Title: "plain_run" } tab)
                terminalPanel.CloseTab(tab);
            CloseNow();
        });
        test.Settle(0.3f);
    }

    // What a workspace says its run target is, read back out of what it writes
    private static Horizon.HIDL.Runtime.IRuntimeValue? RunTargetSaid(HexWorkspace workspace)
    {
        var runtime = new Horizon.HIDL.HIDLRuntime { Output = null };
        runtime.Evaluate(workspace.Write(null));
        return runtime.UserScope.Lookup("workspace") is Horizon.HIDL.Runtime.ObjectValue said ? said.Properties.GetValueOrDefault("run_target") : null;
    }
}
