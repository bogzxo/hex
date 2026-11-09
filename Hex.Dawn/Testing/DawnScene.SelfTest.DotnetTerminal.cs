using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestDotnetTerminal(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "dotnet terminal");
        TerminalView? view = null;
        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(folder, "Library & Tools.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            explorer.OpenFolder(folder);
            terminalPanel.AnimationTime = 0;
            foreach (var tab in terminalPanel.Tabs.ToArray()) terminalPanel.CloseTab(tab);
        });
        test.Wait();
        test.Run(ToggleTerminalPage);
        test.WaitFor(() => terminalPanel.FocusedView?.Screen.AtPrompt == true, 15f);
        test.Run(() => view = terminalPanel.FocusedView);
        test.Check("dotnet terminal: shell has built-in completion", () => view?.Completer is not null);
        test.Run(() => view?.Send("dotnet package add New"));
        test.Settle(0.2f);
        test.Run(() => view?.Type("t"));
        test.WaitFor(() => view?.Completions.Any(item => item.Text.Equals("newtonsoft.json", StringComparison.OrdinalIgnoreCase)) == true, 12f);
        test.Check("dotnet terminal: native NuGet package suggestions appear", () => view?.Completions.Any(item => item.Text.Equals("newtonsoft.json", StringComparison.OrdinalIgnoreCase)) == true);
        test.Run(() => view?.AcceptCompletion());
        test.WaitFor(() => view?.TypedLine()?.Contains("newtonsoft.json", StringComparison.OrdinalIgnoreCase) == true, 5f);
        test.Check("dotnet terminal: accepting inserts package without running it", () => view?.Screen.AtPrompt == true && view.TypedLine()?.Contains("newtonsoft.json", StringComparison.OrdinalIgnoreCase) == true && !Directory.Exists(Path.Combine(folder, "obj")));
        test.Run(() => view?.Send("\u0003"));
        test.Settle(0.3f);
        test.Run(() => view?.Send("dotnet build --configuration"));
        test.Settle(0.2f);
        test.Run(() => view?.Type(" "));
        test.WaitFor(() => view?.Completions.Any(item => item.Text == "Debug") == true, 10f);
        test.Check("dotnet terminal: options appear after a space", () => view?.Completions.Any(item => item.Text == "Debug") == true);
        test.Run(() => view?.Send("\u0003"));
        test.Settle(0.3f);
        test.Run(() => view?.Send("dotnet build \"App.csproj\" --framework "));
        test.Settle(0.2f);
        test.Run(() => view?.Type("n"));
        test.WaitFor(() => view?.Completions.Any(item => item.Text == "net11.0") == true, 10f);
        test.Check("dotnet terminal: framework suggestions use the current project", () => view?.Completions.Any(item => item.Text == "net11.0") == true);
        test.Run(() => view?.Send("\u0003"));
        test.Settle(0.3f);
        test.Run(() => view?.Send("dotnet build \"Library & Tools.csproj\" --framework "));
        test.Settle(0.2f);
        test.Run(() => view?.Type("n"));
        test.WaitFor(() => view?.Completions.Any(item => item.Text == "net9.0") == true, 10f);
        test.Check("dotnet terminal: quoted project selects its own framework", () => view?.Completions.Any(item => item.Text == "net9.0") == true
            && !view.Completions.Any(item => item.Text == "net11.0"));
        test.Run(() => view?.AcceptCompletion());
        test.WaitFor(() => view?.TypedLine()?.EndsWith("--framework net9.0", StringComparison.Ordinal) == true, 5f);
        test.Check("dotnet terminal: insertion preserves the quoted project without executing", () => view?.Screen.AtPrompt == true
            && view.TypedLine() == "dotnet build \"Library & Tools.csproj\" --framework net9.0" && !Directory.Exists(Path.Combine(folder, "obj")));
        test.Run(() => { view?.Send("\u0003"); foreach (var tab in terminalPanel.Tabs.ToArray()) terminalPanel.CloseTab(tab); });
    }
}
