using Hex.Core.Projects;

namespace Hex.Tests;

public class DotnetDebugTargetTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("hex-framework-options-").FullName;
    public DotnetDebugTargetTests() => File.WriteAllText(Path.Combine(folder, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Theory]
    [InlineData("-f net10.0")]
    [InlineData("--framework net10.0")]
    [InlineData("--framework=net10.0")]
    [InlineData("--framework:net10.0")]
    [InlineData("-f=net10.0")]
    [InlineData("-f:net10.0")]
    public void TaskFrameworkOptionsReachTheBuildContext(string options)
    {
        var run = DotnetRunLine.Of("dotnet run " + options + " -e MODE=selected -- -f application", folder)!;
        Assert.Equal("net10.0", run.Framework);
        Assert.Equal("selected", run.Environment["MODE"]);
        Assert.Equal(["-f", "application"], run.Arguments);
        var build = DotnetDebugTarget.BuildArguments(run.Project, "Release", run.Framework);
        Assert.Equal(["--framework", "net10.0"], build.TakeLast(2));
        Assert.Contains("Release", build);
        var properties = DotnetDebugTarget.PropertyArguments(run.Project, "Release", run.Framework, "TargetPath");
        Assert.Contains("-p:TargetFramework=net10.0", properties);
        Assert.Contains("-p:Configuration=Release", properties);
    }

    [Theory]
    [InlineData("dotnet run -f")]
    [InlineData("dotnet run --framework=")]
    [InlineData("dotnet run -f --project App.csproj")]
    [InlineData("dotnet run --framework \"net10.0;Other=bad\"")]
    public void MalformedFrameworkOptionsAreNotSilentlyIgnored(string command) => Assert.Null(DotnetRunLine.Of(command, folder));

    [Fact]
    public void FrameworkChoicesComeFromEvaluatedPropertiesInDeclarationOrder()
    {
        var evaluation = new DotnetEvaluation();
        evaluation.Properties["TargetFrameworks"] = " net10.0;net11.0;NET10.0; ";
        Assert.Equal(["net10.0", "net11.0"], DotnetDebugTarget.Frameworks(evaluation));
        evaluation.Properties["TargetFramework"] = "net11.0";
        Assert.Equal(["net11.0"], DotnetDebugTarget.Frameworks(evaluation));
    }

    [Fact]
    public void PropertyQueriesKeepValuesInsideTheirOwnMsbuildProperties()
    {
        var arguments = DotnetDebugTarget.PropertyArguments("App.csproj", "Debug;Other=value", "net10.0", "OutDir,TargetPath");
        Assert.Contains("-p:Configuration=Debug%3BOther=value", arguments);
        Assert.Contains("-getProperty:OutDir,TargetPath", arguments);
    }
}
