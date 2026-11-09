using Hex.Core.Projects;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestProjectReferences(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "project-references"), project = Path.Combine(folder, "App.csproj"), sln = Path.Combine(folder, "References.slnx");
        string target = Path.Combine(folder, "shared", "C.csproj");
        SolutionProject? app = null;
        int notices = 0;
        test.Run(() =>
        {
            Directory.CreateDirectory(Path.Combine(folder, "shared"));
            foreach (string name in new[] { "B", "C" })
                File.WriteAllText(Path.Combine(folder, "shared", name + ".csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><HorizonRoot Condition=\"'$(HorizonRoot)' == ''\">shared/</HorizonRoot></PropertyGroup><ItemGroup><ProjectReference Include=\"$(HorizonRoot)B.csproj\" /></ItemGroup></Project>");
            File.WriteAllText(sln, "<Solution><Project Path=\"App.csproj\" /><Project Path=\"shared/B.csproj\" /><Project Path=\"shared/C.csproj\" /></Solution>");
            explorer.OpenFolder(folder);
            OpenSolution(sln);
        });
        test.WaitFor(() => solution is not null && PlatformPaths.Same(solution.Path, sln), 20f);
        test.Run(() => { app = solution!.Projects.First(item => item.Name == "App"); AddProjectReference(app); });
        test.WaitFor(() => textDialog is { IsOpen: true }, 60f);
        test.Check("project references: picker excludes the evaluated existing reference", () =>
            textDialog is { IsOpen: true } && Descendants(textDialog).OfType<ListBox>().First().Items.Count() == 1);
        test.Run(() => notices = said.Count);
        PressKeys(test, "Enter");
        test.WaitFor(() => said.Skip(notices).Any(entry => entry.Error || entry.Text == Said("solution.reference_added", ("name", "C"), ("project", "App"))), 60f);
        test.Check("project references: add reports verified success", () => said.Skip(notices).Any(entry => !entry.Error && entry.Text == Said("solution.reference_added", ("name", "C"), ("project", "App"))));
        test.Check("project references: picker adds a property-based expression", () => File.ReadAllText(project).Contains("Include=\"$(HorizonRoot)C.csproj\""));
        test.Run(() => { notices = said.Count; RemoveReference(app!, target); });
        test.WaitFor(() => said.Skip(notices).Any(entry => entry.Error || entry.Text == Said("solution.removed", ("name", "C"), ("project", "App"))), 60f);
        test.Check("project references: remove reports verified success", () => said.Skip(notices).Any(entry => !entry.Error && entry.Text == Said("solution.removed", ("name", "C"), ("project", "App"))));
        test.Check("project references: removal preserves the other expression", () =>
            !File.ReadAllText(project).Contains("C.csproj") && File.ReadAllText(project).Contains("$(HorizonRoot)B.csproj"));
    }
}
