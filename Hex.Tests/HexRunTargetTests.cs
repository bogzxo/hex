using Hex.Core;
using Hex.Core.Projects;
using Hex.Dawn;

namespace Hex.Tests;

/// <summary>
/// What F5 runs, kept with the workspace in its file (the path from the file's folder) and read back, gone again when
/// it's the file on screen, whether a run task runs a project, and a run task's dotnet run read for F5 to debug.
/// </summary>
public sealed class HexRunTargetTests
{
    [Fact]
    public void ItsKeptWithTheWorkspaceRelativeToItsFile()
    {
        string folder = Directory.CreateTempSubdirectory("hex-run-target-").FullName;
        try
        {
            string file = Path.Combine(folder, "game.hex-workspace");
            var workspace = new HexWorkspace { RunTarget = new HexRunTarget(HexRunTarget.TASK, Path.Combine(folder, "hex"), "run") };
            workspace.Add(Path.Combine(folder, "hex"));
            workspace.Save(file);

            Assert.Contains("path: \"hex\"", File.ReadAllText(file));
            Assert.True(new HexRunTarget(HexRunTarget.TASK, Path.Combine(folder, "hex"), "run").Same(HexWorkspace.Load(file).RunTarget));

            workspace.RunTarget = null;
            workspace.Save(file);
            Assert.DoesNotContain("run_target", File.ReadAllText(file));
            Assert.Null(HexWorkspace.Load(file).RunTarget);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData("dotnet run --project Hex/Hex.csproj --no-build -- --window", ".", true)]
    [InlineData("dotnet run", "Hex", true)]
    [InlineData("dotnet run", ".", false)]
    [InlineData("dotnet run --project Other/Other.csproj", "Hex", false)]
    [InlineData("dub run", "Hex", false)]
    public void AProjectARunTaskRunsAlreadyIsLeftOff(string command, string folder, bool runs)
    {
        string root = Path.GetFullPath("C:/work");
        var task = new HexTask { Command = command, Folder = folder, Root = root, Group = "run" };
        Assert.Equal(runs, HexTasks.Runs(task, Path.Combine(root, "Hex", "Hex.csproj")));
    }

    [Fact]
    public void AProjectNobodyRunsIsDotnetRunInItsFolder()
    {
        HexTask task = HexTasks.ProjectTask(Path.GetFullPath("C:/work/Game/Game.csproj"));
        Assert.True(task is { Name: "Game", Command: "dotnet run --project \"Game.csproj\"", Group: "run" });
        Assert.Equal(Path.GetFullPath("C:/work/Game"), task.WorkingFolder());
    }

    [Fact]
    public void A_run_tasks_dotnet_run_says_what_F5_debugs()
    {
        string folder = Directory.CreateTempSubdirectory("hex-run-line-").FullName;
        try
        {
            string project = Path.Combine(folder, "Horizon.Testing", "Horizon.Testing.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(project)!);
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            DotnetRunLine? run = DotnetRunLine.Of("dotnet run --project Horizon.Testing/Horizon.Testing.csproj --no-build -c Release -- --checks \"a b\"", folder);
            Assert.NotNull(run);
            Assert.Equal(Path.GetFullPath(project), run.Project);
            Assert.Equal("Release", run.Configuration);
            Assert.Equal(["--checks", "a b"], run.Arguments);
            Assert.False(run.NoProfile);

            // Its arguments the launch profile's when it gives none, the folder's own project when it names none
            run = DotnetRunLine.Of("dotnet run --no-launch-profile", Path.GetDirectoryName(project)!);
            Assert.True(run is { Arguments: null, NoProfile: true, Configuration: null });

            Assert.Null(DotnetRunLine.Of("dotnet build && dotnet run --project Horizon.Testing/Horizon.Testing.csproj", folder));
            Assert.Null(DotnetRunLine.Of("dotnet run --project Missing/Missing.csproj", folder));
            Assert.Null(DotnetRunLine.Of("dotnet test Horizon.Testing/Horizon.Testing.csproj", folder));
            Assert.Null(DotnetRunLine.Of("dotnet run", folder));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
