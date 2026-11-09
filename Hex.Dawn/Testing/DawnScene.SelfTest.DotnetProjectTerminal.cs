namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestDotnetProjectTerminal(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "dotnet project terminal"), project = Path.Combine(root, "Nested App"), source = Path.Combine(project, "Models");
        TerminalView? original = null, selected = null;
        bool SdkReady()
        {
            try { return File.Exists(Path.Combine(project, "sdk-proof.txt")) && File.ReadAllText(Path.Combine(project, "sdk-proof.txt")).Trim().Length > 0; }
            catch (IOException) { return false; }
        }
        test.Run(() =>
        {
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(root, "Root.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(project, "App.csproj"), CSHARP_PROJECT);
            File.WriteAllText(Path.Combine(source, "Model.cs"), "class Model {}\n");
            explorer.OpenFolder(root);
            terminalPanel.AnimationTime = 0;
            NewTerminal();
            original = terminalPanel.FocusedView;
            Open(Path.Combine(source, "Model.cs"));
        });
        test.WaitFor(() => original?.Screen.AtPrompt == true, 15f);
        test.Check("Project terminal: command available from C# file", () => AllCommands().Any(command => command.Text.EndsWith(Said("languages.dotnet_terminal"), StringComparison.Ordinal)));
        test.Run(() =>
        {
            AllCommands().First(command => command.Text.EndsWith(Said("languages.dotnet_terminal"), StringComparison.Ordinal)).Picked?.Invoke();
            selected = terminalPanel.FocusedView;
        });
        test.WaitFor(() => selected?.Screen.AtPrompt == true, 15f);
        test.Check("Project terminal: opens a separate shell", () => selected is not null && !ReferenceEquals(original, selected));
        test.Run(() => selected?.Send("dotnet --version > sdk-proof.txt\r"));
        test.WaitFor(SdkReady, 15f);
        test.Check("Project terminal: dotnet runs beside the owning project", () => File.Exists(Path.Combine(project, "sdk-proof.txt")) && !File.Exists(Path.Combine(root, "sdk-proof.txt")));
        test.WaitFor(() => selected?.Screen.AtPrompt == true, 10f);
        test.Run(() => selected?.Send("dotnet build --framework "));
        test.Settle(0.2f);
        test.Run(() => selected?.Type("n"));
        test.WaitFor(() => selected?.Completions.Any(item => item.Text == "net11.0") == true, 12f);
        test.Check("Project terminal: SDK completion uses nested project", () => selected?.Completions.Any(item => item.Text == "net11.0") == true && !selected.Completions.Any(item => item.Text == "net9.0"));
        test.Run(() => selected?.Send("\u0003"));
        test.Run(() => original?.Send("echo original > original-proof.txt\r"));
        test.WaitFor(() => File.Exists(Path.Combine(root, "original-proof.txt")), 10f);
        test.Check("Project terminal: existing terminal keeps its directory", () => File.Exists(Path.Combine(root, "original-proof.txt")) && !File.Exists(Path.Combine(project, "original-proof.txt")));

        test.Run(() => { foreach (var tab in terminalPanel.Tabs.ToArray()) terminalPanel.CloseTab(tab); });
    }
}
