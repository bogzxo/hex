using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The runner (Run, Run a behaviour, Ctrl+F5), a function of the file called tick after tick the way a game calls a
// behaviour (Fighter2D's dummy behaviours, a view of the fight in and what to press out). A little window out of
// runner.hor says which function, the view it's handed and how many ticks, and it's called through the console, in what
// the file came to, so what it hands back every tick and what it prints are lines of the console, and the state at the
// top of the file (pressed_for in the dummy's) is said after. The view can be changed between one tick and the next, Start
// over runs the file again so its state is what it starts as.
internal sealed partial class HexScene
{
    private WindowPanel? runnerWindow;
    private Dropdown runnerFunction = null!;
    private TextBox runnerView = null!;
    private NumberBox runnerTicks = null!;
    private Label runnerAt = null!;

    // Which tick is next, and how many times the runner has asked, for names of its own in the console's runtime
    private int runnerTick, runnerRuns;

    private void OpenRunner()
    {
        if (runnerWindow is { Visible: true } open)
        {
            open.BringToFront();
            FillRunnerFunctions();
            return;
        }

        UILayout made = layout.Instantiate("runner.hor", chrome.Root);
        var window = runnerWindow = made.Get<WindowPanel>("window");
        window.Color = compositor.Skin is { } skin ? skin.PanelColor with { W = 1.0f } : null;
        window.OnClosed = () =>
        {
            window.Parent?.Remove(window);
            runnerWindow = null;
        };

        runnerFunction = made.Get<Dropdown>("runner_function");
        runnerView = made.Get<TextBox>("runner_view");
        runnerTicks = made.Get<NumberBox>("runner_ticks");
        runnerAt = made.Get<Label>("runner_at");
        runnerView.Text = "{ tick: 0 }";
        runnerTicks.Value = 10;

        made.Get<Button>("runner_step").OnPressed = () => RunBehaviour(1);
        made.Get<Button>("runner_run").OnPressed = () => RunBehaviour(Math.Max(1, (int)runnerTicks.Value));
        made.Get<Button>("runner_reset").OnPressed = StartRunnerOver;

        runnerTick = 0;
        FillRunnerFunctions();
        ShowRunnerTick();
    }

    // The functions the file declares, the dropdown's choices
    private void FillRunnerFunctions()
    {
        string[] functions = values is { } shown && valuesOf == document
            ? [.. shown.Declared.Where(pair => pair.Value is FunctionValue or AnonymousFunctionValue).Select(pair => pair.Name)]
            : [];

        string was = runnerFunction.Value;
        runnerFunction.Options = functions.Length > 0 ? functions : ["none"];
        runnerFunction.Value = functions.Contains(was) ? was : runnerFunction.Options[0];
    }

    private void ShowRunnerTick() => runnerAt.Text = Said("runner.next", ("tick", (runnerTick + 1).ToString()));

    /// <summary>The function called so many ticks on from where the runner got to, through the console.</summary>
    private void RunBehaviour(int ticks)
    {
        FillRunnerFunctions();
        string function = runnerFunction.Value;
        if (values is not { } shown || valuesOf != document || !shown.Declared.Any(pair => pair.Name == function))
        {
            Say(Said("runner.nothing"), error: true);
            return;
        }

        string view = runnerView.Text.Trim().Length > 0 ? runnerView.Text.Trim() : "null";
        int run = ++runnerRuns, from = runnerTick, to = runnerTick + ticks;

        // The state at the top of the file, everything it declares that isn't a function, said after the ticks
        string[] state = [.. shown.Declared.Where(pair => pair.Value is not (FunctionValue or AnonymousFunctionValue)).Select(pair => pair.Name).Take(8)];
        string stateLine = state.Length == 0 ? string.Empty : $" print(`state   {string.Join(", ", state.Select(name => $"{name} = {{{name}}}"))}`);";

        string v = $"__runner_view_{run}", t = $"__runner_tick_{run}", said = $"__runner_said_{run}";
        string snippet = $"let {v} = {view}; for ({t} in range({from}, {to})) {{ if (typeof({v}) == \"object\" && has({v}, \"tick\")) {{ {v}.tick = {t}; }} " +
                         $"let {said} = {function}({v}); print(`tick {{{t} + 1}}   {{{said}}}`); }}{stateLine} null";

        HIDLRuntime runtime = shown.Runtime;
        IReadOnlyList<string> before = runtime == consoleRanIn ? [] : consoleDeclared;
        if (!evaluation.Ask(runtime, before, snippet, printed => consoleOut.Enqueue((UIRichText.Literal(printed), null))))
        {
            Say(Said("console.busy"), error: true);
            return;
        }

        consoleRanIn = runtime;
        consoleAsked = snippet;
        runnerTick = to;
        ShowRunnerTick();

        if (bottom.Selected != CONSOLE_PAGE || !bottom.Shown)
            ShowPanel(CONSOLE_PAGE);
        ConsoleLine($"[color:accent]>[/color] {function}, {(ticks == 1 ? $"tick {to}" : $"ticks {from + 1} to {to}")}, handed {UIRichText.Literal(view)}", null);
    }

    /// <summary>The file run again, its state what it starts as, and the runner back to its first tick.</summary>
    private void StartRunnerOver()
    {
        runnerTick = 0;
        valuesAsked = (null, -1);
        ShowRunnerTick();
        Say(Said("runner.over"));
    }
}
