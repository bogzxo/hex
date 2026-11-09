using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestOnTypeFormatting(HexPointerScript test, string language)
    {
        bool csharp = language == "csharp", cpp = language == "cpp";
        string root = Path.Combine(testFolder, "on type formatting " + language);
        string file = Path.Combine(root, csharp ? "Program.cs" : cpp ? "main.cpp" : "main.py");
        string original = csharp ? "class Program\n{\n    static void Main()\n    {\n        int value=1+2\n        System.Console.WriteLine(value);\n    }\n}\n"
            : cpp ? "int main() {\n  int value=1+2;\n  return value;\n}\n" : "value=1+2\nprint(value)\n";
        int line = csharp ? 4 : cpp ? 1 : 0;
        char typed = csharp ? ';' : UIKeyboard.ENTER;
        string raw = "", formatted = "";
        UITextPosition rawCaret = default;
        bool wasEnabled = true;
        void Type()
        {
            code.Document.MoveTo(new(line, code.Document.LineAt(line).Length));
            typeof(CodeEditor).GetMethod("OnTextInput", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(code, [typed]);
            raw = code.Text;
            rawCaret = code.Document.Caret;
        }
        test.Run(() =>
        {
            wasEnabled = settings.FormatOnType;
            Directory.CreateDirectory(root);
            if (csharp) File.WriteAllText(Path.Combine(root, "Probe.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, original);
            explorer.OpenFolder(root);
            Open(file);
            settings.FormatOnType = true;
        });
        test.WaitFor(() => languages.CanServe(document, csharp || cpp ? "documentOnTypeFormattingProvider" : "documentSymbolProvider")
            && (!csharp || code.Diagnostics?.Any(diagnostic => diagnostic.Message.Contains("; expected", StringComparison.Ordinal)) == true), 60f);
        test.Check("Format on type: " + language + " native capability is negotiated", () =>
            languages.CanServe(document, "documentOnTypeFormattingProvider") == (csharp || cpp));
        test.Run(Type);
        if (!csharp && !cpp)
        {
            test.Settle(0.8f);
            test.Check("Format on type: unsupported Python server preserves normal newline editing", () => code.Text == raw && code.Text.StartsWith("value=1+2\n", StringComparison.Ordinal));
        }
        else
        {
            test.WaitFor(() => code.Text.Contains("int value = 1 + 2;", StringComparison.Ordinal), 10f);
            test.Run(() => formatted = code.Text);
            test.Check("Format on type: " + language + " native formatting follows the typed trigger", () => formatted.Contains("int value = 1 + 2;", StringComparison.Ordinal));
            test.Check("Format on type: " + language + " formatting preserves the code around the statement", () =>
                code.Text.Contains(csharp ? "        System.Console.WriteLine(value);" : "  return value;", StringComparison.Ordinal));
            test.Check("Format on type: " + language + " caret stays after the typed statement or on its new line", () =>
                code.Document.Caret == (csharp ? new UITextPosition(line, code.Document.LineAt(line).Length) : new UITextPosition(line + 1, 2)));
            test.Run(() => code.Document.Undo());
            test.Check("Format on type: " + language + " one undo removes automatic formatting without removing typing", () => code.Text == raw && code.Document.Caret == rawCaret);
            test.Run(() => code.Document.Redo());
            test.Check("Format on type: " + language + " redo restores the native edits", () => code.Text == formatted);
            test.Run(() => { code.Text = original; settings.FormatOnType = false; Type(); });
            test.Settle(0.8f);
            test.Check("Format on type: " + language + " disabling preserves the typed text", () => code.Text == raw);
            test.Run(() =>
            {
                settings.FormatOnType = true;
                code.Text = original;
                Type();
                code.Document.Insert("// newer typing");
                raw = code.Text;
            });
            test.Settle(0.8f);
            test.Check("Format on type: " + language + " late edits cannot overwrite newer typing", () => code.Text == raw);
            test.Run(() => { code.Text = original; Type(); code.Document.MoveTo(new(0, 0)); });
            test.Settle(0.8f);
            test.Check("Format on type: " + language + " moving the caret retires the pending format", () => code.Text == raw && code.Document.Caret == new UITextPosition(0, 0));
            test.Run(() => { code.Text = original; Type(); settings.FormatOnType = false; });
            test.Settle(0.8f);
            test.Check("Format on type: " + language + " disabling also retires a pending format", () => code.Text == raw);
            HexDocument? background = null;
            test.Run(() =>
            {
                settings.FormatOnType = true;
                code.Text = original;
                Type();
                background = document;
                string other = Path.Combine(root, "other.txt");
                File.WriteAllText(other, "another document\n");
                Open(other);
            });
            test.Settle(0.8f);
            test.Check("Format on type: " + language + " switching documents retires the pending format", () => background is not null
                && document != background && background.Editor.Text == raw && code.Text == "another document\n");
            test.Run(() => Open(file));
        }
        test.Check("Format on type: " + language + " edits remain unsaved", () => document.Modified && File.ReadAllText(file) == original);
        test.Run(() => settings.FormatOnType = wasEnabled);
    }

    private void TestOnTypeFormattingSettings(HexPointerScript test)
    {
        bool wasEnabled = true;
        test.Run(() => { wasEnabled = settings.FormatOnType; OpenSettings(); });
        test.Wait();
        test.Check("Format on type: setting appears in Editor Text", () => SettingsPage!.Settings.Any(setting => setting.Key == "format_on_type" && setting.Group == EditorSettingsCatalog.TEXT));
        test.Run(() => SettingsPage!.Enter("format_on_type", Values.Bool(false)));
        test.Check("Format on type: settings control disables native formatting", () => !settings.FormatOnType);
        test.Run(() => SettingsPage!.Reset("format_on_type"));
        test.Check("Format on type: resetting restores native formatting", () => settings.FormatOnType);
        test.Run(() => { SettingsPage!.Enter("format_on_type", Values.Bool(wasEnabled)); CloseSettings(); });
    }
}
