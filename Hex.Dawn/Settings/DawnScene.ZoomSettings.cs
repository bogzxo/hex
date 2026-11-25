namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private HexSettings? zoomSettingsPending;
    private float zoomSettingsWait;
    private const float ZOOM_SAVE_DELAY = 0.3f;

    private void QueueZoomSettings()
    {
        // A workspace/settings replacement must not redirect an earlier pending save.
        if (zoomSettingsPending is not null && !ReferenceEquals(zoomSettingsPending, settings))
            FlushZoomSettings();
        zoomSettingsPending = settings;
        zoomSettingsWait = ZOOM_SAVE_DELAY;
    }

    // The agent data folder as typed, taken up once the typing has paused: it's made (and written to) when it's taken up,
    // and the box commits as the typing goes
    private bool agentFolderPending;
    private float agentFolderWait;

    private void AgentFolderTyped()
    {
        agentFolderPending = true;
        agentFolderWait = 1.5f;
    }

    private void FlushAgentFolder()
    {
        if (!agentFolderPending)
            return;
        agentFolderPending = false;
        Hex.Core.HexData.UseAgentFolder(settings.AgentDataFolder);
    }

    private void UpdateZoomSettings(float dt)
    {
        if (agentFolderPending && (agentFolderWait -= dt) <= 0)
            FlushAgentFolder();
        if (zoomSettingsPending is not null)
        {
            // Counted down by frames, and the engine may be asleep while nothing else moves.
            // Hold off composing/reading settings.hor while the pointer is held down on a slider drag.
            Horizon.Engine.GameEngine.Instance?.WindowManager.RequestFrames();
            if (compositor?.Pointer.Down == true)
                zoomSettingsWait = MathF.Max(zoomSettingsWait, 0.05f);
            else if ((zoomSettingsWait -= dt) <= 0)
                WriteSettings(report: true, background: true);
        }

        // A write on the pool is over: what it couldn't do is said here, on the editor's thread
        if (settingsWriteDone && Volatile.Read(ref settingsWritesOpen) == 0)
        {
            settingsWriteDone = false;
            if (Interlocked.Exchange(ref settingsWriteProblem, null) is { } problem)
                Say(problem, error: true);
            if (settingsWatched is { } watched && settingsProblems.ContainsKey(watched))
                CheckSettingsFiles();
        }
    }

    // The settings queued (see QueueZoomSettings) are written here. The text is made on the editor's thread, which owns the
    // settings, and the disk (a flush to it and a replace of the file, tens of milliseconds on some machines) is waited for on
    // the pool; a flush from a closing or a resting editor waits for it instead
    private Task settingsWrites = Task.CompletedTask;
    private int settingsWritesOpen;
    private string? settingsWriteProblem;
    private volatile bool settingsWriteDone;

    // Whether settings are on their way to the file, so a change of the file seen now is Hex's own and not somebody's
    private bool SettingsBeingKept => zoomSettingsPending is not null || Volatile.Read(ref settingsWritesOpen) > 0;

    private void FlushZoomSettings(bool report = true) => WriteSettings(report, background: false);

    private void WriteSettings(bool report, bool background)
    {
        if (zoomSettingsPending is not { } pending) return;
        zoomSettingsPending = null;
        if (pending.Compose() is not { } text) return;

        // What the file will say, so the watch doesn't read Hex's own write back in
        if (ReferenceEquals(pending, settings) && settingsWatched is not null)
            settingsKnown = text;

        if (background)
        {
            Interlocked.Increment(ref settingsWritesOpen);
            settingsWrites = settingsWrites.ContinueWith(_ =>
            {
                if (pending.Write(text) is { } failed)
                    Interlocked.Exchange(ref settingsWriteProblem, failed);
                settingsWriteDone = true;
                Interlocked.Decrement(ref settingsWritesOpen);
                Horizon.Engine.GameEngine.Instance?.WindowManager.Wake();
            }, TaskScheduler.Default);
            return;
        }

        settingsWrites.Wait();
        if (pending.Write(text) is { } problem)
        {
            if (report) Say(problem, error: true);
            else HexLogLine(problem);
        }
        else if (ReferenceEquals(pending, settings) && settingsWatched is { } watched && settingsProblems.ContainsKey(watched))
            CheckSettingsFiles();
    }

    protected override void DisposeOther()
    {
        // Scene disposal runs after updates have stopped, including close before the debounce expires.
        FlushZoomSettings(report: false);
        FlushAgentFolder();
        explorer?.FlushKept();
        // The chats as they are at the last, what moved since the last few seconds' write
        KeepChats();
        // Written on a thread of their own, waited for so the last of them is on the disk before Hex goes
        chatsKept?.Flush();
        debugger?.DetachOnClose();
        extensions?.StopWatching();
        StopRemote();
        _ = linkPortMapping?.SetPort(0);
        // The llama-server Hex started goes with it, its memory and the card let go
        ShutDownLlamaServer();
        base.DisposeOther();
    }
}
