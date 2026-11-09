using Hex.Core.Projects;

namespace Hex.Tests;

public class DotnetRunEnvironmentTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("hex-run-environment-").FullName;
    public DotnetRunEnvironmentTests() => File.WriteAllText(Path.Combine(folder, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Theory]
    [InlineData("-e NAME=value")]
    [InlineData("--environment NAME=value")]
    [InlineData("--environment=NAME=value")]
    [InlineData("--environment:NAME=value")]
    [InlineData("-e=NAME=value")]
    [InlineData("-e:NAME=value")]
    public void NativeEnvironmentOptionFormsAreCaptured(string options)
    {
        var run = DotnetRunLine.Of("dotnet run " + options, folder);
        Assert.NotNull(run);
        Assert.Equal("value", run.Environment["NAME"]);
        Assert.Null(run.Arguments);
    }

    [Fact]
    public void ValuesKeepSpacesEqualsEmptyAndLiteralReferencesAndStopAtTheArgumentSeparator()
    {
        var run = DotnetRunLine.Of("dotnet run -e FIRST=old --environment \"FIRST=a=b c\" -e EMPTY= -e EMPTY_NAME -e LITERAL=%PARENT% -- -e FIRST=application", folder)!;
        Assert.Equal("a=b c", run.Environment["FIRST"]);
        Assert.Equal("", run.Environment["EMPTY"]);
        Assert.Equal("", run.Environment["EMPTY_NAME"]);
        Assert.Equal("%PARENT%", run.Environment["LITERAL"]);
        Assert.Equal(["-e", "FIRST=application"], run.Arguments);
    }

    [Fact]
    public void CommandOverridesWinWithoutChangingTheLaunchProfile()
    {
        var profile = new DotnetLaunchProfile("Web", "profile-args", null, new Dictionary<string, string>
        { ["MODE"] = "profile", ["ONLY_PROFILE"] = "kept", ["ASPNETCORE_URLS"] = "http://localhost:5001" }) { ApplicationUrl = "http://localhost:5000" };
        var run = DotnetRunLine.Of("dotnet run -e MODE=command -e ASPNETCORE_URLS=http://localhost:7000 -e DOTNET_LAUNCH_PROFILE=custom", folder)!;
        var environment = run.RuntimeEnvironment(profile);
        Assert.Equal("command", environment["MODE"]);
        Assert.Equal("http://localhost:7000", environment["ASPNETCORE_URLS"]);
        Assert.Equal("custom", environment["DOTNET_LAUNCH_PROFILE"]);
        Assert.Equal("kept", environment["ONLY_PROFILE"]);
        Assert.Equal("profile", profile.Environment["MODE"]);
        Assert.Equal("http://localhost:5001", profile.Environment["ASPNETCORE_URLS"]);
    }

    [Fact]
    public void NoLaunchProfileKeepsCommandOverridesAndOmitsAllProfileValues()
    {
        var profile = new DotnetLaunchProfile("Web", "", null, new Dictionary<string, string> { ["MODE"] = "profile" }) { ApplicationUrl = "http://localhost:5000" };
        var run = DotnetRunLine.Of("dotnet run --no-launch-profile -e MODE=command", folder)!;
        var environment = run.RuntimeEnvironment(profile);
        Assert.Equal("command", environment["MODE"]);
        Assert.False(environment.ContainsKey("ASPNETCORE_URLS"));
        Assert.False(environment.ContainsKey("DOTNET_LAUNCH_PROFILE"));
    }

    [Theory]
    [InlineData("dotnet run -e")]
    [InlineData("dotnet run --environment")]
    [InlineData("dotnet run -e =bad")]
    [InlineData("dotnet run --environment=")]
    [InlineData("dotnet run -e --project App.csproj")]
    [InlineData("dotnet run -e NAME=value && dotnet test")]
    public void InvalidEnvironmentOptionsOrShellChainsAreNotSilentlyDebugged(string command) => Assert.Null(DotnetRunLine.Of(command, folder));
}
