using Hex.Core.Projects;

namespace Hex.Tests;

public class DotnetRuntimeOptionsTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("hex-runtime-options-").FullName;
    public DotnetRuntimeOptionsTests() => File.WriteAllText(Path.Combine(folder, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Theory]
    [InlineData("-r win-x64", "win-x64", null, null)]
    [InlineData("--runtime win-x64", "win-x64", null, null)]
    [InlineData("--runtime=linux-musl-x64", "linux-musl-x64", null, null)]
    [InlineData("--runtime:osx-arm64", "osx-arm64", null, null)]
    [InlineData("-r=win-x64", "win-x64", null, null)]
    [InlineData("-r:win-x64", "win-x64", null, null)]
    [InlineData("-a x64", null, "x64", null)]
    [InlineData("--arch=arm64", null, "arm64", null)]
    [InlineData("--arch:x86", null, "x86", null)]
    [InlineData("-a:x64 --os linux-musl", null, "x64", "linux-musl")]
    [InlineData("--os=win --arch x64", null, "x64", "win")]
    [InlineData("--os:linux", null, null, "linux")]
    public void RuntimeOptionsAreNativeSdkArgumentsInBuildAndPropertyContexts(string options, string? rid, string? arch, string? os)
    {
        var run = DotnetRunLine.Of("dotnet run " + options + " -f net11.0 -e MODE=task -- --runtime application", folder)!;
        Assert.Equal(new DotnetRuntimeOptions(rid, arch, os), run.RuntimeOptions);
        Assert.Equal(new[] { "--runtime", "application" }, run.Arguments);
        Assert.Equal("task", run.Environment["MODE"]);
        var build = DotnetDebugTarget.BuildArguments(run.Project, "Checked", run.Framework, run.RuntimeOptions);
        var query = DotnetDebugTarget.PropertyArguments(run.Project, "Checked", run.Framework, "OutDir,TargetPath", run.RuntimeOptions);
        Assert.Equal("build", query[0]);
        Assert.Contains("--no-restore", query);
        Assert.Contains("-getProperty:OutDir,TargetPath", query);
        Assert.DoesNotContain("--no-restore", build);
        foreach (var (flag, value) in new[] { ("--runtime", rid), ("--arch", arch), ("--os", os) })
        {
            if (value is null) { Assert.DoesNotContain(flag, build); Assert.DoesNotContain(flag, query); }
            else { Assert.Equal(value, build[build.IndexOf(flag) + 1]); Assert.Equal(value, query[query.IndexOf(flag) + 1]); }
        }
    }

    [Theory]
    [InlineData("--self-contained", true)]
    [InlineData("--sc", true)]
    [InlineData("--self-contained true", true)]
    [InlineData("--self-contained=false", false)]
    [InlineData("--sc:false", false)]
    [InlineData("--no-self-contained", false)]
    public void SelfContainedSelectionRemainsPartOfTheRuntimeContext(string options, bool expected)
    {
        var run = DotnetRunLine.Of("dotnet run " + options + " -f net11.0", folder)!;
        Assert.Equal(expected, run.RuntimeOptions.SelfContained);
        var arguments = DotnetDebugTarget.BuildArguments(run.Project, "Debug", run.Framework, run.RuntimeOptions);
        Assert.Equal(expected ? "true" : "false", arguments[arguments.IndexOf("--self-contained") + 1]);
        Assert.Equal("build", DotnetDebugTarget.PropertyArguments(run.Project, "Debug", run.Framework, "TargetPath", run.RuntimeOptions)[0]);
    }

    [Theory]
    [InlineData("-r")]
    [InlineData("--arch=")]
    [InlineData("--os --project App.csproj")]
    [InlineData("--runtime win-x64 -a x64")]
    [InlineData("--runtime win-x64 --os win")]
    [InlineData("--runtime \"win-x64;Other=bad\"")]
    [InlineData("--arch \"x64,Other=bad\"")]
    [InlineData("--self-contained=bogus")]
    [InlineData("--no-self-contained=false")]
    [InlineData("--self-contained --no-self-contained")]
    [InlineData("--no-self-contained --self-contained false")]
    public void MalformedOrConflictingRuntimeSelectionsCannotBeSilentlyDebuggedAsTheDefault(string options) =>
        Assert.Null(DotnetRunLine.Of("dotnet run " + options, folder));

    [Fact]
    public void OrdinaryTasksAndApplicationArgumentsKeepTheirDefaultBuildContext()
    {
        var run = DotnetRunLine.Of("dotnet run -- --arch x64 --self-contained", folder)!;
        Assert.False(run.RuntimeOptions.Any);
        Assert.Equal("msbuild", DotnetDebugTarget.PropertyArguments(run.Project, "Debug", null, "TargetPath", run.RuntimeOptions)[0]);
    }

    [Fact]
    public void SelectedRuntimeUsesTheNativeAppHostRatherThanBypassingItsArchitecture()
    {
        var target = new DotnetEvaluation();
        target.Properties["TargetPath"] = "output/Probe.dll";
        target.Properties["RuntimeIdentifier"] = "win-x86";
        target.Properties["RunCommand"] = "output/Probe.exe";
        var launch = DotnetDebugTarget.LaunchTarget(target);
        Assert.Equal("output/Probe.exe", launch.Program);
        Assert.Empty(launch.PrefixArguments);
    }

    [Fact]
    public void NativeDotnetExecArgumentsStayAheadOfTheApplicationsArguments()
    {
        var target = new DotnetEvaluation();
        target.Properties["TargetPath"] = "output with spaces/Probe.dll";
        target.Properties["RunCommand"] = "dotnet";
        target.Properties["RunArguments"] = "exec \"output with spaces/Probe.dll\"";
        var launch = DotnetDebugTarget.LaunchTarget(target, new(Architecture: "x64"));
        Assert.Equal("dotnet", launch.Program);
        Assert.Equal(new[] { "exec", "output with spaces/Probe.dll" }, launch.PrefixArguments);
    }

    [Fact]
    public void OrdinaryLaunchesAndMissingSdkCommandsKeepTheExistingAssemblyFallback()
    {
        var target = new DotnetEvaluation();
        target.Properties["TargetPath"] = "output/Probe.dll";
        target.Properties["RunCommand"] = "output/Probe.exe";
        Assert.Equal("output/Probe.dll", DotnetDebugTarget.LaunchTarget(target).Program);
        target.Properties.Remove("RunCommand");
        Assert.Equal("output/Probe.dll", DotnetDebugTarget.LaunchTarget(target, new(RuntimeIdentifier: "win-x64")).Program);
    }
}
