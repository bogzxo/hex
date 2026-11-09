using System.Globalization;

using Hex.Core.Projects;

using Horizon.UI.Components;

namespace Hex.Dawn;

// The explorer's rows, a summary and the buttons, then project, namespace, class and test, Visual Studio's hierarchy. A
// theory has a row for each of its cases under it, the way Visual Studio has, each with its own tick or cross, its own
// Run and Debug and what failed in it. A theory of two cases with one failing was one red row, and which case it was had
// to be read off the message. Every row has an id, so the one picked stays picked and the list stays where it was as the
// rows are made again after a run (DawnScene.SidePlaces), and what the search box has typed in it narrows them down
internal sealed partial class HexTestExplorer
{
    // What each row runs, debugs, goes to and copies, by its id, made again with the rows
    private readonly Dictionary<string, TestTarget> targets = new(StringComparer.Ordinal);

    /// <summary>What a row is of, the project and the names a run of it goes by (null for all of the project), and the rest its menu has.</summary>
    private sealed record TestTarget(string Project, string[]? Names, string What, string Copied, Action? GoTo, string? Framework = null);

    // The side's rows, a summary and the buttons, then project, namespace, class and test
    private List<UITreeItem> Rows()
    {
        var rows = new List<UITreeItem>();
        targets.Clear();
        if (!discovered && !discovering)
            Discover();

        DotnetTestTally all = DotnetTests.Tally(tests.Values.SelectMany(its => its), ResultsOf);
        rows.AddRange(Summary(all));
        if (discovered && tests.Count == 0)
        {
            rows.Add(HexNotes.Note(context.Strings.Said("tests.no_projects"), context.Panels.Dim));
            return rows;
        }

        DotnetTestQuery query = Query();
        searching = !query.IsEverything;
        foreach (var (project, its) in tests.OrderBy(pair => Path.GetFileName(pair.Key), StringComparer.OrdinalIgnoreCase))
        {
            string name = ctestProjects.TryGetValue(project, out var native) ? Path.GetFileName(native.Root) + " (CTest)" : Path.GetFileNameWithoutExtension(project);
            var projectRow = Group(name, "project", its, new TestTarget(project, null, name, name, () => context.Documents.OpenAt(project, 0, 0)));
            Remember(projectRow, project, tests.Count == 1 || Failing(its));
            List<DotnetTest> ofProjectShown = [];
            var namespaces = its.GroupBy(test => test.Namespace).OrderBy(group => group.Key, StringComparer.Ordinal).ToList();
            foreach (var inNamespace in namespaces)
            {
                // A class with no namespace goes straight under its project, VS's Empty Namespace row says nothing
                UITreeItem into = projectRow;
                List<DotnetTest> ofNamespaceShown = [];
                if (inNamespace.Key.Length > 0)
                {
                    List<DotnetTest> ofNamespace = [.. inNamespace];
                    string prefix = inNamespace.Key + ".";
                    into = Group(inNamespace.Key, "symbol-namespace", ofNamespace, new TestTarget(project, [prefix], inNamespace.Key, inNamespace.Key, null));
                    Remember(into, project + "|" + prefix, namespaces.Count == 1 || Failing(ofNamespace));
                }

                foreach (var inClass in inNamespace.GroupBy(test => test.FullClass).OrderBy(group => group.Key, StringComparer.Ordinal))
                {
                    List<DotnetTest> ofClass = [.. inClass];
                    string className = ofClass[0].Class;
                    var classRow = Group(className, "symbol-class", ofClass, new TestTarget(project, [inClass.Key + "."], className, inClass.Key, () => GoTo(ofClass[0])));
                    Remember(classRow, project + "|" + inClass.Key, Failing(ofClass));
                    List<DotnetTest> ofClassShown = [];
                    foreach (DotnetTest test in ofClass)
                    {
                        if (TestRow(project, test, query) is { } testRow)
                        {
                            classRow.Children!.Add(testRow);
                            ofClassShown.Add(test);
                        }
                    }

                    if (classRow.Children!.Count > 0 || query.IsEverything)
                        into.Children!.Add(Counted(classRow, searching ? ofClassShown : ofClass, "symbol-class"));
                    ofNamespaceShown.AddRange(ofClassShown);
                }

                if (into != projectRow && (into.Children!.Count > 0 || query.IsEverything))
                    projectRow.Children!.Add(Counted(into, searching ? ofNamespaceShown : [.. inNamespace], "symbol-namespace"));
                ofProjectShown.AddRange(ofNamespaceShown);
            }

            Counted(projectRow, searching ? ofProjectShown : its, "project");
            if (frameworkBuildFailures.TryGetValue(project, out var failedFrameworks))
            {
                projectRow.Icon = "error";
                projectRow.IconColor = context.Panels.Error;
                foreach (string framework in failedFrameworks.Order(StringComparer.Ordinal))
                {
                    if (!query.IsEverything && !query.Names(framework)) continue;
                    string label = FrameworkLabel(framework);
                    var target = new TestTarget(project, null, name + " (" + label + ")", name, null,
                        framework.Length == 0 ? null : framework);
                    string id = "framework-build|" + project + "|" + framework;
                    targets[id] = target;
                    projectRow.Children!.Insert(0, new UITreeItem(context.Strings.Said("tests.framework_not_run", ("framework", label)), "error")
                    {
                        Color = context.Panels.Error, Actions = RowActions(target),
                        Tooltip = context.Strings.Said("tests.framework_failed", ("what", name), ("frameworks", label)),
                        Tag = new SideTag(Menu: _ => Menu(target), Key: key => RowKey(target, key), Id: id)
                    });
                }
            }
            if (projectRow.Children!.Count > 0 || query.IsEverything) rows.Add(projectRow);
        }

        if (discovered && !query.IsEverything && rows.TrueForAll(row => (row.Tag as SideTag)?.Id is "summary" or "counts"))
            rows.Add(HexNotes.Note(context.Strings.Said("tests.nothing_matches"), context.Panels.Dim));
        return rows;
    }

