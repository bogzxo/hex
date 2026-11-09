namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestSignatureDocumentation(HexPointerScript test, string language)
    {
        string root = Path.Combine(testFolder, "signature documentation " + language);
        string file = Path.Combine(root, language == "csharp" ? "Program.cs" : language == "cpp" ? "main.cpp" : "main.py");
        const string summary = "Adds integer operands. The result combines the supplied values without changing either operand. Use the parameter descriptions to choose the values passed to this documented method.", otherSummary = "Adds decimal operands.";
        string source = language == "csharp" ? "class Program {\n"
            + "/// <summary>" + summary + "</summary>\n/// <param name=\"first\">First operand.</param>\n/// <param name=\"second\">Second operand.</param>\n"
            + "static int Add(int first, int second) => first + second;\n"
            + "/// <summary>" + otherSummary + "</summary>\nstatic double Add(double first, double second) => first + second;\n"
            + "static void Main() { _ = Add(1, 2); }\n}\n"
            : language == "cpp" ? "/// " + summary + "\n/// \\param first First operand.\n/// \\param second Second operand.\nint add(int first, int second);\n"
            + "/// " + otherSummary + "\ndouble add(double first, double second);\nint main() { return add(1, 2); }\n"
            : "def add(first: int, second: int) -> int:\n    \"\"\"" + summary + "\n\n    Args:\n        first: First operand.\n        second: Second operand.\n    \"\"\"\n    return first + second\nresult = add(1, 2)\n";
        string[] lines = source.Split('\n');
        int callLine = Array.FindIndex(lines, line => line.Contains("(1, 2)", StringComparison.Ordinal));
        string call = lines[callLine];
        var firstCaret = new UITextPosition(callLine, call.IndexOf('1') + 1);
        var secondCaret = new UITextPosition(callLine, call.IndexOf('2') + 1);
        var fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        string PopupDocs() => (string?)typeof(CodeEditor).GetField("signaturePopupDocs", fields)!.GetValue(code) ?? string.Empty;
        int PaintedDocLines() => ((IReadOnlyCollection<(int Start, int Length)>)typeof(CodeEditor).GetField("signatureDocLines", fields)!.GetValue(code)!).Count;
        bool Shows(string text) => code.Signature?.Documentation.Contains(text, StringComparison.Ordinal) == true && PopupDocs().Contains(text, StringComparison.Ordinal);
        bool ShowsSecondParameter() => code.Signature is {} signature && signature.ParameterDocumentation.Contains("Second operand.", StringComparison.Ordinal)
            && PopupDocs().StartsWith(signature.ParameterDocumentation, StringComparison.Ordinal);
        void Type(char character) => typeof(CodeEditor).GetMethod("OnTextInput", fields)!.Invoke(code, [character]);
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            if (language == "csharp") File.WriteAllText(Path.Combine(root, "Probe.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, source);
            explorer.OpenFolder(root);
            Open(file);
        });
        test.WaitFor(() => languages.CanServe(document, "signatureHelpProvider"), 60);
        test.Run(() => { code.Document.MoveTo(firstCaret); code.Focus(); });
        test.WaitFor(() => Shows(summary) && PaintedDocLines() > 0, 20);
        test.Check("Signature docs: " + language + " native method documentation is rendered with the signature", () => Shows(summary) && PaintedDocLines() > 0);
        test.Check("Signature docs: " + language + " native prose wraps across readable lines", () => PaintedDocLines() > 1);
        test.Check("Signature docs: " + language + " help fits inside the editor", () =>
            typeof(CodeEditor).GetField("signaturePopupSize", fields)!.GetValue(code) is System.Numerics.Vector2 size
            && size.X > 0 && size.X <= code.Bounds.Width && size.Y > 0 && size.Y <= code.Bounds.Height);
        if (language == "python")
        {
            test.Check("Signature docs: native Python first-parameter documentation is selected", () => code.Signature?.ParameterDocumentation.Contains("First operand.", StringComparison.Ordinal) == true);
            test.Run(() => code.Document.MoveTo(secondCaret));
            test.WaitFor(ShowsSecondParameter, 10);
            test.Run(() => Console.WriteLine("[Hex self-test] Python active parameter documentation: " + code.Signature?.ParameterDocumentation));
            test.Check("Signature docs: native Python parameter documentation follows the caret", ShowsSecondParameter);
        }
        else
        {
            test.Check("Signature docs: " + language + " native overloads have separate documentation", () => code.Signature is { Overloads: { Count: 2 } overloads }
                && overloads.Any(item => item.Documentation.Contains(otherSummary, StringComparison.Ordinal)));
            test.Run(() => Type(UIKeyboard.DOWN));
            test.WaitFor(() => Shows(otherSummary), 5);
            test.Check("Signature docs: " + language + " overload selection updates the rendered documentation", () => Shows(otherSummary) && code.Document.Caret == firstCaret);
            test.Run(() => Type(UIKeyboard.UP));
            test.WaitFor(() => Shows(summary), 5);
            test.Check("Signature docs: " + language + " returning to the first overload restores its documentation", () => Shows(summary));
        }
        test.Check("Signature docs: " + language + " reading help does not edit or save source", () => code.Text == source && File.ReadAllText(file) == source);
        test.Run(() => code.GoTo(callLine, call.Length));
        test.WaitFor(() => code.Signature is null, 5);
        test.Check("Signature docs: " + language + " leaving the call dismisses documentation with its signature", () => code.Signature is null);
    }
}
