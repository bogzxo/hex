namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestNativeSignatures(HexPointerScript test)
        => TestNativeSignatures(test, "cpp");

    private void TestNativeSignatures(HexPointerScript test, string language, bool quoted = false)
    {
        string root = Path.Combine(testFolder, "native signatures " + language + (quoted ? " quoted" : ""));
        string file = Path.Combine(root, language == "csharp" ? "Program.cs" : language == "python" ? "main.py" : "main.cpp");
        string call = language == "csharp" ? "static void Main() { _ = Add(1, 2); }" : language == "python" ? "result = add(1, 2)" : "int main() { return add(1, 2); }";
        string firstArgument = quoted ? language == "python" ? "'a,b'" : "','" : "1";
        if (quoted) call = call.Replace("(1, 2)", "(" + firstArgument + ", 2)");
        string source = language == "csharp" ? "class Program { static int Add(int first, int second) => first + second;\n" + call + "\n}\n"
            : language == "python" ? "def add(first: int, second: int) -> int: return first + second\n" + call + "\n"
            : "int add(int, int);\n" + call + "\n";
        if (quoted) source = source.Replace("int first", "char first").Replace("int add(int, int)", "int add(char, int)")
            .Replace("first: int", "first: str").Replace("return first + second", "return second");
        int first = -1;
        bool Highlighted() => code.Signature is { Length: > 0 };
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            if (language == "csharp") File.WriteAllText(Path.Combine(root, "Probe.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, source);
            explorer.OpenFolder(root);
            Open(file);
        });
        test.WaitFor(() => languages.CanServe(document, "signatureHelpProvider"), 60);
        test.Run(() => code.GoTo(1, call.IndexOf(firstArgument, StringComparison.Ordinal) + firstArgument.Length));
        test.WaitFor(Highlighted, 15);
        test.Check("Native signatures: the first parameter is highlighted", Highlighted);
        test.Run(() => { first = code.Signature?.Start ?? -1; code.GoTo(1, call.IndexOf('2') + 1); });
        test.WaitFor(() => code.Signature is { Length: > 0 } signature && signature.Start > first, 5);
        test.Check("Native signatures: moving to the second argument advances the highlight", () => code.Signature is { Length: > 0 } signature && signature.Start > first);
        test.Run(() => code.GoTo(1, call.IndexOf(firstArgument, StringComparison.Ordinal) + firstArgument.Length));
        test.WaitFor(() => code.Signature?.Start == first, 5);
        test.Check("Native signatures: returning to the first argument restores its highlight", () => first >= 0 && code.Signature?.Start == first);
        test.Run(() => code.GoTo(1, call.Length));
        test.WaitFor(() => code.Signature is null, 5);
        test.Check("Native signatures: leaving the call dismisses the signature", () => code.Signature is null);
    }
}
