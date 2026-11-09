using Hex.Core;
using Hex.Dawn;

namespace Hex.Tests;

/// <summary>
/// What a task depends on followed all the way down, the run task's build and the build's build_solution before it, in
/// the order they run, and a loop of tasks that name each other run once each rather than for ever.
/// </summary>
public sealed class HexTaskChainTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "hex-chain"));

    private static HexTask Task(string name, string command, string dependsOn = "", string folder = ".") =>
        new() { Name = name, Command = command, DependsOn = dependsOn, Folder = folder, Root = Root };

    private static List<HexTask> ChainOf(HexTask task, params HexTask[] tasks) =>
        HexTasks.ChainOf(task, name => tasks.FirstOrDefault(other => other.Name == name));

    [Fact]
    public void ADependencyOfADependencyRunsFirst()
    {
        HexTask solution = Task("build_solution", "dotnet build Horizon.sln");
        HexTask build = Task("build", "dotnet build Horizon.Shaders.Build", "build_solution");
        HexTask run = Task("run", "dotnet run --no-build", "build");

        Assert.Equal(["build_solution", "build"], ChainOf(run, solution, build, run).Select(task => task.Name));
        Assert.Equal("dotnet build Horizon.sln; if ($?) { dotnet build Horizon.Shaders.Build; if ($?) { dotnet run --no-build } }",
            HexTasks.ChainedLine([solution, build, run], "powershell"));
        Assert.Equal("dotnet build Horizon.sln && dotnet build Horizon.Shaders.Build && dotnet run --no-build",
            HexTasks.ChainedLine([solution, build, run], "pwsh"));
    }

    [Fact]
    public void ALoopRunsEachOnce()
    {
        HexTask a = Task("a", "echo a", "b");
        HexTask b = Task("b", "echo b", "c");
        HexTask c = Task("c", "echo c", "a");

        Assert.Equal(["c", "b"], ChainOf(a, a, b, c).Select(task => task.Name));
        Assert.Empty(ChainOf(Task("self", "echo self", "self")));
    }

    [Fact]
    public void ANameThatIsntThereEndsIt()
    {
        HexTask build = Task("build", "dotnet build", "gone");
        HexTask run = Task("run", "dotnet run", "build");

        Assert.Equal(["build"], ChainOf(run, build, run).Select(task => task.Name));
    }

    [Fact]
    public void EachRunsInItsOwnFolder()
    {
        HexTask engine = Task("engine", "dotnet build", folder: "engine");
        HexTask run = Task("run", "dotnet run", "engine");

        string engineFolder = Path.Combine(Root, "engine");
        Assert.Equal($"cd \"{engineFolder}\" && dotnet build && cd \"{Root}\" && dotnet run", HexTasks.ChainedLine([engine, run], "bash"));
        Assert.Equal("dotnet run", HexTasks.ChainedLine([run], "bash"));
    }
}
