using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

/// <summary>
/// What a breakpoint has to it beyond its line, Visual Studio's breakpoint settings. A condition it only stops on when
/// it's true, a hit count, a message it logs instead of stopping, and whether it's on at all.
/// </summary>
internal sealed record HexBreakpoint(string? Condition = null, DebugHitCondition? Hits = null, string? LogMessage = null, bool Enabled = true)
{
    /// <summary>Whether there's nothing to it but the line, the kind not kept.</summary>
    public bool Plain => Condition is null && Hits is null && LogMessage is null && Enabled;

    /// <summary>How the gutter draws it.</summary>
    public CodeBreakpointMark Mark =>
        (LogMessage is not null ? CodeBreakpointMark.Log : Condition is not null || Hits is not null ? CodeBreakpointMark.Conditional : CodeBreakpointMark.Plain)
        | (Enabled ? CodeBreakpointMark.Plain : CodeBreakpointMark.Disabled);
}

// Breakpoints the way Visual Studio's go. A right click on the dot in the gutter, or on a breakpoint's row in the Debug
// side, has its settings, a condition, a hit count (=, >= or a multiple of), a message to log instead of stopping (a
// tracepoint, {expressions} in it evaluated), and turning it off without taking it away. A right click in the gutter
// where there isn't one puts one down, a conditional one or a tracepoint. The gutter draws each its own way, a dot with
// bars for a condition or a count, a diamond for a message, the outline of one that's off.
internal sealed partial class HexDebugging
{
    // What the breakpoints that aren't plain have to them, by the file's whole path and then the line
    private readonly Dictionary<string, Dictionary<int, HexBreakpoint>> breakpointSettings = new(StringComparer.OrdinalIgnoreCase);

    // How each file's editor draws its breakpoints, the same object for every editor of the file
    private readonly Dictionary<string, Dictionary<int, CodeBreakpointMark>> breakpointMarks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What a breakpoint has to it, a plain one's nothing.</summary>
    internal HexBreakpoint SettingsOf(string full, int line) =>
        breakpointSettings.TryGetValue(full, out var lines) && lines.TryGetValue(line, out HexBreakpoint? settings) ? settings : new HexBreakpoint();

    /// <summary>A breakpoint's settings changed, put down first if it isn't there, and the debugger told.</summary>
    internal void Settle(string full, int line, HexBreakpoint settings)
    {
        BreakpointsOf(full).Add(line);
        if (!breakpointSettings.TryGetValue(full, out var lines))
            breakpointSettings[full] = lines = [];
        if (settings.Plain)
            lines.Remove(line);
        else
            lines[line] = settings;

        BreakpointsChanged(full);
    }

    /// <summary>A file's breakpoints changed, its editors drawn again with them and the debugger told.</summary>
    private void BreakpointsChanged(string full)
    {
        SortedSet<int> lines = BreakpointsOf(full);
        if (breakpointTracking.TryGetValue(full, out var tracking))
        {
            tracking.Synchronize(VisibleBreakpoints(full));
            if (lines.Count == 0) breakpointTracking.Remove(full);
        }
        if (breakpointSettings.TryGetValue(full, out var settings))
        {
            foreach (int gone in settings.Keys.Where(line => !lines.Contains(line)).ToArray())
                settings.Remove(gone);
        }

        Dictionary<int, CodeBreakpointMark> marks = MarksOf(full);
        marks.Clear();
        if (settings is not null)
        {
            foreach (var (line, setting) in settings)
                marks[line] = setting.Mark;
        }

        foreach (HexDocument open in context.Documents.All)
        {
            if (open.Path is { } path && PlatformPaths.Same(path, full))
                open.Editor.BreakpointMarksVersion++;
        }

        debugging?.SetBreakpoints(full, DebugBreakpointsOf(full));
        KeepBreakpoints();
        context.Panels.RefreshSide(HexSide.Debug);
    }

