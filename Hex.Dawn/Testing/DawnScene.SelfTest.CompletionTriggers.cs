namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestCompletionTriggers(HexPointerScript test, string language)
    {
        bool python = language == "python";
        string root = Path.Combine(testFolder, "completion triggers " + language);
        string file = Path.Combine(root, python ? "main.py" : "main.cpp");
        string source = python
            ? "from typing import TypedDict\nclass Row(TypedDict):\n    answer: int\nrow: Row = {\"answer\": 42}\nvalue = row\n"
            : "namespace Palette { int answer = 42; }\nint main() { return Palette; }\n";
        int line = python ? 4 : 1;
        int column = python ? "value = row".Length : "int main() { return Palette".Length;
        char trigger = python ? '[' : ':';
        string typed = python ? "[" : "::";
        string completed = python ? source.Replace("value = row", "value = row[\"answer\"]")
            : source.Replace("Palette;", "Palette::answer;");
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(file, source);
            explorer.OpenFolder(root);
            Open(file);
        });
        test.WaitFor(() => languages.CanServe(document, "completionProvider"), 45f);
        test.Check("Completion trigger: " + language + " advertises its native trigger", () =>
            languages.TestServerFor(document)?.TriggersCompletion(trigger) == true && code.CompletionTrigger?.Invoke(trigger) == true);
        test.Run(() => { code.Focus(); code.GoTo(line, column); code.CloseCompletion(); });
        foreach (char character in typed)
            test.Run(() => Engine.Input.Post(Horizon.Input.InputEvent.Typed(character.ToString())));
        test.WaitFor(() => code.CompletionOpen && code.Completions.Any(item => item.Text.Contains("answer", StringComparison.Ordinal)), 20f);
        test.Check("Completion trigger: " + language + " opens native suggestions from typed punctuation", () =>
            code.CompletionOpen && code.Completions.Any(item => item.Text.Contains("answer", StringComparison.Ordinal)));
        test.Run(() =>
        {
            int index = code.Completions.ToList().FindIndex(item => item.Text.Contains("answer", StringComparison.Ordinal));
            if (index >= 0) { code.MoveCompletion(index); code.AcceptCompletion(); }
            else Console.WriteLine("[Hex self-test] trigger completions: " + string.Join(", ", code.Completions.Select(item => item.Text + " => " + item.Insert)));
        });
        test.Check("Completion trigger: " + language + " acceptance preserves surrounding punctuation", () => code.Text == completed);
        test.Check("Completion trigger: " + language + " completion stays unsaved", () => File.ReadAllText(file) == source);
        test.Run(() => code.Document.Undo());
        test.Check("Completion trigger: " + language + " undo restores the typed trigger", () =>
            code.Text == (python ? source.Replace("value = row", "value = row[]") : source.Replace("Palette;", "Palette::;")));
    }
}
