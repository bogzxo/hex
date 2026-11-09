namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestNativeFolding(HexPointerScript test, string language)
    {
        bool python = language == "python", cpp = language == "cpp";
        string root = Path.Combine(testFolder, "native folding " + language);
        string file = Path.Combine(root, python ? "main.py" : cpp ? "main.cpp" : "Program.cs");
        string source = python ? "class Box:\n    def get(self):\n        numbers = [\n            1,\n            2\n        ]\n        return numbers\n"
            : cpp ? "/*\n A comment that has no bracket-based fold.\n Another line.\n*/\nint main() {\n return 0;\n}\n"
            : "#region Group\n// First line.\n// Second line.\n#endregion\nclass Program\n{\n static void Main()\n {\n  System.Console.WriteLine(42);\n }\n}\n";
        string flat = python ? "value = 42\n" : cpp ? "int main() { return 0; }\n" : "class Program { static void Main() { System.Console.WriteLine(42); } }\n";
        bool lenses = settings.CodeLensReferences;
        int before = 0, hidden = 0;
        test.Run(() =>
        {
            settings.CodeLensReferences = false;
            Directory.CreateDirectory(root);
            if (!python && !cpp) File.WriteAllText(Path.Combine(root, "Probe.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, source);
            explorer.OpenFolder(root);
            Open(file);
        });
        test.WaitFor(() => code.NativeFoldingRanges?.Any(range => range.Start == 0) == true && code.CanFold(0), 60f);
        test.Check("Native folding: " + language + " server structure creates a non-bracket fold", () => code.NativeFoldingRanges?.Any(range => range.Start == 0) == true && code.CanFold(0));
        test.Check("Native folding: " + language + " uses its available native provider", () => languages.FoldingSourceOf(document) is {} server
            && (python ? !server.Offers("foldingRangeProvider") && server.Offers("documentSymbolProvider") && code.CanFold(2) : server.Offers("foldingRangeProvider")));
        test.Run(() =>
        {
            code.GoTo(0, 0);
            before = code.RowCount;
            hidden = code.NativeFoldingRanges?.FirstOrDefault(range => range.Start == 0).End ?? 0;
            code.ToggleFold(0);
        });
        test.Check("Native folding: " + language + " collapse hides exactly the native range", () => code.FoldedLines.Contains(0) && code.RowCount == before - hidden);
        test.Run(() => code.UnfoldAll());
        test.Check("Native folding: " + language + " unfold restores rows without changing source", () => code.RowCount == before && code.Text == source && File.ReadAllText(file) == source);
        test.Run(() => { code.Document.SelectAll(); code.Document.Insert(flat); });
        test.Check("Native folding: " + language + " editing retires the previous snapshot", () => code.NativeFoldingRanges is null);
        test.WaitFor(() => code.NativeFoldingRanges is { Count: 0 }, 20f);
        test.Check("Native folding: " + language + " current empty response removes obsolete folds", () => code.NativeFoldingRanges is { Count: 0 } && !code.CanFold(0));
        test.Run(() => code.Document.Undo());
        test.WaitFor(() => code.NativeFoldingRanges?.Any(range => range.Start == 0) == true, 20f);
        test.Check("Native folding: " + language + " undo restores semantic folds in the unsaved buffer", () => code.Text == source && code.CanFold(0)
            && code.NativeFoldingRanges?.Any(range => range.Start == 0) == true && File.ReadAllText(file) == source);
        test.Run(() => { code.UnfoldAll(); settings.CodeLensReferences = lenses; });
    }
}
