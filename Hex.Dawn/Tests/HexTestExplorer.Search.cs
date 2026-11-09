using System.Numerics;

using Hex.Core.Projects;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The box over the explorer's rows, Visual Studio's search of its Test Explorer. What's typed narrows the tests down to
// the ones with it in their namespace, class, method or a case's arguments, and the buttons at its end to the ones that
// failed, passed or haven't run, or typed the way VS's search takes an outcome. Finding one test of a thousand was a lot
// of scrolling. The rows are only a few thousand, so they're narrowed down as they're made, there and then
internal sealed partial class HexTestExplorer
{
    private const float SEARCH_HEIGHT = 26.0f, OUTCOME_BUTTON = 24.0f, OUTCOME_GAP = 2.0f;

    private TextBox search = null!;
    private readonly List<(Button Button, DotnetTestOutcomes Outcome)> outcomeButtons = [];

    // The outcomes picked with the buttons
    private DotnetTestOutcomes picked;

    // Whether the rows being made are narrowed down, something typed or an outcome picked
    private bool searching;

    // The box and its buttons made, over the rows while the side bar is on the explorer
    private void BuildSearch()
    {
        (string Icon, string Tip, DotnetTestOutcomes Outcome)[] outcomes =
        [
            ("error", "tests.show_failed", DotnetTestOutcomes.Failed),
            ("pass", "tests.show_passed", DotnetTestOutcomes.Passed),
            ("circle-large-outline", "tests.show_not_run", DotnetTestOutcomes.NotRun)
        ];

        float buttons = outcomes.Length * (OUTCOME_BUTTON + OUTCOME_GAP);
        var head = new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0.0f, SEARCH_HEIGHT), Padding = new UIEdges(0.0f, 0.0f, buttons, 0.0f), Color = Vector4.Zero };
        search = head.Add(new TextBox
        {
            Fill = UIFill.Horizontal,
            Size = new Vector2(0.0f, SEARCH_HEIGHT),
            Placeholder = context.Strings.Said("tests.search"),
            LeadIcon = "search",
            MaxLength = 200,
            OnChanged = _ => Searched(),

            // Enter goes down to what was found, the keyboard on the rows
            OnSubmitted = _ => context.Panels.ShowSide(HexSide.Tests)
        });

        // Out in the room the box leaves past its end, which is in the head's padding, the last one at the far right
        for (int i = 0; i < outcomes.Length; i++)
        {
            var (icon, tip, outcome) = outcomes[i];
            var button = head.Add(new Button
            {
                Anchor = Origin.Right,
                Pivot = Origin.Right,
                Position = new Vector2(buttons - (outcomes.Length - 1 - i) * (OUTCOME_BUTTON + OUTCOME_GAP), 0.0f),
                Size = new Vector2(OUTCOME_BUTTON, SEARCH_HEIGHT - 2.0f),
                Icon = icon,
                Style = "button_flat",
                Tooltip = context.Strings.Said(tip),
                Animated = false,
                OnPressed = () => Pick(outcome)
            });
            outcomeButtons.Add((button, outcome));
        }

        context.Panels.AddSideHead(HexSide.Tests, head);
    }

    // An outcome's button pressed, the rows narrowed down to it as well or not any more
    private void Pick(DotnetTestOutcomes outcome)
    {
        picked ^= outcome;
        foreach (var (button, of) in outcomeButtons)
            button.Selected = (picked & of) != 0;
        Searched();
    }

    // What the rows are narrowed down to changed, they're made again. Cleared, what was opened while searching is forgotten
    private void Searched()
    {
        if (Query().IsEverything)
            openedSearching.Clear();
        context.Panels.RefreshSide(HexSide.Tests);
    }

    /// <summary>What the box and the buttons narrow the rows down to.</summary>
    private DotnetTestQuery Query() => search is null ? DotnetTestQuery.Everything : DotnetTestQuery.Read(search.Text, picked);

    /* A row's menu */

    // The test or case row pressed last and where that put the caret. A click on a row goes to the test and the keyboard
    // goes with it, so Ctrl+R T straight after it is that row's, a theory's case on its own, for as long as the caret
    // stays where the click put it. It was the caret's, and ran every case of the theory
    private (TestTarget Target, string? Path, int Line)? pressedLast;

    // A test's or a case's row pressed, gone to and remembered
    private void Pressed(TestTarget target)
    {
        target.GoTo?.Invoke();
        HexDocument now = context.Documents.Current;
        pressedLast = (target, now.Path, now.Editor.Document.Caret.Line);
    }

    // The row picked in the explorer while it has the keyboard, or the one pressed last while the caret is still where
    // that put it, what Ctrl+R T runs then. A failure's row is its test's
    private TestTarget? PickedRow()
    {
        if (!context.Panels.Showing(HexSide.Tests))
            return null;

        for (UITreeItem? row = context.Panels.FocusedSideRow; row is not null; row = row.Parent)
        {
            if ((row.Tag as SideTag)?.Id is { } id && targets.TryGetValue(id, out TestTarget? target))
                return target;
        }

        HexDocument current = context.Documents.Current;
        return pressedLast is { } last && last.Path is { } path && current.Path is { } at && PlatformPaths.Same(path, at) && current.Editor.Document.Caret.Line == last.Line
            ? last.Target
            : null;
    }

    // A right click on a test, a case, a class, a namespace or a project, Visual Studio's menu of it
    private void Menu(TestTarget target)
    {
        var menu = new Menu("tests");
        menu.Add(context.Strings.Said("tests.run"), () => Run(target.Project, target.Names, target.What, target.Framework), "Ctrl+R T", "play").IsEnabled = () => !running;
        menu.Add(context.Strings.Said("tests.debug"), () => Debug(target.Project, target.Names, target.What, target.Framework), "Ctrl+R Ctrl+T", "debug-alt").IsEnabled = () => !running && !debugger.Debugging;
        menu.Add(context.Strings.Said("tests.run_until_failure"), () => RunUntilFailure(target), icon: "sync").IsEnabled = () => !running;
        menu.AddSeparator();
        if (target.GoTo is { } go)
            menu.Add(context.Strings.Said("tests.go_to"), go, icon: "go-to-file");
        menu.Add(context.Strings.Said("tests.copy_name"), () => UIClipboard.Copy(target.Copied), "Ctrl+C", "copy");
        context.Windows.Menu(menu);
    }

    // The keys a row takes, Ctrl+C its name
    private static bool RowKey(TestTarget target, char key)
    {
        if (key != UIKeyboard.COPY)
            return false;

        UIClipboard.Copy(target.Copied);
        return true;
    }

    /* Run Until Failure */

    // What Run Until Failure is running again and how many times it has, null while it isn't
    private (string Project, string[]? Names, string What, int Round, string? Framework)? untilFailure;

    // How many times it goes before it calls it held, VS goes on until it's stopped
    private const int MOST_ROUNDS = 100;

    /// <summary>Run Until Failure, the tests of a row run again and again until one fails, or a hundred times. Any other run stops it.</summary>
    private void RunUntilFailure(TestTarget target)
    {
        Run(target.Project, target.Names, target.What, target.Framework);
        untilFailure = (target.Project, target.Names, target.What, 1, target.Framework);
    }

    /// <summary>Called as a run's results are in, the next round started if nothing failed. True when it said how it went itself.</summary>
    private bool AgainUntilFailure(int failed, bool debugged)
    {
        if (untilFailure is not { } until || debugged || running)
            return false;

        if (failed == 0 && until.Round < MOST_ROUNDS)
        {
            untilFailure = until with { Round = until.Round + 1 };
            Start(until.Project, until.Names, until.What, until.Framework);
            context.Notices.Say(context.Strings.Said("tests.until_round", ("what", until.What), ("round", Number(until.Round + 1))));
            return true;
        }

        untilFailure = null;
        context.Notices.Say(context.Strings.Said(failed > 0 ? "tests.until_failed" : "tests.until_held", ("what", until.What), ("round", Number(until.Round))), error: failed > 0);
        return true;
    }
}