    // The summary's two rows, how many tests and cases there are with the buttons, then how they came out, cases counted
    // the way VS counts them. A theory of two cases that both passed is 2 passed, it said 1 passed
    private IEnumerable<UITreeItem> Summary(DotnetTestTally all)
    {
        bool cases = all.Cases != all.Tests;
        string summary = discovering && all.Tests == 0
            ? context.Strings.Said("tests.finding")
            : running
                ? context.Strings.Said("tests.running", ("what", runningWhat))
                : context.Strings.Said(cases ? "tests.summary" : "tests.summary_tests", ("tests", Number(all.Tests)), ("cases", Number(all.Cases)));
        string counts = context.Strings.Said("tests.counts", ("passed", Number(all.Passed)), ("failed", Number(all.Failed)), ("skipped", Number(all.Skipped)), ("not_run", Number(all.NotRun)));
        yield return new UITreeItem(summary, running ? "loading~spin" : all.Failed > 0 ? "error" : all.Passed > 0 ? "pass" : "beaker")
        {
            IconColor = all.Failed > 0 ? context.Panels.Error : all.Passed > 0 && !running ? Passed : null,
            Tooltip = context.Strings.Said(cases ? "tests.summary" : "tests.summary_tests", ("tests", Number(all.Tests)), ("cases", Number(all.Cases))) + ", " + counts,
            Actions = running
                ? [new UITreeAction("debug-stop", context.Strings.Said("tests.cancel"), Cancel)]
                :
                [
                    new UITreeAction("run-all", context.Strings.Said("tests.run_all"), RunAll),
                    new UITreeAction("run-errors", context.Strings.Said("tests.run_failed"), RunFailed),
                    new UITreeAction("refresh", context.Strings.Said("tests.refresh"), Discover)
                ],
            Tag = new SideTag(Id: "summary")
        };

        if (all.Tests > 0)
        {
            yield return new UITreeItem(counts, string.Empty)
            {
                Color = all.Failed > 0 ? context.Panels.Error : context.Panels.Dim,
                Tag = new SideTag(Id: "counts")
            };
        }
    }

