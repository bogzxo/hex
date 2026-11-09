using System.Globalization;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

internal sealed partial class HexDebugging
{
    private int attachChoice;
    private bool attachingProcess;

    internal void DetachOnClose()
    {
        attachChoice++;
        CancelDebugBuild();
        if (debugging is { TerminatesDebuggeeOnStop: false } external)
        {
            // Scene updates have stopped. Acknowledge detach before process cleanup closes the adapter.
            external.Dispose();
            debugging = null;
        }
    }

    private void AttachToProcess()
    {
        if (Debugging) return;
        int choice = ++attachChoice;
        int generation = debugBuildGeneration;
        object workspace = context.Workspace.WatchExpressions;
        bool Current() => choice == attachChoice && generation == debugBuildGeneration && !Debugging && ReferenceEquals(workspace, context.Workspace.WatchExpressions);
        _ = Task.Run(() =>
        {
            IReadOnlyList<HexDebugProcess> processes;
            try { processes = HexDebugProcess.List(); }
            catch (Exception) { processes = []; }
            debugWork.Enqueue(() =>
            {
                if (!Current()) return;
                var choices = new List<(string, Action)>
                {
                    (context.Strings.Said("debugging.attach_pid"), () =>
                    {
                        if (!Current()) return;
                        context.Windows.Prompt(context.Strings.Said("debugging.attach"), context.Strings.Said("debugging.attach_pid_text"), "",
                            context.Strings.Said("debugging.attach_action"), string.Empty, text =>
                            {
                                if (!Current()) return;
                                if (int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int id) && HexDebugProcess.Find(id) is {} process)
                                    AttachProcess(process);
                                else context.Notices.Say(context.Strings.Said("debugging.attach_gone"), error: true);
                            });
                    }),
                    (context.Strings.Said("debugging.attach_refresh"), () => { if (Current()) AttachToProcess(); })
                };
                choices.AddRange(processes.Select(process => (process.Label, (Action)(() => { if (Current()) AttachProcess(process); }))));
                context.Windows.Choose(context.Strings.Said("debugging.attach"), context.Strings.Said("debugging.attach_text"), choices);
            });
        });
    }

    private void AttachProcess(HexDebugProcess process)
    {
        if (Debugging) return;
        if (!process.IsCurrent())
        {
            context.Notices.Say(context.Strings.Said("debugging.attach_gone"), error: true);
            return;
        }
        if (Netcoredbg() is not {} adapter)
        {
            if (!InstallingFirst(() => AttachProcess(process))) context.Notices.Say(NoDebugger, error: true);
            return;
        }
        attachChoice++;
        CancelDebugBuild();
        var attempt = new CancellationTokenSource();
        debugBuild = attempt;
        attachingProcess = true;
        building = true;
        restart = null;
        debuggedProject = process.Label;
        conditionFailures.Clear();
        var breakpointsNow = BreakpointsNow();
        context.Notices.Say(context.Strings.Said("debugging.attaching", ("process", process.Label)));
        context.Panels.ShowSide(HexSide.Debug);
        _ = Task.Run(async () =>
        {
            DebugSession? session = null;
            try
            {
                attempt.Token.ThrowIfCancellationRequested();
                if (!process.IsCurrent()) throw new InvalidOperationException(context.Strings.Said("debugging.attach_gone"));
                session = DebugSession.Start(adapter, ["--interpreter=vscode"], terminateDebuggeeOnStop: false);
                await StartUnder(session, process.Label, DebugSession.CORECLR,
                    started => started.Attach(process.Id, breakpointsNow, expectedStartTime: process.Started), attempt.Token).WaitAsync(attempt.Token);
            }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested) { }
            catch (Exception error)
            {
                PostBuild(attempt.Token, () =>
                {
                    building = false;
                    context.Notices.Say(context.Strings.Said("debugging.attach_failed", ("why", error.Message)), error: true);
                });
            }
            finally
            {
                // Cancellation must close even an adapter whose attach response never arrives and whose queued
                // publication was suppressed. Wait for safe disconnect before closing its transport.
                if (attempt.IsCancellationRequested && session is not null)
                {
                    await session.StopAsync().ConfigureAwait(false);
                    session.Dispose();
                }
                debugWork.Enqueue(() =>
                {
                    if (ReferenceEquals(debugBuild, attempt)) debugBuild = null;
                    attempt.Dispose();
                });
            }
        });
    }
}
