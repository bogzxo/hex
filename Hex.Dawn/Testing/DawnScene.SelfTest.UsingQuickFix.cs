using System.Numerics;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestUsingQuickFix(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "using quick fix");
        string file = Path.Combine(root, "Program.cs");
        const string original = "class Program\n{\n    static void Main()\n    {\n        HexMissingImport value = new();\n        System.Console.WriteLine(value);\n    }\n}\n";
        string before = "";
        var settings = new HexBreakpoint(Condition: "value != null", Enabled: false);
        ListBox? Choices() => textDialog is { IsOpen: true } dialog ? Descendants(dialog.Body).OfType<ListBox>().FirstOrDefault() : null;
        int StatementLine() => Array.FindIndex(code.Text.Split('\n'), line => line.Contains("System.Console.WriteLine"));
        bool BreakpointFollows() => code.Breakpoints is { Count: 1 } marks && marks.Contains(StatementLine()) && debugger.SettingsOf(file, StatementLine()) == settings;
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "Probe.csproj"), CSHARP_PROJECT);
            File.WriteAllText(Path.Combine(root, "Library.cs"), "namespace Tools { public class HexMissingImport {} }\n");
            File.WriteAllText(file, original);
            explorer.OpenFolder(root);
            Open(file);
        });
        test.WaitFor(() => code.Diagnostics.Any(item => item.Message.Contains("CS0246")), 90);
        test.Run(() =>
        {
            code.Document.MoveToEnd(false);
            code.Document.Insert("// unsaved work\n");
            before = code.Text;
            debugger.Settle(file, 5, settings);
            code.Document.MoveTo(new(0, 0));
        });

        // The lightbulb, nothing asked of a line with nothing wrong, the server asked once the caret rests on the line
        // that has, and pressed in the gutter it's Ctrl+.'s menu
        int asksBefore = 0;
        test.Settle(0.6f);
        test.Run(() => asksBefore = languages.LightbulbAsks);
        test.Check("Lightbulb: none on a line with nothing wrong, and nothing asked for it", () => code.ActionLine < 0 && code.ActionBounds is null && languages.LightbulbAsks == asksBefore);
        test.Run(() => code.Document.MoveTo(new(4, 8)));
        test.WaitFor(() => code.ActionLine == 4 && code.ActionBounds is not null, 20);
        test.Check("Lightbulb: shown in the gutter on the caret's line with a fix", () => code.ActionLine == 4 && code.ActionBounds is not null);
        test.Settle(0.6f);
        test.Check("Lightbulb: the server asked at most once for the line", () => languages.LightbulbAsks - asksBefore <= 1);
        test.Click(() => code.Module is { } module && code.ActionBounds is { } bulb ? module.ToWorld(bulb.Center) : Vector2.Zero);
        test.WaitFor(() => Choices()?.Items.Any(item => item.Contains("using Tools;")) == true, 20);
        test.Check("Using quick fix: native missing-namespace fix is offered first", () => Choices()?.Items.FirstOrDefault()?.Contains("using Tools;") == true);
        test.Run(() =>
        {
            if (Choices() is { } list && Array.FindIndex(list.Items, item => item.Contains("using Tools;")) is var at && at >= 0)
            {
                list.Choose(at);
                list.OnActivated?.Invoke(list.Items[at]);
            }
        });
        test.WaitFor(() => code.Text.StartsWith("using Tools;", StringComparison.Ordinal), 10);
        test.Check("Using quick fix: import is added while unsaved work is preserved", () => code.Text.StartsWith("using Tools;", StringComparison.Ordinal) && code.Text.EndsWith("// unsaved work\n", StringComparison.Ordinal));
        test.Check("Using quick fix: breakpoint and its settings follow the statement", BreakpointFollows);
        test.Check("Using quick fix: caret follows the affected source line", () => code.Document.Caret.Line == StatementLine() - 1 && code.Document.Caret.Column == 8);
        test.WaitFor(() => languages.ServerDiagnosticsOf(document) is { Count: 0 }, 20);
        test.Check("Using quick fix: native diagnostics clear without saving", () => languages.ServerDiagnosticsOf(document) is { Count: 0 } && File.ReadAllText(file) == original);
        test.Run(() => code.Document.Undo());
        test.Check("Using quick fix: undo restores text, caret and breakpoint", () => code.Text == before && code.Document.Caret == new UITextPosition(4, 8) && BreakpointFollows());
        test.Run(() => code.Document.Redo());
        test.Check("Using quick fix: redo carries the breakpoint with the statement", () => code.Text.StartsWith("using Tools;", StringComparison.Ordinal) && BreakpointFollows());
        test.Run(() => debugger.breakpoints.Remove(file));
    }
}
