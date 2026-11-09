namespace Hex.Dawn;

internal sealed partial class HexLanguages
{
    internal LanguageServer? TestServerFor(HexDocument document) => linters.GetValueOrDefault(document);
}

internal sealed partial class DawnScene
{
    private void TestSolutionSwitch(HexPointerScript test)
    {
        string first = Path.Combine(testFolder, "switch-first"), second = Path.Combine(testFolder, "switch-second");
        string sub = Path.Combine(second, "Library"), file = Path.Combine(sub, "Example.cs");
        const string disk = "public class Original { public int Value => 1; }\n";
        const string edited = "public class Unsaved { public int Value => 2; }\n";
        HexDocument? kept = null;
        LanguageServer? old = null;
        bool symbolsReturned = false, unsavedSymbol = false;
        test.Run(() =>
        {
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(sub);
            const string project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>";
            File.WriteAllText(Path.Combine(first, "First.csproj"), project);
            File.WriteAllText(Path.Combine(first, "First.slnx"), "<Solution><Project Path=\"First.csproj\" /></Solution>");
            File.WriteAllText(Path.Combine(sub, "Library.csproj"), project);
            File.WriteAllText(Path.Combine(second, "Second.slnx"), "<Solution><Project Path=\"Library/Library.csproj\" /></Solution>");
            File.WriteAllText(file, disk);
            OpenSolution(Path.Combine(first, "First.slnx"));
            Open(file);
            kept = document;
        });
        test.WaitFor(() => languages.CanServe(kept!, "documentSymbolProvider"), 90f);
        test.Check("solution switch: external file starts with its own project server", () =>
            languages.TestServerFor(kept!) is { } server && PlatformPaths.Same(server.RootDirectory, sub));
        test.Run(() =>
        {
            old = languages.TestServerFor(kept!);
            kept!.Editor.Text = edited;
            OpenSolution(Path.Combine(second, "Second.slnx"));
        });
        test.Check("solution switch: workspace services use the newly opened solution root", () =>
            Context.Workspace.Roots.Count == 1 && PlatformPaths.Same(Context.Workspace.Roots[0], second)
            && PlatformPaths.Same(Context.Workspace.Solution!, Path.Combine(second, "Second.slnx")));
        test.Check("solution switch: document and unsaved buffer survive without writing disk", () =>
            OpenDocument(file) == kept && kept!.Editor.Text == edited && File.ReadAllText(file) == disk);
        test.WaitFor(() => languages.CanServe(kept!, "documentSymbolProvider") && languages.TestServerFor(kept!) != old, 90f);
        test.Check("solution switch: already-open document rebinds to the new root", () =>
            languages.TestServerFor(kept!) is { } server && !ReferenceEquals(server, old) && PlatformPaths.Same(server.RootDirectory, second));
        test.Check("solution switch: unused previous server is retired from active services", () =>
            old is not null && !LanguageServers.Running.Contains(old));
        test.Run(() => languages.AskSymbols(kept!, symbols =>
        {
            symbolsReturned = true;
            static IEnumerable<LanguageServerSymbol> Flatten(IEnumerable<LanguageServerSymbol> rows) => rows.SelectMany(row => new[] { row }.Concat(Flatten(row.Children)));
            var all = Flatten(symbols).ToArray();
            Console.WriteLine("[Hex solution switch symbols] " + string.Join(" | ", all.Select(symbol => symbol.Name + ":" + symbol.Kind)));
            unsavedSymbol = all.Any(symbol => symbol.Name == "Unsaved") && !all.Any(symbol => symbol.Name == "Original");
        }));
        test.WaitFor(() => symbolsReturned, 30f);
        test.Check("solution switch: new server sees the unsaved document", () => symbolsReturned && unsavedSymbol);
        test.Run(() =>
        {
            old = languages.TestServerFor(kept!);
            string alternative = Path.Combine(second, "Z-Selected.slnx");
            File.WriteAllText(alternative, "<Solution><Project Path=\"Library/Library.csproj\" /></Solution>");
            OpenSolution(alternative);
        });
        test.WaitFor(() => languages.CanServe(kept!, "documentSymbolProvider") && languages.TestServerFor(kept!) != old, 90f);
        test.Check("solution switch: selecting another solution in the same folder replaces its server", () =>
            languages.TestServerFor(kept!) is { } server && !ReferenceEquals(server, old) && PlatformPaths.Same(server.RootDirectory, second)
            && Context.Workspace.Solution == Path.Combine(second, "Z-Selected.slnx"));
        test.WaitFor(() => old is { IsRunning: false }, 10f);
        test.Check("solution switch: previous solution server exits", () => old is { IsRunning: false } && !LanguageServers.Running.Contains(old));
        test.Run(() =>
        {
            old = languages.TestServerFor(kept!);
            OpenSolution(Path.Combine(second, "Z-Selected.slnx"));
        });
        test.Check("solution switch: reopening the selected solution reuses its server and unsaved document", () =>
            ReferenceEquals(languages.TestServerFor(kept!), old) && kept!.Editor.Text == edited && File.ReadAllText(file) == disk);
        test.Run(() => { Show(kept!); CloseNow(); });
    }
}
