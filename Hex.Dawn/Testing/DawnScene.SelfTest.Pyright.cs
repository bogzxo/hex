using Horizon.UI.Scripting;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    // Opt-in acceptance suite: requires a real global Pyright installation, never downloads one.
    private void TestPyright(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "engineering_pyright.py");
        const string source = "import math\nlength: float = 2.0\nbad: int = 'wrong'\narea = math.sqrt(length)\nmath.sq\n";
        LanguageServer? server = null;
        Task<string?>? hover = null;
        Task<LanguageServerCompletions?>? completion = null;
        test.Check("pyright: global language server is found", () => HexServerCatalog.Locate("pyright-langserver") is not null);
        test.Run(() => { File.WriteAllText(file, source); Open(file); });
        test.WaitFor(() => (server = LanguageServers.Running.FirstOrDefault(running => running.Name == "python" && running.IsReady)) is not null, 30);
        test.Check("pyright: opening Python starts the global stdio server", () => server is { IsRunning: true, IsReady: true, Failure: null } && languages.LinterOf(document) == "python");
        test.WaitFor(() => code.Diagnostics.Any(diagnostic => diagnostic.Line == 2 && !diagnostic.Warning), 30);
        test.Check("pyright: assignment errors reach the Python editor", () => code.Diagnostics.Any(diagnostic => diagnostic.Line == 2 && !diagnostic.Warning));
        test.Run(() => { hover = server?.Hover(file, 1, 2); completion = server?.Complete(file, 4, 7, 5); });
        test.WaitFor(() => hover?.IsCompleted == true && completion?.IsCompleted == true, 20);
        test.Check("pyright: hover resolves Python types", () => hover is { IsCompletedSuccessfully: true } && hover.Result?.Contains("float") == true);
        test.Check("pyright: member completion includes math.sqrt", () => completion is { IsCompletedSuccessfully: true } && completion.Result is { } result && result.Items.Any(item => item.Text == "sqrt"));
        test.Run(() => { code.Document.SelectAll(); code.Document.Insert("import math\nlength: float = 2.0\nbad: int = 3\narea = math.sqrt(length)\n"); });
        test.WaitFor(() => code.Diagnostics.Count == 0, 30);
        test.Check("pyright: editing clears stale diagnostics", () => code.Diagnostics.Count == 0);
        test.Run(CloseNow);
        test.Wait();
    }
}
