using Horizon.UI.Scripting;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestPythonStubs(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "python stubs");
        string stub = Path.Combine(root, "api.pyi"), consumer = Path.Combine(root, "main.py");
        LanguageServer? server = null;
        Task<string?>? hover = null;
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(stub, "count: int = 'wrong'\ndef greet(name: str) -> str: ...\n");
            File.WriteAllText(Path.Combine(root, "api.py"), "count = 42\ndef greet(name): return str(name)\n");
            File.WriteAllText(consumer, "from api import greet\nvalue = greet(42)\n");
            explorer.OpenFolder(root);
            Open(stub);
        });
        test.WaitFor(() => (server = LanguageServers.Running.FirstOrDefault(s => s.Name == "python" && PlatformPaths.Same(s.RootDirectory, root))) is { IsReady: true }, 30);
        test.WaitFor(() => code.Diagnostics.Any(d => d.Line == 0 && !d.Warning), 30);
        test.Check("Python stubs: opening a stub starts Pyright and reports type errors", () =>
            languages.LinterOf(document) == "python" && code.Diagnostics.Any(d => d.Line == 0 && !d.Warning));
        test.Check("Python stubs: stub has highlighting but is not an executable script", () =>
            code.Highlighter is not null && !HexDebugging.SupportsDebugging(stub));
        test.Run(() => hover = server?.Hover(stub, 1, 5));
        test.WaitFor(() => hover?.IsCompleted == true, 15);
        test.Check("Python stubs: hover describes the declared function", () => hover is { IsCompletedSuccessfully: true }
            && hover.Result?.Contains("greet") == true && hover.Result.Contains("str"));
        test.Run(() => { code.Text = "count: int\ndef greet(name: str) -> str: ...\n"; Save(); });
        test.WaitFor(() => code.Diagnostics.Count == 0, 20);
        test.Check("Python stubs: repairing a declaration clears the error", () => code.Diagnostics.Count == 0);
        test.Run(() => Open(consumer));
        test.WaitFor(() => code.Diagnostics.Any(d => d.Line == 1 && !d.Warning), 30);
        test.Check("Python stubs: consumer calls are checked against stub types", () => code.Diagnostics.Any(d => d.Line == 1 && !d.Warning));
        test.Run(() => code.Text = "from api import greet\nvalue = greet('Ada')\n");
        test.WaitFor(() => code.Diagnostics.Count == 0, 20);
        test.Check("Python stubs: valid consumer call clears the error", () => code.Diagnostics.Count == 0);
    }
}
