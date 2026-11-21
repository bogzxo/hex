using Hex.Shell;
using Horizon.UI.Components;

namespace Hex.Dusk;

// Dusk in every look Hex comes with at 100, 150 and 200%, the same few tabs in each so the pictures can be laid side by
// side: a tab whose commands ran (one went well, one didn't) split beside a log, a tab whose program ended with a code,
// and one behind that rang its bell. The panes are written by hand with the shell's marks (OSC 133) rather than a real
// shell, so every picture says the same. DUSK_SELFTEST_SHOTS names a folder for them.
internal sealed partial class DuskScene
{
    // A prompt, a command typed at it, what it wrote and how it ended, the way a shell with its integration says them
    private static string Block(string command, string output, int? exit)
    {
        const string ESC = "\u001b", BEL = "\u0007";
        string prompt = $"{ESC}]133;A{BEL}{ESC}[32mPS{ESC}[0m {ESC}[36mC:\\src\\horizon{ESC}[0m> {ESC}]133;B{BEL}";
        if (exit is not { } code)
            return prompt + command;
        return $"{prompt}{command}\r\n{ESC}]133;C{BEL}{output}{ESC}]133;D;{code}{BEL}";
    }

    private const string LOOKS_BUILD = "  Determining projects to restore...\r\n  Horizon -> C:\\src\\horizon\\bin\\Debug\\Horizon.dll\r\n  Hex -> C:\\src\\hex\\bin\\Debug\\Hex.dll\r\n\r\n\u001b[32mBuild succeeded.\u001b[0m 0 Warning(s) 0 Error(s)\r\n";
    private const string LOOKS_TEST = "  Passed!  - Failed: 0, Passed: 412 - Horizon.Tests.dll\r\n  \u001b[31mFailed!\u001b[0m - Failed: 2, Passed: 87 - Hex.Tests.dll\r\n";

    private static TerminalProfile Fixture(string name, string icon) => new(name, string.Empty, icon, "looks-" + name) { Kind = TerminalProfileKind.Embedded };

    // The tabs every picture has, the first open
    private void StageLooks()
    {
        // The ended one stays to be seen, the suite puts the setting back as it ends
        panel.CloseOnExit = false;
        foreach (TerminalTab tab in panel.Tabs.ToArray())
            panel.CloseTab(tab);

        TerminalTab work = panel.NewTab(Fixture("pwsh", "terminal-powershell"))!;
        TerminalView left = work.FocusedView!;
        left.Feed("\u001bc" + Block("dotnet build", LOOKS_BUILD, 0) + Block("dotnet test", LOOKS_TEST, 1) + Block("git status", string.Empty, null));
        TerminalView? right = panel.Split(SplitOrientation.Horizontal, Fixture("log", "output"));
        right?.Feed("\u001bc\u001b[2m12:04:31\u001b[0m info  server listening on :5173\r\n\u001b[2m12:04:33\u001b[0m info  GET / 200 4 ms\r\n\u001b[2m12:04:35\u001b[0m \u001b[33mwarn\u001b[0m  slow query, 812 ms\r\n");

        TerminalTab ended = panel.NewTab(Fixture("cmd", "terminal-cmd"))!;
        ended.FocusedView!.Feed("\u001bcC:\\src> build.cmd\r\nerror: the build broke\r\n");
        ended.FocusedView.End(1);

        TerminalTab rang = panel.NewTab(Fixture("bash", "terminal-bash"))!;
        rang.FocusedView!.Feed("\u001bcuser@box:~$ sleep 30 && echo done\r\ndone\r\n\a");

        panel.Select(work);
        if (right is not null)
            panel.FocusPane(left);
    }

    private void TestLooks(DuskScript test)
    {
        float scaleBefore = 1.0f;
        string themeBefore = settings.Theme;
        test.Run(() => scaleBefore = compositor.Scale);

        foreach (HexLook look in HexLooks.BuiltIn)
        {
            foreach (float scale in (float[])[1.0f, 1.5f, 2.0f])
            {
                string named = $"{look.Name} at {scale * 100:0}%";
                test.Run(() =>
                {
                    PickLook(look);
                    SetInterfaceSize(scale);
                    StageLooks();
                });
                test.Settle(0.4f);
                test.Check($"looks: {named}, three tabs, the first split in two", () =>
                    panel.Tabs.Count == 3 && panel.Current == panel.Tabs[0] && panel.Current.Tree.Count == 2 && panel.Bounds.Width > 0);
                string shot = $"looks-{look.Name.Replace(' ', '-').ToLowerInvariant()}-{scale * 100:0}";
                test.Run(() => Engine.WindowManager.RequestFrames());
                test.Settle(0.4f);
                test.Run(() => Shot(shot));
                test.Settle(0.1f);
            }
        }

        // The states in a look of their own, at the size it comes
        test.Run(() =>
        {
            PickLook(HexLooks.Graphite);
            SetInterfaceSize(1.0f);
            StageLooks();
        });
        test.Settle(0.4f);
        test.Run(() => panel.Select(panel.Tabs[1]));
        test.Settle(0.3f);
        test.Run(() => Shot("looks-state-exited"));
        test.Settle(0.1f);
        test.Run(() =>
        {
            panel.Select(panel.Tabs[0]);
            panel.BeginFind();
        });
        test.Settle(0.2f);
        test.Run(() =>
        {
            if (compositor.Focus is TextBox box)
            {
                box.Text = "dotnet";
                box.OnChanged?.Invoke(box.Text);
            }
        });
        test.Settle(0.3f);
        test.Run(() => Shot("looks-state-find"));
        test.Settle(0.1f);
        test.Run(() => panel.CloseFind());

        test.Run(() =>
        {
            SetInterfaceSize(scaleBefore);
            settings.Theme = themeBefore;
            host.ApplySettings();
            panel.CloseOnExit = host.Settings.CloseOnExit;
        });
        test.Settle(0.3f);
    }
}
