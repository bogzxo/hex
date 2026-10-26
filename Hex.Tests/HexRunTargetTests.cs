using Hex.Core;
using Hex.Dawn;

namespace Hex.Tests;

/// <summary>
/// What F5 runs, kept with the workspace in its file (the path from the file's folder) and read back, gone again when
/// it's the file on screen, and a project left off the arrow's list when a run task runs it already.
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
}