    /// <summary>A count said with its thousands apart, 1,042.</summary>
    private static string Number(int count) => count.ToString("N0", CultureInfo.InvariantCulture);

    // A project's, a namespace's or a class's row, its counts, its own Run and Debug buttons and its menu
    private UITreeItem Group(string text, string icon, List<DotnetTest> its, TestTarget target)
    {
        string id = "group|" + target.Project + "|" + (target.Names is { } names ? names[0] : string.Empty);
        targets[id] = target;
        var row = new UITreeItem(text, icon)
        {
            Detail = Took(its.SelectMany(ResultsOf)),
            Expandable = true,
            Children = [],
            Actions = RowActions(target),
            Tag = new SideTag(Menu: _ => Menu(target), Key: key => RowKey(target, key), Id: id)
        };
        return row;
    }

    // A group's row with how many cases are in it and how they came out, only the ones the search found while it's on.
    // Its own icon stays unless they all passed or one failed
    private UITreeItem Counted(UITreeItem row, List<DotnetTest> its, string icon)
    {
        DotnetTestTally tally = DotnetTests.Tally(its, ResultsOf);
        bool passed = tally.Passed == tally.Cases && tally.Passed > 0;
        row.Icon = tally.Failed > 0 ? "error" : passed ? "pass" : icon;
        row.IconColor = tally.Failed > 0 ? context.Panels.Error : passed ? Passed : null;
        row.Badge = tally.Failed > 0 ? $"{Number(tally.Failed)}/{Number(tally.Cases)}" : Number(tally.Cases);
        row.BadgeColor = tally.Failed > 0 ? context.Panels.Error : null;
        return row;
    }

    // Whether any of some tests failed last time
    private bool Failing(List<DotnetTest> its)
    {
        foreach (DotnetTest test in its)
        {
            if (Outcome(test) == DotnetTestOutcome.Failed)
                return true;
        }

        return false;
    }

    // A row's Run and Debug buttons
    private UITreeAction[] RowActions(TestTarget target) => [
        new UITreeAction("play", context.Strings.Said("tests.run"), () => Run(target.Project, target.Names, target.What, target.Framework)),
        new UITreeAction("debug-alt", context.Strings.Said("tests.debug"), () => Debug(target.Project, target.Names, target.What, target.Framework))
    ];

    /// <summary>
    /// A test's row, its result, how long it took, and what failed under it, or a theory's cases under it. Null when the
    /// search leaves it out, its name hasn't what's typed and none of its cases' have, or it didn't come out a way picked.
    /// </summary>
    private UITreeItem? TestRow(string project, DotnetTest test, DotnetTestQuery query)
    {
        bool named = query.Names(test.FullName);
        List<DotnetTestResult> cases = [.. ResultsOf(test).OrderBy(result => result.FullName, StringComparer.Ordinal)];
        List<DotnetTestResult> shown = query.IsEverything ? cases : cases.FindAll(result => (named || query.Names(result.FullName)
            || result.Framework.Length > 0 && query.Names(result.Framework)) && query.Shows(result.Outcome));
        if (cases.Count == 0 ? !named || !query.Shows(DotnetTestOutcome.NotRun) : shown.Count == 0)
            return null;

        DotnetTestOutcome outcome = Outcome(test);
        bool waits = waiting.Contains(Identity(project, test.FullName));
        var target = new TestTarget(project, [test.FullName], test.Method, test.FullName, () => GoTo(test));
        string id = "test|" + project + "|" + test.FullName;
        targets[id] = target;
        var row = new UITreeItem(test.Method, waits ? "clock" : Icon(outcome))
        {
            IconColor = waits ? context.Panels.Dim : Shade(outcome),
            Detail = Took(cases),
            Badge = cases.Count > 1 ? Number(cases.Count) : string.Empty,
            Actions = RowActions(target),
            Tag = new SideTag(Pressed: () => Pressed(target), Menu: _ => Menu(target), Key: key => RowKey(target, key), Id: id)
        };

        if (cases.Any(result => result.Framework.Length > 0))
        {
            row.Expandable = true;
            Remember(row, id, outcome == DotnetTestOutcome.Failed);
            row.Children = [.. shown.GroupBy(result => result.Framework).OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => FrameworkRow(project, test, group.Key, [.. group], waits))];
            return row;
        }

