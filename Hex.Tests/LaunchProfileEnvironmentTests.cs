using System.Text.Json.Nodes;
using Hex.Core.Projects;

namespace Hex.Tests;

public class LaunchProfileEnvironmentTests
{
    [Fact]
    public void ProfileUrlsAndNameProduceRuntimeValuesWithoutChangingStoredSettings()
    {
        var profile = Assert.Single(DotnetLaunch.ProfilesIn("""
            {"profiles":{"Web":{"commandName":"Project","applicationUrl":"http://localhost:5000;https://localhost:5001",
              "environmentVariables":{"MODE":"development"}}}}
            """));
        var environment = profile.RuntimeEnvironment();
        Assert.Equal("http://localhost:5000;https://localhost:5001", environment["ASPNETCORE_URLS"]);
        Assert.Equal("Web", environment["DOTNET_LAUNCH_PROFILE"]);
        Assert.Equal("development", environment["MODE"]);
        Assert.False(profile.Environment.ContainsKey("ASPNETCORE_URLS"));
        Assert.False(profile.Environment.ContainsKey("DOTNET_LAUNCH_PROFILE"));
    }

    [Fact]
    public void ExplicitProfileValuesOverrideGeneratedUrlsAndNameAndExpandInheritedVariables()
    {
        string name = "HEX_PROFILE_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "expanded value");
        try
        {
            var profile = new DotnetLaunchProfile("Web", "", null, new Dictionary<string, string>
            {
                ["ASPNETCORE_URLS"] = "http://localhost:7000", ["DOTNET_LAUNCH_PROFILE"] = "custom", ["EXPANDED"] = "%" + name + "%"
            }) { ApplicationUrl = "http://localhost:5000" };
            var environment = profile.RuntimeEnvironment();
            Assert.Equal("http://localhost:7000", environment["ASPNETCORE_URLS"]);
            Assert.Equal("custom", environment["DOTNET_LAUNCH_PROFILE"]);
            Assert.Equal("expanded value", environment["EXPANDED"]);
            Assert.Equal("%" + name + "%", profile.Environment["EXPANDED"]);
            Assert.Equal("expanded value", (profile with { ApplicationUrl = "%" + name + "%" , Environment = new Dictionary<string, string>() }).RuntimeEnvironment()["ASPNETCORE_URLS"]);
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public void UrlEditsAndClearingPreserveOtherProfilesAndUnknownLaunchSettings()
    {
        const string json = """
            {"$schema":"kept","profiles":{
             "Web":{"commandName":"Project","applicationUrl":"http://localhost:5000","launchBrowser":true,"launchUrl":"swagger","commandLineArgs":"a b","environmentVariables":{"MODE":"dev"}},
             "Other":{"commandName":"IISExpress","applicationUrl":"https://localhost:9000"}}}
            """;
        var profile = Assert.Single(DotnetLaunch.ProfilesIn(json));
        string edited = DotnetLaunch.WithProfile(json, profile with { ApplicationUrl = "http://localhost:6000" });
        var root = JsonNode.Parse(edited)!;
        Assert.Equal("kept", root["$schema"]!.GetValue<string>());
        Assert.True(root["profiles"]!["Web"]!["launchBrowser"]!.GetValue<bool>());
        Assert.Equal("swagger", root["profiles"]!["Web"]!["launchUrl"]!.GetValue<string>());
        Assert.Equal("https://localhost:9000", root["profiles"]!["Other"]!["applicationUrl"]!.GetValue<string>());
        Assert.Equal("http://localhost:6000", Assert.Single(DotnetLaunch.ProfilesIn(edited)).ApplicationUrl);
        string cleared = DotnetLaunch.WithProfile(edited, profile with { ApplicationUrl = "" });
        var read = Assert.Single(DotnetLaunch.ProfilesIn(cleared));
        Assert.Null(read.ApplicationUrl);
        Assert.Equal("a b", read.CommandLine);
        Assert.Equal("dev", read.Environment["MODE"]);
    }
}
