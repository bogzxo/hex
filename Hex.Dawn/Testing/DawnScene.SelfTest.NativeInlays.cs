namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestNativeInlays(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "native inlays"), file = Path.Combine(root, "main.cpp");
        bool Has(string name) => code.InlayHints.Any(hint => hint.Text.Contains(name, StringComparison.Ordinal));
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(file, "int add(int first, int second) { return first + second; }\nint main() { auto total = add(1, 2); return total; }\n");
            settings.InlayHints = true;
            explorer.OpenFolder(root);
            Open(file);
            UpdateInlays(); // Exercise an immediate request while the native server is still starting.
        });
        test.WaitFor(() => languages.CanServe(document, "inlayHintProvider"), 30);
        test.Check("Native inlays: clangd offers inlay hints", () => languages.CanServe(document, "inlayHintProvider"));
        test.WaitFor(() => Has("first") && Has("second"), 5);
        test.Check("Native inlays: parameter hints appear without typing", () => Has("first") && Has("second"));
        test.Check("Native inlays: hints leave the file unmodified", () => !document.Modified);
        test.Run(() =>
        {
            code.Document.Select(new(0, 0), new(0, code.Document.LineAt(0).Length));
            code.Document.Insert("int add(int left, int second) { return left + second; }");
        });
        test.WaitFor(() => Has("left") && !Has("first"), 5);
        test.Check("Native inlays: unsaved parameter edits update hints on the unchanged call line", () => Has("left") && !Has("first"));
        test.Check("Native inlays: refreshing hints does not save source", () => document.Modified && File.ReadAllText(file).Contains("int first", StringComparison.Ordinal));
        test.Run(() => code.Document.Undo());
        test.WaitFor(() => Has("first") && !Has("left"), 5);
        test.Check("Native inlays: undo refreshes the original parameter hint", () => Has("first") && !Has("left"));
        test.Run(() => { settings.InlayHints = false; ForgetInlays(); });
        test.Check("Native inlays: disabling hints clears them", () => code.InlayHints.Count == 0);
        test.Run(() => { settings.InlayHints = true; ForgetInlays(); });
        test.WaitFor(() => Has("first") && Has("second"), 5);
        test.Check("Native inlays: enabling hints restores them without editing", () => Has("first") && Has("second"));
    }
}
