namespace Hex.Dawn;

// What it's stopped with looked at by the expression, VS Code's watch, its debug console and its data tips. Watches are
// expressions evaluated every time it stops, in the frame looked at, in a section of the Debug side of their own. Evaluate
// Expression (Shift+F9, Visual Studio's QuickWatch key) asks for one, the word at the caret to start with, and what it
// came to goes in the Immediate section of the Debug side, opened out like a variable (HexDebugging.Immediate). A name
// the pointer rests on while it's stopped says its value in a data tip (HexDebugging.DataTips). C# goes through
// HexDebugEvaluator on the way to netcoredbg, which knows names the way the file does.
internal sealed partial class HexDebugging
{
    // Expressions are kept in order with the workspace and restored when it changes.
    private readonly List<string> watches = [];
    private IReadOnlyList<string>? watchWorkspace;

    // How many times it's stopped, what a data tip or a watch belongs to
    private int stops;

    private void AddWatchCommands()
    {
        watchWorkspace = context.Workspace.WatchExpressions;
        watches.AddRange(watchWorkspace);
        UICommands commands = context.Commands;
        commands.Add("Run/debug", context.Strings.Said("debugging.add_watch"), AddWatch, icon: "eye");
        commands.Add("Run/debug", context.Strings.Said("debugging.evaluate"), EvaluateExpression, "Shift+F9", "debug-console", UIKeyUse.BoundWhileTyping);
        context.Documents.AddHovers(DataTip, first: true);
    }

    /// <summary>Add Watch, an expression asked for, the selection or the name at the caret to start with.</summary>
    private void AddWatch()
    {
        context.Windows.Prompt(context.Strings.Said("debugging.add_watch_title"), context.Strings.Said("debugging.add_watch_text"), ExpressionAtCaret(),
            context.Strings.Said("debugging.add_button"), string.Empty, typed =>
            {
                WatchExpression(typed);
            });
    }

    internal void WatchExpression(string typed)
    {
        string expression = typed.Trim();
        if (expression.Length == 0 || watches.Contains(expression)) return;
        watches.Add(expression);
        context.Workspace.KeepWatchExpressions(watches);
        variables.ForgetWatches();
        context.Panels.ShowSide(HexSide.Debug);
        context.Panels.RefreshSide(HexSide.Debug);
    }

    private void WatchMenu(string expression, DebugVariable? value = null, Func<bool>? current = null)
    {
        if (!watches.Contains(expression)) return;
        var menu = new Menu("watch");
        menu.Add(context.Strings.Said("debugging.edit_watch"), () => EditWatch(expression), icon: "edit");
        if (DebugStopped && debugging is { CanSetExpression: true } && value is { ReadOnly: false } editable && current?.Invoke() == true)
            menu.Add(context.Strings.Said("debugging.edit_value"), () => EditWatchValue(expression, editable, current), icon: "edit");
        menu.Add(context.Strings.Said("debugging.remove_watch"), () => RemoveWatch(expression), icon: "close");
        context.Windows.Menu(menu);
    }

    private void EditWatch(string expression)
    {
        var workspace = context.Workspace.WatchExpressions;
        context.Windows.Prompt(context.Strings.Said("debugging.edit_watch"), context.Strings.Said("debugging.add_watch_text"), expression,
            context.Strings.Said("debugging.set_value"), string.Empty, typed =>
            {
                if (!ReferenceEquals(workspace, context.Workspace.WatchExpressions)) return;
                string replacement = typed.Trim();
                int index = watches.IndexOf(expression);
                if (index < 0 || replacement.Length == 0 || replacement == expression || watches.Contains(replacement)) return;
                watches[index] = replacement;
                context.Workspace.KeepWatchExpressions(watches);
                variables.ForgetWatches();
                context.Panels.RefreshSide(HexSide.Debug);
            });
    }

    private void RestoreWatches()
    {
        watches.Clear();
        watchWorkspace = context.Workspace.WatchExpressions;
        watches.AddRange(watchWorkspace);
        variables.ForgetWatches();
        context.Panels.RefreshSide(HexSide.Debug);
    }

    private void RemoveWatch(string expression)
    {
        watches.Remove(expression);
        context.Workspace.KeepWatchExpressions(watches);
        variables.ForgetWatches();
        context.Panels.RefreshSide(HexSide.Debug);
    }

