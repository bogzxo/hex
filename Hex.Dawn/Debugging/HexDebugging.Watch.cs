namespace Hex.Dawn;

// What it's stopped with looked at by the expression, VS Code's watch, its debug console and its data tips. Watches are
// expressions evaluated every time it stops, in the frame looked at, in a section of the Debug side of their own. Evaluate
// Expression (Shift+F9, Visual Studio's QuickWatch key) asks for one and says what it came to, the word at the caret to
// start with. A name the pointer rests on while it's stopped says its value in the hover, before what the language server
// says of it.
internal sealed partial class HexDebugging
{
    // The expressions watched, in the order they were added, kept for every session while Hex runs
    private readonly List<string> watches = [];

    // The rows of the watches for the stop and the frame they were asked for, null while they're coming
    private List<UITreeItem>? watchesShown;
    private (int Stop, int Frame) watchesFor = (-1, -1);

    // How many times it's stopped, what a data tip or a watch belongs to
    private int stops;

    private void AddWatchCommands()
    {
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
                string expression = typed.Trim();
                if (expression.Length == 0 || watches.Contains(expression))
                    return;

                watches.Add(expression);
                watchesShown = null;
                context.Panels.ShowSide(HexSide.Debug);
                context.Panels.RefreshSide(HexSide.Debug);
            });
    }

    private void RemoveWatch(string expression)
    {
        watches.Remove(expression);
        watchesShown = null;
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

        int frame = stoppedFrames[stoppedFrame].Id;
        context.Windows.Prompt(context.Strings.Said("debugging.evaluate_title"), context.Strings.Said("debugging.evaluate_text"), ExpressionAtCaret(),
            context.Strings.Said("debugging.evaluate_button"), string.Empty, typed =>
            {
                string expression = typed.Trim();
                if (expression.Length == 0)
                    return;

                _ = Task.Run(async () =>
                {
                    // As a watch, lldb's repl says "(int) $1 = 0" for 0
                    DebugVariable? value = await session.TryEvaluate(expression, frame);
                    debugWork.Enqueue(() =>
                    {
                        if (value is not { } said)
                        {
                            context.Notices.Say(context.Strings.Said("debugging.could_not_evaluate", ("expression", expression)), error: true);
                            return;
                        }

                        // What's said along the bottom goes in the output as well
                        context.Notices.Say(context.Strings.Said("debugging.evaluated", ("expression", expression), ("value", said.Value)));
                    });
                });
            });
    }

    // The watches' rows, each evaluated in the frame looked at off the editor's thread, handed back
    private void AskWatches(int frameId, int frameIndex)
    {
        if (debugging is not { } session)
            return;

        watchesFor = (stops, frameIndex);
        watchesShown = null;
        string[] asked = [.. watches];
        int stop = stops;
        _ = Task.Run(async () =>
        {
            var rows = new List<UITreeItem>();
            foreach (string expression in asked)
            {
                DebugVariable? value = await session.TryEvaluate(expression, frameId);
                UITreeItem row = value is { } said
                    ? VariableItem(session, said)
                    : new UITreeItem($"{expression} = ?", "symbol-variable") { Color = context.Panels.Dim, Detail = context.Strings.Said("debugging.not_here") };
                rows.Add(row);
            }

            debugWork.Enqueue(() =>
            {
                if (watchesFor != (stop, frameIndex))
                    return;
                watchesShown = rows;
                context.Panels.RefreshSide(HexSide.Debug);
            });
        });
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
            if (watchesShown is null || watchesFor != (stops, stoppedFrame))
            {
                section.Children.Add(NoteItem(context.Strings.Said("debugging.asking")));
                AskWatches(stoppedFrames[stoppedFrame].Id, stoppedFrame);
                return section;
            }

            // Each watch's row taken away by its menu, the expression it was asked for being its first
            for (int i = 0; i < watchesShown.Count && i < watches.Count; i++)
            {
                string expression = watches[i];
                UITreeItem row = watchesShown[i];
                row.Tag = row.Tag is SideTag tag ? tag with { Menu = _ => RemoveWatch(expression) } : new SideTag(Menu: _ => RemoveWatch(expression));
                section.Children.Add(row);
            }

            return section;
        }

        foreach (string expression in watches)
            section.Children.Add(new UITreeItem(expression, "symbol-variable") { Color = context.Panels.Dim, Tag = new SideTag(Menu: _ => RemoveWatch(expression)) });
        return section;
    }

    // The last data tip said and the one being asked, by the expression, the frame and the stop
    private (string Expression, int Frame, int Stop, string? Said) tipped;
    private (string? Expression, int Frame, int Stop) tipping;

    /// <summary>
    /// A data tip, what the name under the pointer comes to while it's stopped, before the language server's word on it.
    /// Null while it's asked and for anything the debugger can't say, the server's hover is shown then.
    /// </summary>
    private string? DataTip(HexDocument of, CodeEditor editor, UITextPosition at)
    {
        if (!DebugStopped || debugging is not { } session || stoppedFrames.Count == 0 || (!IsCSharp(of.Path) && !IsD(of.Path)) || at.Line >= editor.Document.LineCount)
            return null;

        string expression = ExpressionAt(editor.Document.LineAt(at.Line), at.Column);
        if (expression.Length == 0)
            return null;

        int frame = stoppedFrames[stoppedFrame].Id;
        if (tipped.Said is not null && (tipped.Expression, tipped.Frame, tipped.Stop) == (expression, frame, stops))
            return tipped.Said.Length > 0 ? tipped.Said : null;

        if (tipping != (expression, frame, stops))
        {
            tipping = (expression, frame, stops);
            int stop = stops;
            _ = Task.Run(async () =>
            {
                DebugVariable? value = await session.TryEvaluate(expression, frame, "hover");
                debugWork.Enqueue(() =>
                {
                    // A function comes to its address, which says less than the server's word on what it does
                    bool tells = value is { } said && said.Type?.Contains('(') != true;
                    tipped = (expression, frame, stop, tells ? TipOf(value!.Value) : string.Empty);
                    if (tells)
                        editor.AskAgain();
                });
            });
        }

        return null;
    }

    // A value as a data tip says it, the expression and what it is with its type under it
    private static string TipOf(DebugVariable value) =>
        value.Type is { Length: > 0 } type ? $"**{value.Name}** = {value.Value}\n\n{type}" : $"**{value.Name}** = {value.Value}";

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
