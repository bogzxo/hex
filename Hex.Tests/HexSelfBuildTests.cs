using Hex.Core.Projects;

namespace Hex.Tests;

/// <summary>
/// Hex building itself, the locked files of a folder moved aside and cleaned up, a command line read for where it builds
/// to, and the solutions and startup project of a folder that the tasks template is written from.
/// </summary>
public sealed class HexSelfBuildTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("hex-selfbuild-").FullName;

    [Fact]
    public void ALockedDllIsMovedAsidePutBackAndCleanedUp()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string locked = Path.Combine(folder, "Locked.dll"), free = Path.Combine(folder, "Free.dll");
        File.WriteAllText(locked, "old");
        File.WriteAllText(free, "free");

        List<string> moved;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            moved = HexSelfBuild.MoveAside(folder);
            Assert.Equal([locked], moved);
            Assert.False(File.Exists(locked));
            Assert.True(File.Exists(locked + ".old"));
            Assert.True(File.Exists(free));

            // A build that didn't write it, the old one back where it was
            Assert.Equal(1, HexSelfBuild.PutBack(moved));
            Assert.Equal("old", File.ReadAllText(locked));
            moved = HexSelfBuild.MoveAside(folder);

            // One the build did write stays the new one
            File.WriteAllText(locked, "new");
            Assert.Equal(0, HexSelfBuild.PutBack(moved));
        }

        Assert.Equal("new", File.ReadAllText(locked));
        File.WriteAllText(Path.Combine(folder, "notes.old"), "not ours");
        Assert.Equal(1, HexSelfBuild.CleanUp(folder));
        Assert.False(File.Exists(locked + ".old"));
        Assert.True(File.Exists(Path.Combine(folder, "notes.old")));
    }

    [Theory]
    [InlineData("dotnet build Hex/Hex.csproj -p:OutDir=\"C:\\out\\\\\"", "build", "Hex/Hex.csproj", "C:\\out")]
    [InlineData("dotnet build", "build", null, null)]
    [InlineData("dotnet run --project Hex/Hex.csproj -- --window", "run", "Hex/Hex.csproj", null)]
    [InlineData("dotnet publish App.sln -o publish", "publish", "App.sln", "publish")]
    [InlineData("dotnet msbuild a.csproj /p:Configuration=Release;OutDir=bin\\x\\", "msbuild", "a.csproj", "bin\\x")]
    public void ACommandLineSaysWhatItBuildsAndWhere(string command, string verb, string? target, string? output)
    {
        string here = Path.GetFullPath(folder);
        DotnetCommandLine line = Assert.Single(DotnetCommandLine.Read(command, here));
        Assert.Equal(verb, line.Verb);
        Assert.Equal(target is null ? null : Path.GetFullPath(Path.Combine(here, target)), line.Target);
        Assert.Equal(output is null ? null : Path.GetFullPath(Path.Combine(here, output)), line.OutputFolder);
    }

    [Fact]
    public void AChainedLineHasEachOfItsBuildsAndAnythingElseIsNone()
    {
        Assert.Equal(2, DotnetCommandLine.Read("cd x && dotnet build a.csproj && dotnet test b.csproj", folder).Count);
        Assert.Empty(DotnetCommandLine.Read("echo dotnet build && dub build", folder));
        Assert.Empty(DotnetCommandLine.Read("dotnet --info", folder));
    }

    [Fact]
    public void TheStartupProjectIsTheProgramThatIsntTests()
    {
        string Make(string name, string body)
        {
            string path = Path.Combine(folder, name, name + ".csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{body}</PropertyGroup></Project>");
            return path;
        }

        string library = Make("Lib", "<OutputType>Library</OutputType>");
        string app = Make("App", "<OutputType>WinExe</OutputType>");
        string tests = Make("App.Tests", "<OutputType>Exe</OutputType><IsTestProject>true</IsTestProject>");
        string tool = Make("Tool", "<OutputType>Exe</OutputType>");

        File.WriteAllText(Path.Combine(folder, "All.sln"), string.Join("\r\n",
            "Microsoft Visual Studio Solution File, Format Version 12.00",
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Lib\", \"Lib\\Lib.csproj\", \"{11111111-1111-1111-1111-111111111111}\"",
            "EndProject",
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"App.Tests\", \"App.Tests\\App.Tests.csproj\", \"{22222222-2222-2222-2222-222222222222}\"",
            "EndProject",
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Tool\", \"Tool\\Tool.csproj\", \"{33333333-3333-3333-3333-333333333333}\"",
            "EndProject",
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"App\", \"App\\App.csproj\", \"{44444444-4444-4444-4444-444444444444}\"",
            "EndProject"));

        string solution = Path.Combine(folder, "All.sln");
        Assert.Equal(solution, DotnetSolutions.BuildTargetIn(folder));
        List<string> projects = DotnetSolutions.ProjectsIn(solution);
        Assert.Equal([library, tests, tool, app], projects);
        Assert.Equal(tool, DotnetSolutions.StartupProject(projects));
        Assert.Equal(app, DotnetSolutions.StartupProject(projects, "App"));

        // An .slnx is read the same, and goes before the .sln
        File.WriteAllText(Path.Combine(folder, "All.slnx"), "<Solution>\n  <Folder Name=\"/src/\">\n    <Project Path=\"App/App.csproj\" />\n  </Folder>\n  <Project Path=\"Lib/Lib.csproj\" />\n</Solution>\n");
        Assert.Equal(Path.Combine(folder, "All.slnx"), DotnetSolutions.BuildTargetIn(folder));
        Assert.Equal([app, library], DotnetSolutions.ProjectsIn(Path.Combine(folder, "All.slnx")));
    }

    [Fact]
    public void TheTasksTemplateBuildsTheSolutionAndRunsItsStartupProject()
    {
        Directory.CreateDirectory(Path.Combine(folder, "src", "App"));
        File.WriteAllText(Path.Combine(folder, "src", "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(folder, "All.slnx"), "<Solution>\n  <Project Path=\"src/App/App.csproj\" />\n</Solution>\n");

        string written = Hex.Dawn.HexTasks.TasksTemplate(folder);
        var runtime = new Horizon.HIDL.HIDLRuntime { Output = null };
        Assert.True(runtime.Evaluate(written).success);
        Assert.Contains("command: \"dotnet build All.slnx\"", written);
        Assert.Contains("command: \"dotnet test All.slnx\"", written);
        Assert.Contains("command: \"dotnet run --project src/App/App.csproj --no-build\"", written);
        Assert.Contains("depends_on: \"build\"", written);
    }

    [Fact]
    public void AProgramIsLaunchedWithItsLaunchSettingsAndHexWithAWindowOfItsOwn()
    {
        string project = Path.Combine(folder, "App.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Assert.Empty(DotnetLaunch.ArgumentsFor(project, Path.Combine(folder, "bin", "App.dll"), Path.Combine(folder, "Hex.exe")));
        Assert.Equal(["--window"], DotnetLaunch.ArgumentsFor(project, Path.Combine(folder, "bin", "Hex.dll"), Path.Combine(folder, "Hex.exe")));

        Directory.CreateDirectory(Path.Combine(folder, "Properties"));
        File.WriteAllText(Path.Combine(folder, "Properties", "launchSettings.json"), "{\n  \"profiles\": {\n    \"web\": { \"commandName\": \"IIS\" },\n    \"App\": { \"commandName\": \"Project\", \"commandLineArgs\": \"--level 3 \\\"a b\\\"\" }\n  }\n}\n");
        Assert.Equal(["--level", "3", "a b"], DotnetLaunch.ArgumentsFor(project, Path.Combine(folder, "bin", "App.dll"), Path.Combine(folder, "Hex.exe")));
        Assert.Equal(["--window", "--level", "3", "a b"], DotnetLaunch.ArgumentsFor(project, Path.Combine(folder, "bin", "Hex.exe"), Path.Combine(folder, "Hex.exe")));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
        }
    }
}
