namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestRangeFormatting(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "range formatting"), file = Path.Combine(root, "Program.cs");
        const string second = "    static void Second(){int untouched=3+4;System.Console.WriteLine(untouched);}";
        const string original = "class Program\n{\n    static void First()\n    {\n        int value=1+2;\n        System.Console.WriteLine(value);\n    }\n\n" + second + "\n}\n";
        string before = "";
        void SelectLine() => code.Document.Select(new(4, 0), new(4, code.Document.LineAt(4).Length));
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "Probe.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, original);
            explorer.OpenFolder(root);
            Open(file);
        });
        test.WaitFor(() => languages.CanServe(document, "documentRangeFormattingProvider"), 90);
        test.Check("Range formatting: native C# server supports selection formatting", () => languages.CanServe(document, "documentRangeFormattingProvider"));
        test.Run(() =>
        {
            SelectLine();
            languages.FormatSelection();
            code.Document.MoveToEnd(false);
            code.Document.Insert("// unsaved typing during formatting\n");
            before = code.Text;
        });
        test.Settle(1f);
        test.Check("Range formatting: late response preserves newer typing", () => code.Text == before);
        test.Run(() => { SelectLine(); languages.FormatSelection(); });
        test.WaitFor(() => code.Text.Contains("int value = 1 + 2;"), 15);
        test.Check("Range formatting: selected statement receives native formatting", () => code.Text.Contains("int value = 1 + 2;"));
        test.Check("Range formatting: surrounding code and unsaved edits are preserved", () => code.Text.Contains(second)
            && code.Text.EndsWith("// unsaved typing during formatting\n", StringComparison.Ordinal));
        test.Check("Range formatting: disk contents remain unchanged", () => File.ReadAllText(file) == original);
        test.Run(() => code.Document.Undo());
        test.Check("Range formatting: one undo restores text and selection", () => code.Text == before && code.Document.SelectionStart == new UITextPosition(4, 0)
            && code.Document.SelectionEnd == new UITextPosition(4, "        int value=1+2;".Length));
        test.Run(() => code.Document.Redo());
        test.Check("Range formatting: redo reapplies only the selected formatting", () => code.Text.Contains("int value = 1 + 2;") && code.Text.Contains(second));
    }
}
