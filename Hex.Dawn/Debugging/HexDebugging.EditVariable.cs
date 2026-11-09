namespace Hex.Dawn;

internal sealed partial class HexDebugging
{
    private bool editingVariable;
    internal bool EditingVariable => editingVariable;

    private void EditVariable(int reference, DebugVariable variable)
    {
        if (variable.ReadOnly || debugging is not { CanSetVariable: true } session) return;
        EditValue(variable.Name, variable.Value, session, (value, _) => session.SetVariable(reference, variable.Name, value));
    }

    private void EditWatchValue(string expression, DebugVariable variable, Func<bool> current)
    {
        if (variable.ReadOnly || debugging is not { CanSetExpression: true } session) return;
        EditValue(expression, variable.Value, session, (value, frame) => session.SetExpression(expression, value, frame),
            () => watches.Contains(expression) && current());
    }

    private void EditValue(string name, string initial, DebugSession session, Func<string, int, Task> set, Func<bool>? available = null)
    {
        if (editingVariable || !DebugStopped || !ReferenceEquals(debugging, session) || stoppedFrames.Count == 0) return;
        int stop = stops, frame = stoppedFrames[stoppedFrame].Id;
        bool Current() => DebugStopped && ReferenceEquals(debugging, session) && stops == stop
            && stoppedFrames.Count > stoppedFrame && stoppedFrames[stoppedFrame].Id == frame && available?.Invoke() != false;
        if (!Current()) return;
        context.Windows.Prompt(context.Strings.Said("debugging.edit_value"), name, initial,
            context.Strings.Said("debugging.set_value"), string.Empty, value =>
            {
                if (!Current() || editingVariable) return;
                editingVariable = true;
                _ = Change(value);
            });
        async Task Change(string value)
        {
            Exception? failure = null;
            try { await set(value, frame).ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            debugWork.Enqueue(() =>
            {
                editingVariable = false;
                if (!Current()) return;
                if (failure is not null)
                    context.Notices.Say(context.Strings.Said("debugging.set_value_failed", ("why", failure.Message)), error: true);
                else
                {
                    stops++; // Retire pending hovers and evaluations of values before the edit.
                    variables.Reset();
                    locals = null;
                    insides.Clear();
                    RefreshPinnedTip();
                    context.Panels.RefreshSide(HexSide.Debug);
                }
            });
        }
    }
}