    /// <summary>Shift+F9, an expression evaluated where it's stopped, what it came to said along the bottom and in the output.</summary>
    private void EvaluateExpression()
    {
        if (!DebugStopped || debugging is not { } session || stoppedFrames.Count == 0)
        {
            context.Notices.Say(context.Strings.Said("debugging.evaluates_when_stopped"));
            return;
        }

        int stop = stops, frame = stoppedFrames[stoppedFrame].Id;
        bool Current() => ReferenceEquals(debugging, session) && DebugStopped && stops == stop && stoppedFrames.Count > stoppedFrame && stoppedFrames[stoppedFrame].Id == frame;
        void Submit(string typed) { if (Current()) EvaluateIntoImmediate(typed.Trim()); }
        if (!session.CanComplete)
        {
            context.Windows.Prompt(context.Strings.Said("debugging.evaluate_title"), context.Strings.Said("debugging.evaluate_text"), ExpressionAtCaret(),
                context.Strings.Said("debugging.evaluate_button"), string.Empty, Submit);
            return;
        }
        context.Windows.PromptExpression(context.Strings.Said("debugging.evaluate_title"), context.Strings.Said("debugging.evaluate_text"), ExpressionAtCaret(),
            context.Strings.Said("debugging.evaluate_button"), stoppedFrames[stoppedFrame].File ?? "expression.py", box =>
            {
                var weak = new WeakReference<CodeEditor>(box);
                bool Active() => Current() && weak.TryGetTarget(out var target) && target.IsFocused;
                var completion = new HexDebugCompletions((text, column) => session.Completions(text, column, frame), Active, debugWork.Enqueue,
                    context.Strings.Said("debugging.asking"));
                box.CompletionSource = completion.Complete;
                box.CompletionAvailable = Active;
            }, Submit);
    }

    /// <summary>The Watch section of the Debug side, what each expression came to while it's stopped, the expressions otherwise.</summary>
    private UITreeItem WatchSection()
    {
        var section = new UITreeItem(context.Strings.Said("debugging.watch"), "eye") { Expandable = true, Expanded = true, Children = [] };
        if (watches.Count == 0)
        {
            section.Children.Add(NoteItem(context.Strings.Said("debugging.watch_hint")));
            return section;
        }

        if (DebugStopped && stoppedFrames.Count > 0)
        {
            DebugSession session = debugging!;
            string? file = stoppedFrames[stoppedFrame].File;
            List<UITreeItem>? watchesShown = variables.Watches(session, stoppedFrames[stoppedFrame].Id, watches,
                (expression, frame) => Evaluate(session, expression, frame, file), session.Variables);
            if (watchesShown is null)
            {
                section.Children.Add(NoteItem(context.Strings.Said("debugging.asking")));
                return section;
            }

            // Keep the value's expansion behavior while giving every watch the same Edit/Remove menu.
            for (int i = 0; i < watchesShown.Count && i < watches.Count; i++)
            {
                string expression = watches[i];
                UITreeItem row = watchesShown[i];
                SideTag tag = row.Tag as SideTag ?? new SideTag();
                DebugVariable? value = tag.DebugValue;
                Func<bool>? current = tag.DebugCurrent;
                row.Tag = tag with { Menu = _ => WatchMenu(expression, value, current) };
                section.Children.Add(row);
            }

            return section;
        }

        foreach (string expression in watches)
            section.Children.Add(new UITreeItem(expression, "symbol-variable") { Color = context.Panels.Dim, Tag = new SideTag(Menu: _ => WatchMenu(expression)) });
        return section;
    }

    /// <summary>
    /// An expression evaluated in a frame of a file, or why it couldn't be. C# goes through the session's evaluator, which
    /// knows names the way the file does, works out what netcoredbg can't (an array's Length among them) and refuses what
    /// it would get wrong.
    /// </summary>
    private async Task<DebugEvaluation> Evaluate(DebugSession session, string expression, int frame, string? file, string context = "watch")
    {
        if (debuggingKind != DebugSession.CORECLR || evaluator is not { } through)
            return await session.EvaluateSaying(expression, frame, context);
        return await through.Evaluate(expression, frame, ScopeOf(file), context);
    }

    // The selection, or the expression at the caret, of the file on screen
    private string ExpressionAtCaret()
    {
        UITextDocument document = context.Documents.Current.Editor.Document;
        if (document.HasSelection && document.SelectionStart.Line == document.SelectionEnd.Line)
            return document.SelectedText.Trim();
        return document.LineCount > 0 ? ExpressionAt(document.LineAt(document.Caret.Line), document.Caret.Column) : string.Empty;
    }

    /// <summary>
    /// The name at a column of a line with what it's a member of before it, "box.area" on area, the name alone on box.
    /// Empty on anything that isn't a name.
    /// </summary>
    internal static string ExpressionAt(string line, int column)
    {
        static bool Named(char c) => char.IsLetterOrDigit(c) || c == '_';

        column = Math.Clamp(column, 0, line.Length);
        int end = column;
        while (end < line.Length && Named(line[end]))
            end++;

        int start = column;
        while (start > 0 && (Named(line[start - 1]) || (line[start - 1] == '.' && start - 1 > 0 && Named(line[start - 2]))))
            start--;

        if (start == end || char.IsDigit(line[start]))
            return string.Empty;
        return line[start..end];
    }
}
