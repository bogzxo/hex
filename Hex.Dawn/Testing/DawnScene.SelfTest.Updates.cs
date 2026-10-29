namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestUpdateHandoff(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "update-unsaved.txt");
        int abandoned = 0;
        bool started = true;
        test.Run(() =>
        {
            File.WriteAllText(file, "saved contents");
            Open(file);
            code.Document.Insert("unsaved edit ");
        });
        test.Wait();
        test.Run(() =>
        {
            RestartInto(Path.Combine(testFolder, "missing-setup.exe"), [], () => abandoned++, null);
        });
        test.WaitFor(() => closeQuestion is { IsOpen: true }, 2.0f);
        test.Check("updates: an update handoff asks before closing an unsaved document", () => closeQuestion is { IsOpen: true } && restartInto is not null);
        test.Run(() => closeQuestion?.Cancel());
        test.Wait();
        test.Check("updates: cancel abandons prepared setup and keeps the unsaved document", () =>
            abandoned == 1 && restartInto is null && code.Text.Contains("unsaved edit") && File.ReadAllText(file) == "saved contents");
        test.Run(() =>
        {
            restartInto = (Path.Combine(testFolder, "missing-setup.exe"), [], () => abandoned++, null);
            started = StartRestart();
        });
        test.Check("updates: failed process start abandons setup and leaves the editor open", () =>
            !started && abandoned == 2 && restartInto is null && document.Path == file && code.Text.Contains("unsaved edit"));
        test.Run(() => { code.Document.Undo(); CloseNow(); });
    }
}
