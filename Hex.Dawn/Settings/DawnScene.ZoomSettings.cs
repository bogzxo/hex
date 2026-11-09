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

    private void UpdateZoomSettings(float dt)
    {
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
        debugger?.DetachOnClose();
        base.DisposeOther();
    }
}
