using Hex.Core.Projects;

namespace Hex.Tests;

public class DotnetTestExecutionTests
{
    private static DotnetEvaluation Target(string framework)
    {
        var target = new DotnetEvaluation { Project = Path.Combine(Path.GetTempPath(), "hex-parallel-context", "Tests.csproj") };
        target.Properties["OutDir"] = "bin\\Debug\\" + framework + "\\";
        target.Properties["TargetPath"] = Path.Combine(target.Folder, "bin", "Debug", framework, "Tests.dll");
        target.Properties["IntermediateOutputPath"] = "obj\\Debug\\" + framework + "\\";
        return target;
    }

    [Fact]
    public void NativeFrameworkOutputsCanOverlapWhenTheirBuildFoldersAreDistinct() =>
        Assert.True(DotnetTests.CanRunFrameworksInParallel([Target("net10.0"), Target("net11.0")]));

    [Theory]
    [InlineData("OutDir")]
    [InlineData("IntermediateOutputPath")]
    public void SharedBuildFoldersRequireSequentialBuildAndTest(string property)
    {
        var first = Target("net10.0");
        var second = Target("net11.0");
        second.Properties[property] = first.Properties[property].Replace('\\', '/');
        Assert.False(DotnetTests.CanRunFrameworksInParallel([first, second]));
    }

    [Theory]
    [InlineData(" false ")]
    [InlineData("FALSE")]
    public void NativeSdkParallelOptOutIsRespected(string value)
    {
        var target = Target("net11.0");
        target.Properties["TestTfmsInParallel"] = value;
        Assert.False(DotnetTests.CanRunFrameworksInParallel([Target("net10.0"), target]));
    }

    [Theory]
    [InlineData("OutDir")]
    [InlineData("TargetPath")]
    [InlineData("IntermediateOutputPath")]
    public void UnknownBuildPathsCannotJustifyParallelExecution(string property)
    {
        var target = Target("net11.0");
        target.Properties.Remove(property);
        Assert.False(DotnetTests.CanRunFrameworksInParallel([Target("net10.0"), target]));
    }
}
