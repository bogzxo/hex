namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestZoomPersistence(HexPointerScript test)
    {
        HexSettings original = settings;
        string file = Path.Combine(testFolder, "zoom-settings.hor");
        string before = "";
        float zoom = 1;
        test.Run(() =>
        {
            FlushZoomSettings();
            settings = new HexSettings(file);
            SaveSettings();
            before = File.ReadAllText(file);
            ZoomCode(1.1f); ZoomCode(1.1f); ZoomCode(1.1f);
            zoom = settings.CodeZoom;
        });
        test.Check("zoom persistence: burst applies immediately without writing each notch", () =>
            zoom > 1.3f && File.ReadAllText(file) == before && zoomSettingsPending == settings);
        test.Settle(0.8f);
        test.Check("zoom persistence: final value survives reload after settling", () =>
            zoomSettingsPending is null && MathF.Abs(new HexSettings(file).CodeZoom - zoom) < 0.001f);
        test.Run(() => { ZoomCode(0); FlushZoomSettings(); });
        test.Check("zoom persistence: leaving flushes without waiting", () =>
            zoomSettingsPending is null && new HexSettings(file).CodeZoom == 1);
        test.Run(() => { ZoomCode(1.1f); SaveSettings(); });
        test.Check("zoom persistence: another explicit save consumes pending work", () =>
            zoomSettingsPending is null && MathF.Abs(new HexSettings(file).CodeZoom - 1.1f) < 0.001f);
        test.Run(() =>
        {
            ZoomCode(0);
            FlushZoomSettings(report: false);
        });
        test.Check("zoom persistence: shutdown flush saves the final value", () =>
            zoomSettingsPending is null && new HexSettings(file).CodeZoom == 1);
        test.Run(() =>
        {
            settings = original;
            foreach (HexDocument open in documents) open.Editor.TextScale = CODE_TEXT * settings.CodeZoom;
            foreach (NotebookCellView cell in cellViews) cell.Editor.TextScale = CODE_TEXT * settings.CodeZoom;
        });
    }
}
