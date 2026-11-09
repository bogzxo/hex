namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestNuGetRestore(HexPointerScript test)
    {
        if (debugger.Netcoredbg() is null)
        {
            test.Check("NuGet restore: installed netcoredbg is required", () => false);
            return;
        }
        string root = Path.Combine(testFolder, "nuget restore");
        string project = Path.Combine(root, "App.csproj"), source = Path.Combine(root, "Program.cs");
        const string validProject = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><NuGetAudit>false</NuGetAudit></PropertyGroup></Project>";
        test.Run(() =>
        {
            Directory.CreateDirectory(Path.Combine(root, "empty-feed"));
            File.WriteAllText(Path.Combine(root, "NuGet.Config"), "<configuration><packageSources><clear/><add key=\"fixture\" value=\"empty-feed\"/></packageSources></configuration>");
            File.WriteAllText(project, validProject.Replace("</Project>", "<ItemGroup><PackageReference Include=\"Hex.MissingPackage.Fixture\" Version=\"999.0.0\"/></ItemGroup></Project>"));
            File.WriteAllText(source, "System.Console.WriteLine(42);\n");
            explorer.OpenFolder(root);
            Open(source);
            RunOrPlay();
        });
        test.WaitFor(() => !debugger.Debugging && terminalProblems.Any(problem => problem.Text.Contains("NU1101")), 40);
        test.Check("NuGet restore: missing package identifies its project in Problems", () => terminalProblems.Any(problem =>
            PlatformPaths.Same(problem.Path, project) && !problem.Warning && problem.Text.Contains("NU1101") && problem.Text.Contains("Hex.MissingPackage.Fixture")));
        test.WaitFor(() => Descendants(problems).OfType<Button>().Any(button => button.Tooltip.Contains("NU1101")), 5);
        test.Run(() => Descendants(problems).OfType<Button>().FirstOrDefault(button => button.Tooltip.Contains("NU1101"))?.OnPressed?.Invoke());
        test.Check("NuGet restore: Problems opens the package-owning project", () => PlatformPaths.Same(document.Path!, project));
        test.Run(() =>
        {
            code.Text = validProject;
            Save();
            Context.Terminals.AddProblem(source, 1, 1, "stale NuGet build error", false);
            Open(source);
            debugger.ToggleBreakpoint(source, 0);
            RunOrPlay();
        });
        test.WaitFor(() => debugger.DebugStopped, 90);
        test.Check("NuGet restore: correcting the package reference restores and launches", () => debugger.DebugStopped
            && PlatformPaths.Same(document.Path!, source) && code.ExecutionLine == 0);
        test.Check("NuGet restore: successful build clears stale package errors", () => !terminalProblems.Any(problem =>
            problem.Text.Contains("NU1101") || problem.Text == "stale NuGet build error"));
        test.Run(debugger.StopDebugging);
        test.WaitFor(() => !debugger.Debugging, 10);
        test.Check("NuGet restore: recovered session stops cleanly", () => !debugger.Debugging);
        test.Run(() => debugger.ToggleBreakpoint(source, 0));
    }
}
