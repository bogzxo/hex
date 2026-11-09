using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class HexDebugging
{
    private bool exceptionUpdatePending;
    internal IReadOnlyList<DebugExceptionFilter> ExceptionFilters => debugging?.ExceptionFilters ?? [];
    internal bool BreaksOnException(string id) => debugging?.BreaksOnException(id) == true;

    private void ExceptionSettings()
    {
        if (exceptionUpdatePending || debugging is not { } session) return;
        var menu = new Menu("exception-filters");
        foreach (var filter in session.ExceptionFilters)
        {
            bool enabled = session.BreaksOnException(filter.Id);
            menu.Add(filter.Label, () => SetExceptionFilter(filter.Id, !enabled), icon: enabled ? "circle-filled" : "circle");
        }
        context.Windows.Menu(menu);
    }

    internal void SetExceptionFilter(string id, bool enabled)
    {
        if (exceptionUpdatePending || debugging is not { } session) return;
        exceptionUpdatePending = true;
        string kind = debuggingKind;
        string[] filters = session.ExceptionFilters.Where(filter => filter.Id == id ? enabled : session.BreaksOnException(filter.Id)).Select(filter => filter.Id).ToArray();
        _ = Change();
        async Task Change()
        {
            try
            {
                await session.SetExceptionBreakpoints(filters).ConfigureAwait(false);
                debugWork.Enqueue(() =>
                {
                    if (!ReferenceEquals(debugging, session)) return;
                    context.Settings.Values.ExceptionBreakpoints[kind + "/" + id] = enabled;
                    context.Settings.Save();
                });
            }
            catch (Exception error)
            {
                debugWork.Enqueue(() => context.Notices.Say(context.Strings.Said("debugging.exception_settings_failed", ("why", error.Message)), error: true));
            }
            finally { debugWork.Enqueue(() => exceptionUpdatePending = false); }
        }
    }
}
