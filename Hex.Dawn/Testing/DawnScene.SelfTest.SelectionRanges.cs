using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestSelectionRanges(HexPointerScript test, string language)
    {
        string root = Path.Combine(testFolder, "native selection " + language);
        string file = Path.Combine(root, language == "cpp" ? "main.cpp" : language == "python" ? "main.py" : "Program.cs");
        string source = language == "cpp" ? "int main() { int value = (1 + 2) * 3; return value; }\n"
            : language == "python" ? "value = (1 + 2) * 3\n"
            : "class Program { static int Main() { int value = (1 + 2) * 3; return value; } }\n";
        var caret = new UITextPosition(0, source.IndexOf('1'));
        string first = "";
        void Invoke(string key) => AllCommands().First(command => command.Text.Contains(Said(key), StringComparison.Ordinal)).Picked();
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            if (language == "csharp") File.WriteAllText(Path.Combine(root, "Probe.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, source);
            explorer.OpenFolder(root);
            Open(file);
        });
        test.WaitFor(() => languages.CanServe(document, "hoverProvider"), 60f);
        if (language is "python" or "csharp")
        {
            test.Check("Native selection: " + language + " unsupported capability keeps expansion unavailable", () =>
                !languages.CanServe(document, "selectionRangeProvider")
                && !AllCommands().Any(command => command.Text.Contains(Said("code_server.expand_selection"), StringComparison.Ordinal)));
            return;
        }
        test.WaitFor(() => languages.CanServe(document, "selectionRangeProvider"), 10f);
        test.Check("Native selection: " + language + " server supports structural selection", () => languages.CanServe(document, "selectionRangeProvider"));
        test.Run(() => { code.Document.MoveTo(caret); Invoke("code_server.expand_selection"); });
        test.WaitFor(() => code.Document.HasSelection, 10f);
        test.Check("Native selection: " + language + " selects syntax around caret", () => code.Document.HasSelection && code.Document.SelectedText.Contains('1'));
        test.Run(() => { first = code.Document.SelectedText; Invoke("code_server.expand_selection"); });
        test.Check("Native selection: " + language + " expands to enclosing syntax", () => code.Document.SelectedText.Length > first.Length && code.Document.SelectedText.Contains(first));
        test.Run(() => Invoke("code_server.shrink_selection"));
        test.Check("Native selection: " + language + " shrink restores previous selection", () => first.Length > 0 && code.Document.SelectedText == first);
        test.Run(() => Invoke("code_server.shrink_selection"));
        test.Check("Native selection: " + language + " shrink restores original caret without editing", () => !code.Document.HasSelection && code.Document.Caret == caret && code.Text == source);
        var anchor = new UITextPosition(0, source.IndexOf('2') + 1);
        test.Run(() => { code.Document.Select(anchor, caret); first = code.Document.SelectedText; Invoke("code_server.expand_selection"); });
        test.WaitFor(() => code.Document.SelectedText.Length > first.Length, 10f);
        test.Check("Native selection: reversed selection expands to a containing range", () => code.Document.SelectedText.Length > first.Length && code.Document.SelectedText.Contains(first));
        test.Run(() => Invoke("code_server.shrink_selection"));
        test.Check("Native selection: shrinking restores selection direction", () => code.Document.Caret == caret && code.Document.Anchor == anchor);
        test.Run(() => code.Text = source + "// edited\n");
        test.Check("Native selection: editing invalidates shrink history", () => !AllCommands().Any(command => command.Text.Contains(Said("code_server.shrink_selection"), StringComparison.Ordinal)));
    }
}
