using Horizon.UI.Components;

namespace Hex.Dusk;

// The HIDL REPL as a profile, a program of the terminal's own with no process behind it, typed into for real.
internal sealed partial class DuskScene
{
    private void TestRepl(DuskScript test)
    {
        test.Check("HIDL is a profile", () => host.ProfileCalled(TerminalHost.HIDL_PROFILE) is { Kind: TerminalProfileKind.Embedded });
        test.Check("and the profiles plugin made a command of it", () => host.Commands.Contains("new_tab_hidl"));

        test.Run(() => Run("new_tab_hidl"));
        test.Settle(settle);
        test.Check("new_tab_hidl opens the REPL in a tab", () => panel.FocusedView?.Program is TerminalHidlRepl && panel.Tabs.Count == 2);
        test.Check("with its greeting and its prompt", () => panel.FocusedView!.Screen.Text().Contains("HIDL, the language of Horizon") && PromptLine().Contains("hidl >"));

        if (!RealKeys.Available)
            return;

        BringToFront(test);
        TypeText(test, "let answer = 6 * 7");
        PressKey(test, RealKeys.ENTER);
        TypeText(test, "answ");
        test.Check("a name it knows is completed after the caret", () => PromptLine().Contains("answer"));
        TypeText(test, "\t");
        PressKey(test, RealKeys.ENTER);
        test.Check("Return runs the line and its value is written under it", () => Count(panel.FocusedView, "42") >= 1);

        PressKey(test, RealKeys.UP);
        test.Check("Up brings the line before back, the REPL's own history", () => PromptLine().EndsWith("answer"));
        PressKey(test, RealKeys.DOWN);

        TypeText(test, "{ a: 1,");
        PressKey(test, RealKeys.ENTER);
        test.Check("a line with a bracket open waits for the rest", () => PromptLine().StartsWith("..."));
        TypeText(test, "b: 2 }");
        PressKey(test, RealKeys.ENTER);
        test.Check("and runs once it is closed", () => panel.FocusedView!.Screen.Text().Contains("b"));

        TypeText(test, "exit");
        PressKey(test, RealKeys.ENTER);
        test.Settle(settle + 0.2f);
        test.Check("exit ends it and its tab closes", () => panel.Tabs.Count == 1);
    }
}
