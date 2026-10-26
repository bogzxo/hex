using System.Runtime.ExceptionServices;

using Horizon.Core.Threading;
using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

/// <summary>
/// One run of an automation of HIDL, on a thread of the work's own from start to end, in a runtime of its own with no
/// second to finish in the way an extension has. It's stopped from its card or the status bar's list of work, which
/// stops the HIDL where it is and kills a program it's waiting on. The hex it's handed is the extensions' with every
/// call that touches the editor done on the editor's thread while this one waits (HexAutomationRun.Api), and what it
/// prints goes in the Output panel and on its card.
/// </summary>
internal sealed partial class HexAutomationRun
{
    private readonly DawnContext context;
    private readonly HexAutomation automation;
    private readonly WorkContext work;
    private readonly Action<Action> post;
    private readonly int editorThread;

    // The runtime it runs in and the hex it was handed, for a function it hands back to be called with
    private HIDLRuntime runtime = null!;
    private ObjectValue hex;

    // Whether the Output panel was brought up for what it printed yet, once a run
    private bool shown;

    /// <param name="post">Something done on the editor's thread at its next tick.</param>
    /// <param name="editorThread">The editor's thread, what's asked of it from there is done straight away.</param>
    public HexAutomationRun(DawnContext with, HexAutomation of, WorkContext going, Action<Action> post, int editorThread)
    {
        context = with;
        automation = of;
        work = going;
        this.post = post;
        this.editorThread = editorThread;
    }

    /// <summary>
    /// Runs it to the end on the thread it's called from. Its file is run again in a runtime with hex in it, so its run
    /// function and everything it calls are this runtime's, then run is called with hex. A script is run with hex in it,
    /// and its own run(hex) after when it declares one.
    /// </summary>
    public void Run(ObjectValue editorHex)
    {
        hex = HexFor(editorHex);
        runtime = new HIDLRuntime { BaseDirectory = automation.Root, Output = Print };
        runtime.Interpreter.Cancellation = work.Token;
        runtime.UserScope.DeclareSystem("hex", hex);
        HexInterop.Declare(runtime, automation.Root);
        work.Say(automation.Title);

        if (automation.Script.Length > 0)
        {
            runtime.RunFile(automation.ScriptPath());
            if (runtime.UserScope.Variables.TryGetValue("run", out IRuntimeValue? declared) && Values.IsFunction(declared))
                runtime.Invoke(declared, hex);
            return;
        }

        runtime.RunFile(automation.File);
        if (runtime.UserScope.Lookup("automations") is not ObjectValue all || !all.Properties.TryGetValue(automation.Name, out IRuntimeValue? mine)
            || mine is not ObjectValue told || !told.Properties.TryGetValue("run", out IRuntimeValue? run) || !Values.IsFunction(run))
            throw new HidlRuntimeException(context.Strings.Said("automations.no_run", ("name", automation.Name), ("file", Path.GetFileName(automation.File))));

        runtime.Invoke(run, hex);
    }

    /// <summary>A line it printed, on its card and in the Output panel, the panel brought up the first time.</summary>
    private void Print(string line)
    {
        work.Write(line);
        bool first = !shown;
        shown = true;
        post(() =>
        {
            context.Notices.Note(context.Strings.Said("automations.printed", ("name", automation.Title), ("text", line)));
            if (first)
                context.Panels.ShowOutput();
        });
    }

    /// <summary>
    /// Something done on the editor's thread and what it came to, this thread waiting for it. Stopped while it waits,
    /// it's stopped, and whatever went wrong over there goes wrong here.
    /// </summary>
    private T OnEditor<T>(Func<T> what)
    {
        if (System.Environment.CurrentManagedThreadId == editorThread)
            return what();

        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        post(() =>
        {
            if (work.Token.IsCancellationRequested)
            {
                done.TrySetCanceled();
                return;
            }

            try
            {
                done.TrySetResult(what());
            }
            catch (Exception e)
            {
                done.TrySetException(e);
            }
        });

        return Waited(done.Task);
    }

    /// <summary>What a task came to, waited for until it's done or the automation is stopped, what went wrong in it thrown as it was.</summary>
    private T Waited<T>(Task<T> task)
    {
        try
        {
            task.Wait(work.Token);
        }
        catch (AggregateException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Throw(e.InnerException);
        }

        return task.Result;
    }
}
