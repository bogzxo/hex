using Hex.Core.Projects;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestSaveEvaluation(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "save-evaluation");
        string sln = Path.Combine(folder, "Save.slnx"), source = Path.Combine(folder, "Game", "Source.cs"), added = Path.Combine(folder, "Linked", "Added.cs");
        SolutionProject? project = null;
        int generation = 0;
        test.Run(() =>
        {
            Directory.CreateDirectory(Path.Combine(folder, "Game"));
            Directory.CreateDirectory(Path.Combine(folder, "Linked"));
            File.WriteAllText(Path.Combine(folder, "Linked", "Existing.cs"), "class Existing {}\n");
            File.WriteAllText(Path.Combine(folder, "Game", "Save.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include=\"../Linked/**/*.cs\" Link=\"Linked/%(Filename)%(Extension)\" /></ItemGroup></Project>");
            File.WriteAllText(sln, "<Solution><Project Path=\"Game/Save.csproj\" /></Solution>");
            File.WriteAllText(source, "class Source {}\n");
            explorer.OpenFolder(folder);
            OpenSolution(sln);
        });
        test.WaitFor(() => solution is not null && PlatformPaths.Same(solution.Path, sln), 20f);
        test.Run(() => { project = solution!.Projects.Single(); EnsureSolutionDependencies(project); });
        test.WaitFor(() => project!.DependencyConfiguration == Configuration && !project.DependenciesLoading, 60f);
        test.Settle(1f);
        test.Check("save evaluation: evaluated source belongs to the project", () => project!.Files.Any(file => PlatformPaths.Same(file.Path, source)));
        test.Run(() => { generation = project!.EvaluationGeneration; Open(source); });
        for (int i = 0; i < 3; i++)
        {
            int value = i;
            test.Run(() => { code.Text = $"class Source {{ int value = {value}; }}\n"; Save(); });
            test.Settle(0.8f);
        }
        test.Check("save evaluation: three actual editor saves do not invalidate project evaluation", () =>
            project!.EvaluationGeneration == generation && project.DependencyConfiguration == Configuration && !project.DependenciesLoading);
        test.Check("save evaluation: edited bytes reach disk", () => File.ReadAllText(source).Contains("value = 2"));
        test.Run(() => File.WriteAllText(added, "class Added {}\n"));
        test.WaitFor(() => project!.EvaluationGeneration > generation, 5f);
        test.Check("save evaluation: genuine linked-root creation invalidates membership", () => project!.EvaluationGeneration > generation);
        test.Run(() => EnsureSolutionDependencies(project!));
        test.WaitFor(() => project!.DependencyConfiguration == Configuration && project.Files.Any(file => PlatformPaths.Same(file.Path, added)), 60f);
        test.Check("save evaluation: added source appears as linked after evaluation", () => project!.Files.Any(file => file.Linked && PlatformPaths.Same(file.Path, added)));
        test.Run(() => { generation = project!.EvaluationGeneration; File.Delete(added); });
        test.WaitFor(() => project!.EvaluationGeneration > generation, 5f);
        test.Check("save evaluation: genuine removal invalidates membership", () => project!.EvaluationGeneration > generation);
        test.Run(() => { Console.WriteLine($"[Hex save evaluation] final generation={project!.EvaluationGeneration}"); CloseNow(); });
    }
}
