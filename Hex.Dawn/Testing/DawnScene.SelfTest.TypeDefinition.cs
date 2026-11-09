namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestTypeDefinition(HexPointerScript test, string language)
    {
        string root = Path.Combine(testFolder, "type definition " + language);
        string model = Path.Combine(root, language == "cpp" ? "model.h" : language == "python" ? "model.py" : "Model.cs");
        string file = Path.Combine(root, language == "cpp" ? "main.cpp" : language == "python" ? "main.py" : "Program.cs");
        string source = language == "cpp" ? "#include \"model.h\"\nint main() { Model item; return item.value; }\n"
            : language == "python" ? "from model import Model\nitem = Model()\nvalue = item.value\n"
            : "class Program { static void Main() { var item = new Model(); _ = item.Value; } }\n";
        int line = language == "python" ? 2 : language == "cpp" ? 1 : 0;
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            if (language == "csharp") File.WriteAllText(Path.Combine(root, "Probe.csproj"), CSHARP_PROJECT);
            File.WriteAllText(model, language == "cpp" ? "struct Model { int value = 42; };\n" : language == "python" ? "class Model:\n    value: int = 42\n" : "class Model { public int Value = 42; }\n");
            File.WriteAllText(file, source);
            explorer.OpenFolder(root);
            Open(file);
        });
        test.WaitFor(() => languages.CanServe(document, "typeDefinitionProvider"), 60f);
        test.Check("Type definition: " + language + " server supports native type navigation", () => languages.CanServe(document, "typeDefinitionProvider"));
        test.Run(() => code.GoTo(line, source.Split('\n')[line].LastIndexOf("item", StringComparison.Ordinal) + 1));
        test.Check("Type definition: " + language + " command is available", () => AllCommands().Any(command => command.Text.EndsWith(Said("code_server.go_to_type_definition"), StringComparison.Ordinal)));
        test.Run(() => AllCommands().FirstOrDefault(command => command.Text.EndsWith(Said("code_server.go_to_type_definition"), StringComparison.Ordinal)).Picked?.Invoke());
        test.WaitFor(() => document.Path is { } path && PlatformPaths.Same(path, model), 15f);
        test.Check("Type definition: " + language + " variable navigates to its class", () => document.Path is { } path && PlatformPaths.Same(path, model)
            && code.Document.Caret.Line == 0 && code.Document.LineAt(0).AsSpan(code.Document.Caret.Column).StartsWith("Model"));
    }
}
