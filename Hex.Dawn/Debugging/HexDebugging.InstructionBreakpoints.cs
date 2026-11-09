namespace Hex.Dawn;

internal sealed partial class HexDebugging
{
    private sealed record InstructionView(WeakReference<HexDocument> Document, DebugSession Session, int Stop, int Version, IReadOnlyList<DebugInstruction> Instructions);
    private InstructionView? instructionView;
    private int instructionViewNumber;

    private bool InstructionAt(HexDocument document, int line, out DebugSession session, out DebugInstruction instruction)
    {
        session = null!; instruction = default;
        if (context.Documents.Current != document || !DebugStopped || instructionView is not {} view || debugging != view.Session || !view.Session.CanSetInstructionBreakpoints
            || stops != view.Stop || !view.Document.TryGetTarget(out var bound) || bound != document || document.Editor.Document.Version != view.Version
            || line < 0 || line >= view.Instructions.Count) return false;
        session = view.Session; instruction = view.Instructions[line];
        return true;
    }

    private bool CanToggleInstructionHere() => InstructionAt(context.Documents.Current, context.Documents.Current.Editor.Document.Caret.Line, out _, out _);

    private void BindInstructions(HexDocument document, DebugSession session, IReadOnlyList<DebugInstruction> instructions)
    {
        var weak = new WeakReference<HexDocument>(document);
        instructionView = new(weak, session, stops, document.Editor.Document.Version, instructions);
        int number = ++instructionViewNumber;
        document.Editor.OnGutterPressed = line => { if (number == instructionViewNumber && weak.TryGetTarget(out var target)) ToggleInstruction(target, line); };
        RefreshInstructionMarks();
    }

    private void ToggleInstruction(HexDocument document, int line, bool enableOnly = false)
    {
        if (!InstructionAt(document, line, out var session, out var instruction)) return;
        var point = session.InstructionBreakpoints.FirstOrDefault(point => DebugSession.SameInstructionAddress(point.Address, instruction.Address));
        if (enableOnly)
        {
            if (point is not null) ChangeInstructionBreakpoints(session, () => session.EnableInstructionBreakpoint(point.Address, !point.Enabled));
        }
        else ChangeInstructionBreakpoints(session, () => point is null ? session.AddInstructionBreakpoint(instruction) : session.RemoveInstructionBreakpoint(point.Address));
    }

    private void ChangeInstructionBreakpoints(DebugSession session, Func<Task<bool>> change)
    {
        _ = Change();
        async Task Change()
        {
            Exception? failed = null;
            bool changed = false;
            try { changed = await change().ConfigureAwait(false); }
            catch (Exception error) { failed = error; }
            debugWork.Enqueue(() =>
            {
                if (debugging != session) return;
                string? why = failed?.Message ?? (changed ? session.InstructionBreakpoints.FirstOrDefault(point => point.Enabled && !point.Verified)?.Message : null);
                if (failed is not null || changed && session.InstructionBreakpoints.Any(point => point.Enabled && !point.Verified))
                    context.Notices.Say(context.Strings.Said("debugging.instruction_breakpoint_failed", ("why", why ?? context.Strings.Said("debugging.instruction_breakpoint_unverified"))), error: true);
                RefreshInstructionMarks();
                context.Panels.RefreshSide(HexSide.Debug);
            });
        }
    }

    private void RefreshInstructionMarks()
    {
        if (instructionView is not {} view || debugging != view.Session || !view.Document.TryGetTarget(out var document)
            || document.Editor.Document.Version != view.Version) return;
        var placed = view.Session.InstructionBreakpoints;
        var lines = new SortedSet<int>();
        var marks = new Dictionary<int, CodeBreakpointMark>();
        for (int line = 0; line < view.Instructions.Count; line++)
            if (placed.FirstOrDefault(point => DebugSession.SameInstructionAddress(point.Address, view.Instructions[line].Address)) is {} point)
            {
                lines.Add(line);
                marks[line] = point.Enabled && point.Verified ? CodeBreakpointMark.Plain : CodeBreakpointMark.Disabled;
            }
        document.Editor.Breakpoints = lines;
        document.Editor.BreakpointMarks = marks;
    }

    private void ClearInstructionView()
    {
        if (instructionView?.Document.TryGetTarget(out var document) == true)
        {
            document.Editor.Breakpoints = null;
            document.Editor.BreakpointMarks = null;
            document.Editor.OnGutterPressed = null;
        }
        instructionView = null;
        instructionViewNumber++;
    }

    private void AddInstructionBreakpointRows(UITreeItem section)
    {
        if (debugging is not {} session) return;
        foreach (var point in session.InstructionBreakpoints)
        {
            string detail = !point.Enabled ? context.Strings.Said("debugging.off") : point.Verified
                ? context.Strings.Said("debugging.instruction_breakpoint_verified") : point.Message ?? context.Strings.Said("debugging.instruction_breakpoint_unverified");
            section.Children!.Add(new UITreeItem(point.Address + "  " + point.Description, point.Enabled && point.Verified ? "debug-breakpoint" : "circle")
            {
                Detail = detail, Tooltip = point.Address + "\n" + detail, IconColor = context.Panels.Error,
                Color = point.Enabled ? null : context.Panels.Dim,
                Tag = new SideTag(Pressed: () => { if (debugging == session && DebugStopped) ShowDisassemblyAt(point.Address); }, Menu: _ =>
                {
                    if (debugging != session) return;
                    var menu = new Menu("instruction-breakpoint");
                    menu.Add(context.Strings.Said(point.Enabled ? "debugging.disable_breakpoint" : "debugging.enable_breakpoint"),
                        () => ChangeInstructionBreakpoints(session, () => session.EnableInstructionBreakpoint(point.Address, !point.Enabled)), icon: "circle").IsEnabled = () => debugging == session && DebugStopped;
                    menu.Add(context.Strings.Said("debugging.delete_breakpoint"),
                        () => ChangeInstructionBreakpoints(session, () => session.RemoveInstructionBreakpoint(point.Address)), icon: "trash").IsEnabled = () => debugging == session && DebugStopped;
                    context.Windows.Menu(menu);
                })
            });
        }
    }
}