        if (cases.Count == 0 || (cases.Count == 1 && !cases[0].IsCase && cases[0].Outcome != DotnetTestOutcome.Failed))
            return row;

        row.Expandable = true;
        Remember(row, id, outcome == DotnetTestOutcome.Failed);
        row.Children = [];
        foreach (DotnetTestResult result in shown)
        {
            if (result.IsCase)
                row.Children.Add(CaseRow(project, test, result, waits));
            else if (result.Outcome == DotnetTestOutcome.Failed)
                row.Children.Add(FailureRow(test, result));
        }

        return row;
    }

    // A theory's case, the method with its arguments, how it came out, its own Run and Debug, and what failed in it under it
    private UITreeItem CaseRow(string project, DotnetTest test, DotnetTestResult result, bool testWaits)
    {
        string name = test.Method + result.FullName[result.FullName.IndexOf('(')..];
        var target = new TestTarget(project, [result.FullName], name, result.FullName, () => GoTo(test, result), result.Framework.Length > 0 ? result.Framework : null);
        string id = "case|" + project + "|" + result.Framework + "|" + result.FullName;
        targets[id] = target;
        bool waits = (testWaits || waiting.Contains(Identity(project, result.FullName))) && FrameworkWaiting(project, result.Framework);
        var row = new UITreeItem(name, waits ? "clock" : Icon(result.Outcome))
        {
            IconColor = waits ? context.Panels.Dim : Shade(result.Outcome),
            Detail = Took([result]),
            Tooltip = result.FullName,
            Actions = RowActions(target),
            Tag = new SideTag(Pressed: () => Pressed(target), Menu: _ => Menu(target), Key: key => RowKey(target, key), Id: id)
        };

        if (result.Outcome == DotnetTestOutcome.Failed)
        {
            row.Expandable = true;
            Remember(row, id, true);
            row.Children = [FailureRow(test, result)];
        }

        return row;
    }

    private UITreeItem FrameworkRow(string project, DotnetTest test, string framework, List<DotnetTestResult> cases, bool testWaits)
    {
        var outcome = cases.Any(result => result.Outcome == DotnetTestOutcome.Failed) ? DotnetTestOutcome.Failed
            : cases.Any(result => result.Outcome == DotnetTestOutcome.Passed) ? DotnetTestOutcome.Passed : cases[0].Outcome;
        bool waits = (testWaits || cases.Any(result => waiting.Contains(Identity(project, result.FullName)))) && FrameworkWaiting(project, framework);
        string name = framework.Length > 0 ? framework : context.Strings.Said("tests.default_framework");
        var target = new TestTarget(project, [test.FullName], test.Method + " (" + name + ")", test.FullName, () => GoTo(test), framework.Length > 0 ? framework : null);
        string id = "framework|" + project + "|" + test.FullName + "|" + framework;
        targets[id] = target;
        var row = new UITreeItem(name, waits ? "clock" : Icon(outcome))
        {
            IconColor = waits ? context.Panels.Dim : Shade(outcome), Detail = Took(cases),
            Badge = cases.Count > 1 ? Number(cases.Count) : string.Empty, Actions = RowActions(target),
            Tag = new SideTag(Pressed: () => Pressed(target), Menu: _ => Menu(target), Key: key => RowKey(target, key), Id: id)
        };
        var children = cases.Select(result => result.IsCase ? CaseRow(project, test, result, waits)
            : result.Outcome == DotnetTestOutcome.Failed ? FailureRow(test, result) : null).OfType<UITreeItem>().ToList();
        if (children.Count > 0)
        {
            row.Expandable = true;
            row.Children = children;
            Remember(row, id, outcome == DotnetTestOutcome.Failed);
        }
        return row;
    }

    // What failed, the first line of its message, all of it and the stack in its tooltip, a click going where it failed
    private UITreeItem FailureRow(DotnetTest test, DotnetTestResult failure)
    {
        string message = failure.Message.Split('\n')[0].Trim();
        return new UITreeItem(message.Length > 0 ? message : context.Strings.Said("tests.failed"), "warning")
        {
            Color = context.Panels.Error,
            Tooltip = $"{failure.Message}\n\n{failure.Stack}",
            Tag = new SideTag(Pressed: () => GoTo(test, failure), Id: "failure|" + test.Project + "|" + failure.Framework + "|" + failure.FullName)
        };
    }

    // The rows opened or closed by hand, by what they're of, so a refresh (a run, a file opened from a row) leaves them
    // as they were rather than open only where something failed. It only grows by the rows there are. While the search
    // has something in it every row starts open, so what it found shows, and what's opened or closed then is kept apart
    // and forgotten as the search is cleared
    private readonly Dictionary<string, bool> opened = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> openedSearching = new(StringComparer.Ordinal);

    // A row open the way it was left, or the way it starts when it never was
    private void Remember(UITreeItem row, string key, bool otherwise)
    {
        Dictionary<string, bool> kept = searching ? openedSearching : opened;
        row.Expanded = kept.TryGetValue(key, out bool open) ? open : otherwise || searching;
        row.Tag = (row.Tag as SideTag ?? new SideTag()) with { Toggled = toggled => kept[key] = toggled.Expanded };
    }

    // A test's declaration, or where a result of it failed
    private void GoTo(DotnetTest test, DotnetTestResult? result = null)
    {
        if (result?.At is { } at)
            context.Documents.OpenAt(at.File, at.Line, 0);
        else if (test.File.Length > 0)
            context.Documents.OpenAt(test.File, test.Line, 0);
    }

    private static string Icon(DotnetTestOutcome outcome) => outcome switch
    {
        DotnetTestOutcome.Passed => "pass",
        DotnetTestOutcome.Failed => "error",
        DotnetTestOutcome.Skipped => "circle-slash",
        _ => "circle-outline"
    };

    private System.Numerics.Vector4 Shade(DotnetTestOutcome outcome) => outcome switch
    {
        DotnetTestOutcome.Passed => Passed,
        DotnetTestOutcome.Failed => context.Panels.Error,
        DotnetTestOutcome.Skipped => context.Panels.Warning,
        _ => context.Panels.Dim
    };

    // The results of a test, a theory's every case of it
    private IEnumerable<DotnetTestResult> ResultsOf(DotnetTest test) =>
        results.TryGetValue(Identity(test.Project, test.FullName), out Dictionary<string, DotnetTestResult>? cases) ? cases.Values : [];

    // How a test came out, a theory failed if any case did
    private DotnetTestOutcome Outcome(DotnetTest test)
    {
        DotnetTestOutcome outcome = DotnetTestOutcome.NotRun;
        foreach (DotnetTestResult result in ResultsOf(test))
        {
            if (result.Outcome == DotnetTestOutcome.Failed)
                return DotnetTestOutcome.Failed;
            if (outcome == DotnetTestOutcome.NotRun || result.Outcome == DotnetTestOutcome.Passed)
                outcome = result.Outcome;
        }

        return outcome;
    }

    // How long some results took together, said the way VS says it
    private static string Took(IEnumerable<DotnetTestResult> of)
    {
        TimeSpan took = TimeSpan.Zero;
        bool any = false;
        foreach (DotnetTestResult result in of)
        {
            took += result.Duration;
            any = true;
        }

        return !any ? string.Empty : took.TotalSeconds >= 1 ? $"{took.TotalSeconds:0.0} s" : took.TotalMilliseconds < 1 ? "< 1 ms" : $"{took.TotalMilliseconds:0} ms";
    }
}