    private Dictionary<int, CodeBreakpointMark> MarksOf(string full)
    {
        if (!breakpointMarks.TryGetValue(full, out var marks))
            breakpointMarks[full] = marks = [];
        return marks;
    }

    /// <summary>The breakpoints of a file that are on, as the debugger is given them.</summary>
    internal IReadOnlyCollection<DebugBreakpoint> DebugBreakpointsOf(string full)
    {
        var on = new List<DebugBreakpoint>();
        foreach (int line in BreakpointsOf(full))
        {
            HexBreakpoint settings = SettingsOf(full, line);
            if (settings.Enabled)
                on.Add(new DebugBreakpoint(line, settings.Condition, settings.Hits, settings.LogMessage));
        }

        return on;
    }

    /// <summary>Every file's breakpoints that are on, for a launch or an attach.</summary>
    internal Dictionary<string, IReadOnlyCollection<DebugBreakpoint>> BreakpointsNow()
    {
        var now = new Dictionary<string, IReadOnlyCollection<DebugBreakpoint>>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in breakpoints.Keys)
        {
            if (DebugBreakpointsOf(file) is { Count: > 0 } on)
                now[file] = on;
        }

        return now;
    }

    /* Menus */

    /// <summary>
    /// A right click in the gutter's strip of breakpoints, the menu of the one there, or one to put one down. False when
    /// the click was anywhere else, and it's the code's menu.
    /// </summary>
    private bool GutterMenu(HexDocument at, string full, Vector2 point)
    {
        int line = at.Editor.BreakpointLineAt(point);
        if (line < 0)
            return false;

        BreakpointMenu(full, line);
        return true;
    }

    /// <summary>The menu of a line's breakpoint, its settings, or putting one down where there isn't one.</summary>
    internal void BreakpointMenu(string full, int line)
    {
        var menu = new Menu("breakpoint");
        if (!BreakpointsOf(full).Contains(line))
        {
            menu.Add(context.Strings.Said("debugging.insert_breakpoint"), () => ToggleBreakpoint(full, line), "F9", "debug-breakpoint");
            menu.Add(context.Strings.Said("debugging.insert_conditional"), () => AskCondition(full, line), icon: "debug-breakpoint-conditional");
            menu.Add(context.Strings.Said("debugging.insert_tracepoint"), () => AskLogMessage(full, line), icon: "debug-breakpoint-log");
            context.Windows.Menu(menu);
            return;
        }

        HexBreakpoint settings = SettingsOf(full, line);
        menu.Add(context.Strings.Said("debugging.condition"), () => AskCondition(full, line), icon: "debug-breakpoint-conditional");
        menu.Add(context.Strings.Said("debugging.hit_count"), () => AskHitCount(full, line), icon: "symbol-number");
        menu.Add(context.Strings.Said("debugging.log_message"), () => AskLogMessage(full, line), icon: "debug-breakpoint-log");
        menu.AddSeparator();
        menu.Add(context.Strings.Said(settings.Enabled ? "debugging.disable_breakpoint" : "debugging.enable_breakpoint"),
            () => Settle(full, line, SettingsOf(full, line) with { Enabled = !SettingsOf(full, line).Enabled }), "Ctrl+F9", settings.Enabled ? "circle" : "circle-filled");
        menu.Add(context.Strings.Said("debugging.delete_breakpoint"), () => ToggleBreakpoint(full, line), icon: "trash");
        menu.AddSeparator();
        menu.Add(context.Strings.Said("debugging.go_to_breakpoint"), () => context.Documents.OpenAt(full, line, 0), icon: "go-to-file");
        context.Windows.Menu(menu);
    }

    // The menu of the Breakpoints section, all of them turned on, off or taken away
    private void AllBreakpointsMenu()
    {
        var menu = new Menu("breakpoints");
        menu.Add(context.Strings.Said("debugging.enable_all"), () => EnableAll(true), icon: "circle-filled").IsEnabled = CanChangeAllBreakpoints;
        menu.Add(context.Strings.Said("debugging.disable_all"), () => EnableAll(false), icon: "circle").IsEnabled = CanChangeAllBreakpoints;
        menu.Add(context.Strings.Said("debugging.delete_all"), DeleteAllBreakpoints, icon: "trash").IsEnabled = CanChangeAllBreakpoints;
        context.Windows.Menu(menu);
    }

    private void EnableAll(bool on)
    {
        if (!CanChangeAllBreakpoints()) return;
        EnableAllDataBreakpoints(on);
        if (debugging is {} sourceSession && sourceSession.SourceBreakpointSets.Count > 0)
            ChangeSourceBreakpoints(sourceSession, () => sourceSession.EnableSourceBreakpoints(on));
        if (debugging is {} instructionSession && instructionSession.InstructionBreakpoints.Count > 0)
            ChangeInstructionBreakpoints(instructionSession, () => instructionSession.EnableInstructionBreakpoints(on));
        foreach (string name in functionBreakpoints.Keys.ToArray()) functionBreakpoints[name] = on;
        FunctionBreakpointsChanged();
        foreach (var (file, lines) in breakpoints.ToArray())
        {
            foreach (int line in lines.ToArray())
            {
                HexBreakpoint settings = SettingsOf(file, line);
                if (settings.Enabled != on)
                    Settle(file, line, settings with { Enabled = on });
            }
        }
    }

    private void DeleteAllBreakpoints()
    {
        if (!CanChangeAllBreakpoints()) return;
        DeleteAllDataBreakpoints();
        if (debugging is {} sourceSession && sourceSession.SourceBreakpointSets.Count > 0)
            ChangeSourceBreakpoints(sourceSession, sourceSession.DeleteSourceBreakpoints);
        if (debugging is {} instructionSession && instructionSession.InstructionBreakpoints.Count > 0)
            ChangeInstructionBreakpoints(instructionSession, instructionSession.ClearInstructionBreakpoints);
        functionBreakpoints.Clear();
        FunctionBreakpointsChanged();
        foreach (var (file, lines) in breakpoints.ToArray())
        {
            if (lines.Count == 0)
                continue;
            lines.Clear();
            BreakpointsChanged(file);
        }
    }

    /// <summary>Ctrl+F9, the breakpoint on the caret's line turned off or on again, Visual Studio's key.</summary>
    internal void ToggleBreakpointEnabledHere()
    {
        HexDocument shown = context.Documents.Current;
        if (IsDebuggerSourcePath(shown.Path)) { ToggleSourceBreakpoint(shown, shown.Editor.Document.Caret.Line, enableOnly: true); return; }
        if (IsDisassemblyPath(shown.Path)) { ToggleInstruction(shown, shown.Editor.Document.Caret.Line, enableOnly: true); return; }
        if (!shown.IsText || (!SupportsDebugging(shown.Path)))
            return;

        string full = Path.GetFullPath(shown.Path!);
        int line = shown.Editor.Document.Caret.Line;
        if (BreakpointsOf(full).Contains(line))
            Settle(full, line, SettingsOf(full, line) with { Enabled = !SettingsOf(full, line).Enabled });
    }

    /* Asking for the settings */

    private void AskCondition(string full, int line) =>
        context.Windows.Prompt(context.Strings.Said("debugging.condition_title"), context.Strings.Said("debugging.condition_text"),
            SettingsOf(full, line).Condition ?? string.Empty, context.Strings.Said("debugging.set_button"), "i == 4", typed =>
            {
                string condition = typed.Trim();
                Settle(full, line, SettingsOf(full, line) with { Condition = condition.Length > 0 ? condition : null });
                SayConditionProblem(full, line);
            });

    /* What's wrong with a condition */

    // The conditions that couldn't be evaluated where they stopped, what they were and why, by the breakpoint
    private readonly Dictionary<(string File, int Line), (string Condition, string Why)> conditionFailures = [];

    /// <summary>
    /// What's wrong with a breakpoint's condition in words, what it said the last time it couldn't be evaluated, or what
    /// makes it no C# at all. Null for none, and for one with nothing wrong that can be seen.
    /// </summary>
    internal string? ConditionProblemOf(string full, int line)
    {
        if (SettingsOf(full, line).Condition is not { } condition)
            return null;
        if (conditionFailures.TryGetValue((full, line), out var failed) && failed.Condition == condition)
            return failed.Why;
        return HexDebugExpressions.ConditionProblem(condition) is { } key ? context.Strings.Said(key) : null;
    }

    // A condition just set that won't evaluate said along the bottom and in the output, the row says it too
    private void SayConditionProblem(string full, int line)
    {
        if (ConditionProblemOf(full, line) is not { } why)
            return;

        string said = context.Strings.Said("debugging.condition_bad", ("condition", SettingsOf(full, line).Condition!), ("file", Path.GetFileName(full)),
            ("line", (line + 1).ToString(CultureInfo.InvariantCulture)), ("why", why));
        context.Notices.Say(said, error: true);
    }

    /// <summary>
    /// A stop at a breakpoint whose condition couldn't be evaluated, the way Visual Studio stops and says so rather than
    /// going on as if it were false. Kept for its row, and the words along the bottom (and in the output) for it.
    /// </summary>
    private string ConditionFailed(DebugConditionFailure failed)
    {
        string why = failed.CameTo is { } value ? context.Strings.Said("debugging.condition_not_bool", ("value", value)) : failed.Error ?? "?";
        string full = Path.GetFullPath(failed.File);
        conditionFailures[(full, failed.Line)] = (failed.Condition, why);
        return context.Strings.Said("debugging.condition_failed", ("condition", failed.Condition), ("file", Path.GetFileName(full)),
            ("line", (failed.Line + 1).ToString(CultureInfo.InvariantCulture)), ("why", why));
    }

    private void AskHitCount(string full, int line) =>
        context.Windows.Prompt(context.Strings.Said("debugging.hit_count_title"), context.Strings.Said("debugging.hit_count_text"),
            HitsText(SettingsOf(full, line).Hits), context.Strings.Said("debugging.set_button"), ">= 5", typed =>
            {
                if (typed.Trim().Length == 0)
                {
                    Settle(full, line, SettingsOf(full, line) with { Hits = null });
                    return;
                }

                if (HitsIn(typed) is not { } hits)
                {
                    context.Notices.Say(context.Strings.Said("debugging.hit_count_bad", ("typed", typed.Trim())), error: true);
                    return;
                }

                Settle(full, line, SettingsOf(full, line) with { Hits = hits });
            });

    private void AskLogMessage(string full, int line) =>
        context.Windows.Prompt(context.Strings.Said("debugging.log_message_title"), context.Strings.Said("debugging.log_message_text"),
            SettingsOf(full, line).LogMessage ?? string.Empty, context.Strings.Said("debugging.set_button"), "i is {i} in $FUNCTION", typed =>
            {
                string message = typed.Trim();
                Settle(full, line, SettingsOf(full, line) with { LogMessage = message.Length > 0 ? message : null });
            });

    private static readonly Regex HitsSaid = new(@"^\s*(?:(?<kind>==?|>=|%|multiple\s+of|every)\s*)?(?<count>\d+)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// A hit count as typed, 5 or = 5 for the fifth hit, >= 5 for the fifth and every one after, % 5 or "multiple of 5"
    /// for every fifth. Null for anything else, and for 0.
    /// </summary>
    internal static DebugHitCondition? HitsIn(string typed)
    {
        if (HitsSaid.Match(typed) is not { Success: true } said || !int.TryParse(said.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count <= 0)
            return null;

        string kind = said.Groups["kind"].Value.ToLowerInvariant();
        return new DebugHitCondition(kind switch
        {
            ">=" => DebugHitKind.AtLeast,
            "%" or "every" => DebugHitKind.MultipleOf,
            _ when kind.StartsWith("multiple", StringComparison.Ordinal) => DebugHitKind.MultipleOf,
            _ => DebugHitKind.Equal
        }, count);
    }

    /// <summary>A hit count the way it's typed back, empty for none.</summary>
    internal static string HitsText(DebugHitCondition? hits) => hits switch
    {
        null => string.Empty,
        { Kind: DebugHitKind.Equal } equal => "= " + equal.Count.ToString(CultureInfo.InvariantCulture),
        { Kind: DebugHitKind.AtLeast } least => ">= " + least.Count.ToString(CultureInfo.InvariantCulture),
        { } every => "% " + every.Count.ToString(CultureInfo.InvariantCulture)
    };

    /* The Breakpoints section */

    private UITreeItem BreakpointsSection()
    {
        var points = new UITreeItem(context.Strings.Said("debugging.breakpoints"), "debug-breakpoint")
        {
            Expandable = true,
            Expanded = true,
            Children = [],
            Tag = new SideTag(Menu: _ => AllBreakpointsMenu())
        };

        foreach (var (file, lines) in breakpoints.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (int line in lines)
            {
                string at = file;
                int on = line;
                HexBreakpoint settings = SettingsOf(file, line);

                // A condition that won't evaluate says why on its row in the error colour, Visual Studio's breakpoint
                // window has a warning on it
                string? problem = ConditionProblemOf(file, line);
                string detail = DetailOf(settings);
                points.Children.Add(new UITreeItem($"{Path.GetFileName(file)}:{line + 1}", problem is null ? IconOf(settings) : "warning")
                {
                    IconColor = problem is not null ? context.Panels.Error : settings.Enabled ? context.Panels.Error : context.Panels.Dim,
                    Color = settings.Enabled ? null : context.Panels.Dim,
                    Detail = problem is not null ? context.Strings.Said("debugging.condition_problem", ("why", problem)) : detail.Length > 0 ? detail : context.Workspace.Relative(file),
                    DetailColor = problem is not null ? context.Panels.Error : null,
                    Tooltip = context.Workspace.Relative(file) + (detail.Length > 0 ? "\n" + detail : string.Empty) + (problem is not null ? "\n" + problem : string.Empty),
                    Tag = new SideTag(Pressed: () => context.Documents.OpenAt(at, on, 0), Menu: _ => BreakpointMenu(at, on))
                });
            }
        }

        AddFunctionBreakpointRows(points);
        AddDataBreakpointRows(points);
        AddInstructionBreakpointRows(points);
        AddSourceReferenceBreakpointRows(points);
        if (points.Children.Count == 0)
            points.Children.Add(NoteItem(context.Strings.Said("debugging.breakpoints_hint")));
        return points;
    }

    private static string IconOf(HexBreakpoint settings) => settings.Mark switch
    {
        var mark when (mark & CodeBreakpointMark.Log) != 0 => (mark & CodeBreakpointMark.Disabled) != 0 ? "debug-breakpoint-log-unverified" : "debug-breakpoint-log",
        var mark when (mark & CodeBreakpointMark.Conditional) != 0 => (mark & CodeBreakpointMark.Disabled) != 0 ? "debug-breakpoint-conditional-unverified" : "debug-breakpoint-conditional",
        CodeBreakpointMark.Disabled => "circle",
        _ => "debug-breakpoint"
    };

    // What a breakpoint has to it said in a line, its condition, its hit count, its message and the hits so far
    private string DetailOf(HexBreakpoint settings)
    {
        var parts = new List<string>();
        if (settings.Condition is { } condition)
            parts.Add(context.Strings.Said("debugging.when", ("condition", condition)));
        if (settings.Hits is { } hits)
            parts.Add(context.Strings.Said("debugging.hits", ("hits", HitsText(hits))));
        if (settings.LogMessage is { } log)
            parts.Add(context.Strings.Said("debugging.logs", ("message", log)));
        if (!settings.Enabled)
            parts.Add(context.Strings.Said("debugging.off"));
        return string.Join(", ", parts);
    }
}
