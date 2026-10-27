using System.Text.Json.Nodes;

namespace Hex.Core.Notebooks;

// What's sent to the kernel and what comes back on each of its channels. A request waited for is matched to its reply by
// the reply's parent, everything else (and the replies too) goes on the queue for the editor's thread
internal sealed partial class Kernel
{
    /// <summary>
    /// Runs code, as a cell does, and says the id its outputs and its reply will carry as their parent's. Nothing waits
    /// here, it all comes in on <see cref="Events"/>.
    /// </summary>
    public string Execute(string code, bool silent = false)
    {
        JupyterMessage asked = JupyterMessage.Make(session, "execute_request", new JsonObject
        {
            ["code"] = code,
            ["silent"] = silent,
            ["store_history"] = !silent,
            ["user_expressions"] = new JsonObject(),
            ["allow_stdin"] = true,
            ["stop_on_error"] = true
        });
        Send(SHELL, asked);
        return asked.Id;
    }

    /// <summary>What was typed in answer to the code asking for it (input()), for the request with that id.</summary>
    public void Answer(string value, JupyterMessage request)
    {
        JupyterMessage reply = JupyterMessage.Make(session, "input_reply", new JsonObject { ["value"] = value });
        reply.ParentHeader.Clear();
        foreach (var (name, part) in request.Header)
            reply.ParentHeader[name] = part?.DeepClone();
        Send(STDIN, reply);
    }

    /// <summary>Sends a request on a channel and waits for its reply, cancelled when the kernel goes.</summary>
    public async Task<JupyterMessage> RequestAsync(string channel, string type, JsonObject content, CancellationToken cancel)
    {
        JupyterMessage asked = JupyterMessage.Make(session, type, content);
        var answer = new TaskCompletionSource<JupyterMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting[asked.Id] = answer;
        using CancellationTokenRegistration gone = cancel.Register(() => answer.TrySetCanceled(cancel));
        try
        {
            Send(channel, asked);
            return await answer.Task.ConfigureAwait(false);
        }
        finally
        {
            waiting.TryRemove(asked.Id, out _);
        }
    }

    // Helper method to send on a channel without waiting for it to go, a kernel that's gone just doesn't get it
    private void Send(string channel, JupyterMessage message)
    {
        ZmtpSocket? to = channel switch { SHELL => shell, CONTROL => control, STDIN => stdin, _ => null };
        if (to is not { Ready: true } || State == KernelState.Dead)
            return;

        _ = to.SendAsync(message.ToFrames(key), stop.Token).ContinueWith(sent => { _ = sent.Exception; }, TaskScheduler.Default);
    }

    // Helper method to take in everything a channel says until the kernel goes
    private void Listen(ZmtpSocket socket, string channel) => _ = Task.Run(async () =>
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                List<byte[]> frames = await socket.ReceiveAsync(stop.Token).ConfigureAwait(false);
                if (JupyterMessage.FromFrames(frames, key) is not { } message)
                    continue;

                if (message.Type == "status" && message.Content["execution_state"]?.GetValue<string>() is { } state && State != KernelState.Dead)
                    State = state switch { "busy" => KernelState.Busy, "starting" => KernelState.Starting, _ => KernelState.Idle };

                if (message.Type.EndsWith("_reply", StringComparison.Ordinal) && waiting.TryRemove(message.ParentId, out var asked))
                    asked.TrySetResult(message);

                Events.Enqueue(new KernelEvent(channel, message));
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // The kernel went or was let go of
        }
    });
}
