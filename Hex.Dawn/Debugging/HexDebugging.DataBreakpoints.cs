namespace Hex.Dawn;

internal sealed partial class HexDebugging
{
    private bool CanChangeAllBreakpoints() => debugging is not { } session || session.DataBreakpoints.Count == 0 && session.InstructionBreakpoints.Count == 0 && !session.HasSourceBreakpointSets || DebugStopped;
    internal void AddDataBreakpointAction(Menu menu, SideTag tag)
    {
        if (!DebugStopped || debugging is not { CanSetDataBreakpoints: true } session || tag.DebugContainer <= 0
            || tag.DebugValue is not {} variable || tag.DebugCurrent is not {} current || !current() || stoppedFrames.Count == 0) return;
        int stop = stops, frame = stoppedFrames[stoppedFrame].Id;
        bool Current() => ReferenceEquals(debugging, session) && DebugStopped && stops == stop && current()
            && stoppedFrames.Count > stoppedFrame && stoppedFrames[stoppedFrame].Id == frame;
        menu.Add(context.Strings.Said("debugging.break_on_change"), () =>
        {
            if (Current()) _ = Prepare();
        }, icon: "debug-breakpoint-data");
        async Task Prepare()
        {
            try
            {
                var info = await session.DataBreakpointInfo(tag.DebugContainer, variable.Name, frame).ConfigureAwait(false);
                debugWork.Enqueue(() =>
                {
                    if (!Current() || info is null) return;
                    if (info.DataId is null || !info.AccessTypes.Contains("write"))
                    {
                        context.Notices.Say(context.Strings.Said("debugging.data_breakpoint_unavailable", ("why", info.Description)), error: true);
                        return;
                    }
                    ChangeDataBreakpoints(session, () => session.AddDataBreakpoint(info));
                });
            }
            catch (Exception error)
            {
                debugWork.Enqueue(() => { if (Current()) DataBreakpointError(error.Message); });
            }
        }
    }

    private void DataBreakpointError(string why) => context.Notices.Say(context.Strings.Said("debugging.data_breakpoint_failed", ("why", why)), error: true);

    private void ChangeDataBreakpoints(DebugSession session, Func<Task<bool>> change)
    {
        _ = Change();
        async Task Change()
        {
            Exception? failure = null;
            bool changed = false;
            try { changed = await change().ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            debugWork.Enqueue(() =>
            {
                if (!ReferenceEquals(debugging, session)) return;
                if (failure is not null) DataBreakpointError(failure.Message);
                else if (changed && session.DataBreakpoints.FirstOrDefault(point => point.Enabled && !point.Verified) is {} rejected)
                    DataBreakpointError(rejected.Message ?? context.Strings.Said("debugging.data_breakpoint_unverified"));
                context.Panels.RefreshSide(HexSide.Debug);
            });
        }
    }

    private void AddDataBreakpointRows(UITreeItem section)
    {
        if (debugging is not {} session) return;
        foreach (var point in session.DataBreakpoints)
        {
            string detail = !point.Enabled ? context.Strings.Said("debugging.off") : point.Verified
                ? context.Strings.Said("debugging.data_breakpoint_verified") : point.Message ?? context.Strings.Said("debugging.data_breakpoint_unverified");
            section.Children!.Add(new UITreeItem(point.Description, point.Enabled && point.Verified ? "debug-breakpoint-data" : "debug-breakpoint-data-unverified")
            {
                Detail = detail, Tooltip = point.Description + "\n" + detail, IconColor = context.Panels.Error,
                Color = point.Enabled ? null : context.Panels.Dim,
                Tag = new SideTag(Menu: _ =>
                {
                    if (!ReferenceEquals(debugging, session)) return;
                    var menu = new Menu("data-breakpoint");
                    menu.Add(context.Strings.Said(point.Enabled ? "debugging.disable_breakpoint" : "debugging.enable_breakpoint"),
                        () => ChangeDataBreakpoints(session, () => session.EnableDataBreakpoint(point.DataId, !point.Enabled)), icon: "circle").IsEnabled = () => ReferenceEquals(debugging, session) && DebugStopped;
                    menu.Add(context.Strings.Said("debugging.delete_breakpoint"),
                        () => ChangeDataBreakpoints(session, () => session.RemoveDataBreakpoint(point.DataId)), icon: "trash").IsEnabled = () => ReferenceEquals(debugging, session) && DebugStopped;
                    context.Windows.Menu(menu);
                })
            });
        }
    }

    private void EnableAllDataBreakpoints(bool enabled)
    {
        if (debugging is { CanSetDataBreakpoints: true } session && session.DataBreakpoints.Count > 0)
            ChangeDataBreakpoints(session, () => session.EnableDataBreakpoints(enabled));
    }

    private void DeleteAllDataBreakpoints()
    {
        if (debugging is { CanSetDataBreakpoints: true } session && session.DataBreakpoints.Count > 0)
            ChangeDataBreakpoints(session, session.ClearDataBreakpoints);
    }
}
