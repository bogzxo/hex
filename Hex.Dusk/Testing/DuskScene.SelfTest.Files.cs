using Horizon.UI.Components;

namespace Hex.Dusk;

// The parts of the self test about the files Dusk shares with Hex. Written from outside, the way Hex saving them
// would, into the home the self test has (Program), and taken in without a restart. And the tabs kept and put back.
internal sealed partial class DuskScene
{
    private void TestFiles(DuskScript test)
    {
        string home = TerminalFiles.UserFolder;
        string keys = Path.Combine(home, TerminalFiles.KEYS);
        string settingsFile = Path.Combine(home, TerminalSettings.FILE);
        string plugin = Path.Combine(home, TerminalFiles.PLUGINS, "selftest_plugin", TerminalPlugin.FILE);

        test.Check("the files are in the self test's own home", () => home.Contains("dusk-selftest-home") && host.Options.KeysFiles[^1] == keys);

        // keys.hor saved from outside moves a key
        test.Run(() => File.WriteAllText(keys, "#define dusk\nlet keys = { new_tab: \"Ctrl+Shift+Y\" };\n"));
        test.WaitFor(() => KeyOf("new_tab") == "Ctrl+Shift+Y", 3.0f);
        test.Check("keys.hor saved from outside moves new_tab to Ctrl+Shift+Y", () => KeyOf("new_tab") == "Ctrl+Shift+Y");
        test.Check("and the menu says so", () => menu.Menus[0].Items.First(item => item.Label == "New tab").Shortcut == "Ctrl+Shift+Y");
        test.Run(() => File.WriteAllText(keys, "#define dusk\nlet keys = {};\n"));
        test.WaitFor(() => KeyOf("new_tab") == "Ctrl+Shift+T", 3.0f);
        test.Check("emptied, it goes back to Dusk's own", () => KeyOf("new_tab") == "Ctrl+Shift+T");

        // settings.hor saved from outside changes the text
        float textBefore = 0.0f;
        test.Run(() =>
        {
            textBefore = settings.TextScale;
            var changed = new TerminalSettings(null) { Theme = settings.Theme, TextScale = 0.22f, Animations = settings.Animations };
            File.WriteAllText(settingsFile, changed.Write());
        });
        test.WaitFor(() => Math.Abs(settings.TextScale - 0.22f) < 0.001f, 3.0f);
        test.Check("settings.hor saved from outside changes the text's size", () => Math.Abs(settings.TextScale - 0.22f) < 0.001f && Math.Abs(panel.FocusedView!.TextScale - 0.22f) < 0.001f);
        test.Run(() => File.WriteAllText(settingsFile, "this is not HIDL {{{"));
        test.Settle(0.6f);
        test.Check("a settings.hor that doesn't read leaves things as they were", () => Math.Abs(settings.TextScale - 0.22f) < 0.001f);
        test.Run(() =>
        {
            settings.TextScale = textBefore;
            host.ApplySettings();
            host.SaveSettings();
        });

        // open_settings asks the Hex that is running, over its pipe, which the test plays the part of on a pipe of its own
        string? heard = null;
        test.Run(() =>
        {
            string pipe = "dusk-selftest-" + Environment.ProcessId;
            Environment.SetEnvironmentVariable("DUSK_HEX_PIPE", pipe);
            var thread = new Thread(() =>
            {
                using var server = new System.IO.Pipes.NamedPipeServerStream(pipe, System.IO.Pipes.PipeDirection.In);
                server.WaitForConnection();
                using var reader = new StreamReader(server);
                heard = reader.ReadLine();
            }) { IsBackground = true };
            thread.Start();
        });
        test.Settle(0.2f);
        test.Run(() => Run("open_settings"));
        test.WaitFor(() => heard is not null, 3.0f);
        test.Check("open_settings asks the Hex that is running to open settings.hor", () => heard == $"open|{Path.GetFullPath(settingsFile)}|0");
        test.Run(() => Environment.SetEnvironmentVariable("DUSK_HEX_PIPE", "dusk-selftest-nobody-" + Environment.ProcessId));

        // A plugin put in the user's folder starts, and starts again when it changes
        test.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(plugin)!);
            File.WriteAllText(plugin, """
                let plugin = { name: "Self test", description: "dropped in by the self test" };
                terminal.command("selftest_hello", "Hello", func(terminal) { terminal.say("hello from a plugin"); }, "");
                """);
        });
        test.WaitFor(() => host.Commands.Contains("selftest_hello"), 3.0f);
        test.Check("a plugin put in the plugins folder starts without a restart", () => host.Commands.Find("selftest_hello")?.Title == "Hello");
        test.Run(() => Run("selftest_hello"));
        test.Check("and its command runs", () => messageItem.Text.Contains("hello from a plugin"));
        test.Run(() => File.WriteAllText(plugin, """
            let plugin = { name: "Self test" };
            dusk.command("selftest_hello", "Hello again", func(dusk) { dusk.say("again"); }, "");
            """));
        test.WaitFor(() => host.Commands.Find("selftest_hello")?.Title == "Hello again", 3.0f);
        test.Check("changed, it starts again, and dusk is still a name for the object", () => host.Commands.Find("selftest_hello")?.Title == "Hello again");
        test.Run(() => File.WriteAllText(plugin, "let plugin = { name: \"Self test\" }; this is broken ((("));
        test.WaitFor(() => !host.Commands.Contains("selftest_hello"), 3.0f);
        test.Check("broken, its commands go and it says why", () => !host.Commands.Contains("selftest_hello") && host.Plugins.Any(each => each.Id == "selftest_plugin" && each.Failed is not null));
        test.Run(() => Directory.Delete(Path.GetDirectoryName(plugin)!, recursive: true));
        test.Settle(0.5f);
    }

    private void TestSessions(DuskScript test)
    {
        test.Run(() => Run("split_right"));
        test.Settle(settle);
        test.Run(() => Run("split_down"));
        test.Settle(settle);
        test.Run(() => Run("new_tab"));
        test.Settle(settle);
        test.Run(() => Run("rename_tab"));
        test.Run(() => chrome.Dialog?.Cancel());
        test.Run(() => panel.Rename(panel.Current!, "kept"));
        test.Run(() => panel.Select(0));
        test.Settle(settle);
        test.WaitFor(() => panel.Tabs.All(tab => tab.Views.All(view => view.Process is { IsRunning: true })), 5.0f);

        string? shapeBefore = null;
        test.Run(() =>
        {
            shapeBefore = panel.Tabs[0].Tree.Describe(_ => "o");
            SaveSession();
        });
        test.Check("the session is written", () => File.Exists(SessionFile) && File.ReadAllText(SessionFile).Contains("split: \"right\""));

        test.Run(() =>
        {
            foreach (TerminalTab tab in panel.Tabs.ToArray())
                panel.CloseTab(tab);
        });
        test.Settle(settle);
        test.Run(() => RestoreSession());
        test.Settle(settle);
        test.Check("put back, there are the two tabs", () => panel.Tabs.Count == 2);
        test.Check("the first split as it was", () => panel.Tabs[0].Tree.Describe(_ => "o") == shapeBefore && shapeBefore == "[o|[o/o]]");
        test.Check("the second renamed as it was", () => panel.Tabs[1].Title == "kept");
        test.Check("and the first is the one open", () => panel.CurrentIndex == 0);
        test.WaitFor(() => panel.Tabs.All(tab => tab.Views.All(view => view.Process is { IsRunning: true })), 8.0f);
        test.Check("every pane has its shell", () => panel.Tabs.All(tab => tab.Views.All(view => view.Process is { IsRunning: true })));

        test.Run(() => Run("zoom_pane"));
        test.Wait();
        test.Check("zoom_pane puts the focused pane over the whole tab", () => panel.Current!.Zoomed is not null && Math.Abs(panel.FocusedView!.Bounds.Width - panel.Current!.Tree.Root!.Bounds.Width) < 2.0f);
        test.Run(() => Run("zoom_pane"));
        test.Wait();
        test.Check("and back", () => panel.Current!.Zoomed is null && panel.Current!.Views.All(view => view.Visible));
    }
}
