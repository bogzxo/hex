using Horizon.UI.Components;

namespace Hex.Dusk;

// The self test of what programs write being coloured by rules, the shipped ones and one a plugin adds, behind
// highlight_output, which is off until somebody says.
internal sealed partial class DuskScene
{
    private void TestOutput(DuskScript test)
    {
        TerminalView View() => panel.FocusedView!;
        string plugin = Path.Combine(TerminalFiles.UserFolder, TerminalFiles.PLUGINS, "selftest_rules", TerminalPlugin.FILE);

        test.Check("the output is left alone until the settings say", () => !host.Settings.HighlightOutput && View().OutputHighlighter is null);
        test.Run(() =>
        {
            host.Settings.HighlightOutput = true;
            host.ApplySettings();
        });
        test.Check("highlight_output colours every terminal's output", () => View().OutputHighlighter is not null);

        test.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(plugin)!);
            File.WriteAllText(plugin, """
                let plugin = { name: "Rules", description: "a rule of the self test's" };
                terminal.output_rule("ticket", "\\bDUSK-\\d+\\b", "yellow", true);
                """);
        });
        test.WaitFor(() => host.OutputRules.Rules.Exists(rule => rule.Name == "ticket"), 3.0f);
        test.Check("a plugin adds a rule of its own", () => host.OutputRules.Rules.Exists(rule => rule.Name == "ticket"));

        test.WaitFor(() => View().Screen.AtPrompt, 10.0f);
        test.Run(() => View().Send(OperatingSystem.IsWindows() ? "Write-Output 'the build failed in 3 files, see DUSK-42'\r" : "echo 'the build failed in 3 files, see DUSK-42'\r"));
        test.WaitFor(() => RowOf(View(), "the build failed in 3") >= 0 && View().Screen.AtPrompt, 8.0f);
        test.Settle(0.3f);
        test.Run(() => Shot("output"));
        test.Check("an error word is red, a number a number and the plugin's ticket yellow and underlined", () =>
        {
            int row = RowOf(View(), "the build failed in 3");
            if (row < 0)
                return false;
            string text = View().Screen.RowAt(row).Text(), codes = View().OutputCodesText(row - View().TopRow);
            return codes.Length >= text.Length - 1
                && codes.Substring(text.IndexOf("failed", StringComparison.Ordinal), 6) == "rrrrrr"
                && codes[text.IndexOf(" 3 ", StringComparison.Ordinal) + 1] == 'm'
                && codes.Substring(text.IndexOf("DUSK-42", StringComparison.Ordinal), 7) == "YYYYYYY";
        });

        test.Run(() =>
        {
            host.Settings.HighlightOutput = false;
            host.ApplySettings();
            Directory.Delete(Path.GetDirectoryName(plugin)!, recursive: true);
        });
        test.Check("and off again", () => View().OutputHighlighter is null);

        // The plugin going is said in the status bar, and that goes once it's old, it sat in every shot after this one
        test.WaitFor(() => messageItem.Text.Contains("Rules is gone"), 3.0f);
        test.Check("the plugin taken away is said in the status bar", () => messageItem.Text.Contains("Rules is gone"));
        test.Run(() => saidAt = System.Environment.TickCount64 - SAID_FOR - 1);
        test.Wait();
        test.Check("and an old message goes from the status bar", () => messageItem.Text.Length == 0);
    }

    // The row of the whole a text is on, the last one it is on that isn't the line typed, -1 for none
    private static int RowOf(TerminalView view, string text)
    {
        for (int row = view.Screen.TotalRows - 1; row >= 0; row--)
        {
            string line = view.Screen.RowAt(row).Text();
            if (line.Contains(text, StringComparison.Ordinal) && !line.Contains("Write-Output", StringComparison.Ordinal))
                return row;
        }

        return -1;
    }
}
