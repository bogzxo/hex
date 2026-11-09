namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestFrameworkNavigation(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "framework navigation"), file = Path.Combine(root, "Program.cs");
        const string source = "using System;\nclass Program\n{\n    static void Main()\n    {\n        Version version = new Version(1, 2);\n        Console.WriteLine(version.Major);\n    }\n}\n";
        HexDocument original = null!;
        string versionPath = "", viewText = "";
        DateTime written = default;
        bool formatOnSave = false;
        void GoToVersion()
        {
            Open(file);
            code.GoTo(5, code.Document.LineAt(5).IndexOf("Version", StringComparison.Ordinal) + 1);
            languages.GoToDefinition(document);
        }
        bool IsSource(string declaration) => document.Path is {} path && PlatformPaths.IsUnder(path, HexLanguages.DecompiledFolder)
            && code.Text.Contains(declaration, StringComparison.Ordinal);
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "Probe.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, source);
            explorer.OpenFolder(root);
            Open(file);
            original = document;
            code.Document.MoveToEnd(false);
            code.Document.Insert("// unsaved source stays here\n");
        });
        test.WaitFor(() => languages.CanServe(document, "definitionProvider"), 60f);
        test.Run(GoToVersion);
        test.WaitFor(() => IsSource("class Version"), 30f);
        test.Check("Framework navigation: F12 opens native decompiled Version", () => IsSource("class Version"));
        test.Check("Framework navigation: library view is read-only and retains native navigation", () => code.ReadOnly && !document.Modified
            && languages.CanServe(document, "definitionProvider") && languages.CanServe(document, "hoverProvider"));
        test.Check("Framework navigation: save commands are disabled for library source", () =>
            new[] { "documents.save", "documents.save_as" }.All(key =>
                Commands().FirstOrDefault(pair => LabelOf(pair.Item) == Said(key).TrimEnd('.')).Item?.IsEnabled?.Invoke() == false));
        test.Run(() =>
        {
            if (!IsSource("class Version")) return;
            versionPath = document.Path!;
            viewText = code.Text;
            written = File.GetLastWriteTimeUtc(versionPath);
            typeof(CodeEditor).GetMethod("OnTextInput", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(code, ['x']);
            formatOnSave = settings.FormatOnSave;
            settings.FormatOnSave = true;
            SaveFormatted();
            SaveTo(Path.Combine(root, "forbidden-copy.cs"));
            settings.FormatOnSave = formatOnSave;
            languages.Rename(document);
            typeof(HexLanguages).GetMethod("FormatDocument", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(languages, null);
            languages.QuickFix();
        });
        test.Settle(0.3f);
        test.Check("Framework navigation: editing, save and refactoring cannot modify library source", () => code.Text == viewText
            && document.Path == versionPath && File.GetLastWriteTimeUtc(versionPath) == written && !File.Exists(Path.Combine(root, "forbidden-copy.cs"))
            && textDialog?.IsOpen != true && !languages.CanServe(document, "renameProvider") && !languages.CanServe(document, "documentFormattingProvider"));
        test.Run(() =>
        {
            for (int line = 0; line < code.Document.LineCount; line++)
                if (code.Document.LineAt(line).IndexOf("ICloneable", StringComparison.Ordinal) is >= 0 and var column)
                {
                    code.GoTo(line, column + 1);
                    languages.GoToDefinition(document);
                    break;
                }
        });
        test.WaitFor(() => IsSource("interface ICloneable"), 30f);
        test.Check("Framework navigation: F12 inside decompiled source follows its native interface", () => IsSource("interface ICloneable")
            && document.Path != versionPath && code.ReadOnly && languages.CanServe(document, "definitionProvider"));
        test.Check("Framework navigation: original unsaved text and project file are preserved", () => original.Editor.Text == source + "// unsaved source stays here\n"
            && File.ReadAllText(file) == source);
        test.Run(GoToVersion);
        test.WaitFor(() => document.Path == versionPath, 15f);
        test.Check("Framework navigation: repeated navigation reuses its source view", () => document.Path == versionPath
            && documents.Count(open => open.Path == versionPath) == 1 && code.ReadOnly);
        test.Run(() => { CloseNow(); Open(versionPath); });
        test.Check("Framework navigation: reopening a cached source remains read-only", () => code.ReadOnly && code.Text == viewText && !document.Modified);
        test.Run(GoToVersion);
        test.WaitFor(() => document.Path == versionPath && languages.CanServe(document, "definitionProvider"), 15f);
        test.Check("Framework navigation: revisiting reconnects the native source after close", () => document.Path == versionPath
            && languages.CanServe(document, "definitionProvider") && code.ReadOnly);
    }
}
