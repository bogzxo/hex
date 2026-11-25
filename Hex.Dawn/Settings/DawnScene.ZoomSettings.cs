namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private HexSettings? zoomSettingsPending;
    private float zoomSettingsWait;
    private const float ZOOM_SAVE_DELAY = 0.5f;

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
        if (zoomSettingsPending is not null && (zoomSettingsWait -= dt) <= 0)
            FlushZoomSettings();
    }

    private void FlushZoomSettings(bool report = true)
    {
        if (zoomSettingsPending is not { } pending) return;
        zoomSettingsPending = null;
        if (pending.Save() is { } problem)
        {
            if (report) Say(problem, error: true);
            else HexLogLine(problem);
        }
        else if (ReferenceEquals(pending, settings)) KnowSettingsFile();
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
