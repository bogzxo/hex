namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestNativeHighlights(HexPointerScript test, string language)
    {
        string root = Path.Combine(testFolder, "native highlights " + language);
        string file = Path.Combine(root, language == "cpp" ? "main.cpp" : language == "python" ? "main.py" : "Program.cs");
        string source = language == "cpp" ? "int other() { int value = 99; return value; }\nint main() {\n int value = 1;\n value = 2;\n return value;\n}\n// value\n"
            : language == "python" ? "value = 99\ndef main():\n value = 1\n value = 2\n return value\n\n# value\n"
            : "class Program { static int Other() { int value = 99; return value; }\nstatic int Main() {\n int value = 1;\n value = 2;\n return value;\n}}\n// value\n";
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            if (language == "csharp") File.WriteAllText(Path.Combine(root, "Probe.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, source);
            explorer.OpenFolder(root);
            Open(file);
        });
        test.WaitFor(() => languages.CanServe(document, "documentHighlightProvider"), 60f);
        test.Check("Native highlights: " + language + " advertises document highlights", () => languages.CanServe(document, "documentHighlightProvider"));
        test.Run(() => code.GoTo(3, 3));
        test.WaitFor(() => code.NativeOccurrences.Count >= 3, 15f);
        test.Check("Native highlights: " + language + " scopes references to the selected variable", () => code.NativeOccurrences.Count == 3
            && code.NativeOccurrences.Select(item => item.Line).Order().SequenceEqual(new[] { 2, 3, 4 })
            && code.Occurrences.Count == 3);
        if (language == "cpp")
            test.Check("Native highlights: native read and write kinds are preserved", () => code.NativeOccurrences.Any(item => item.Line == 3 && item.Kind == 3)
                && code.NativeOccurrences.Any(item => item.Line == 4 && item.Kind == 2));
        test.Run(() => code.GoTo(6, 4));
        test.WaitFor(() => code.NativeOccurrences.Count == 0 && code.Occurrences.Count == 0, 10f);
        test.Check("Native highlights: " + language + " does not highlight comment text matches", () => code.NativeOccurrences.Count == 0 && code.Occurrences.Count == 0);
        test.Run(() => code.GoTo(3, 3));
        test.WaitFor(() => code.NativeOccurrences.Count == 3, 10f);
        test.Run(() => code.Text = language == "python" ? "pass\n" : "// changed\n");
        test.WaitFor(() => code.NativeOccurrences.Count == 0 && code.Occurrences.Count == 0, 10f);
        test.Check("Native highlights: " + language + " edits clear obsolete ranges", () => code.NativeOccurrences.Count == 0 && code.Occurrences.Count == 0);
        test.Run(() => { code.Text = source; code.GoTo(3, 3); });
        test.WaitFor(() => code.NativeOccurrences.Count == 3, 15f);
        test.Run(() =>
        {
            code.Document.MoveTo(new UITextPosition(0, 0));
            code.Document.Insert(language == "python" ? "# inserted\n" : "// inserted\n");
            code.GoTo(4, 3);
        });
        test.WaitFor(() => code.NativeOccurrences.Count == 3 && code.NativeOccurrences.Any(item => item.Line == 5), 15f);
        test.Check("Native highlights: " + language + " server tracks an unsaved line insertion after replacement", () =>
            code.NativeOccurrences.Count == 3 && code.NativeOccurrences.Select(item => item.Line).Order().SequenceEqual(new[] { 3, 4, 5 })
            && File.ReadAllText(file) == source);
    }
}
