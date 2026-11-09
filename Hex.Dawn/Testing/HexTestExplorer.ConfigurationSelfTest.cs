using Hex.Core.Projects;

namespace Hex.Dawn;

internal sealed partial class HexTestExplorer
{
    internal void TestConfiguration(HexPointerScript script, string folder, Action<string> configure)
    {
        string project = Path.Combine(folder, "Configuration.csproj");
        string previous = context.Workspace.Configuration;
        script.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net11.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.10.1" />
                    <PackageReference Include="xunit" Version="2.9.3" />
                    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(folder, "Checks.cs"), """
                public class ConfigurationChecks
                {
                #if DEBUG
                    [Xunit.Fact] public void DebugOnly() { }
                #else
                    [Xunit.Fact] public void ReleaseOnly() { }
                #endif
                }
                """);
            configure("Release");
            tests[project] = DotnetTests.Discover(project);
            Run(project, null, "Release configuration");
        });
        script.WaitFor(() => !running, 120f);
        script.Check("test configuration: Release runs only Release-compiled tests", () =>
            results.TryGetValue(Identity(project, "ConfigurationChecks.ReleaseOnly"), out var release)
            && release.Values.Single().Outcome == DotnetTestOutcome.Passed
            && !results.ContainsKey(Identity(project, "ConfigurationChecks.DebugOnly")));
        script.Run(() =>
        {
            configure("Debug");
            tests[project] = DotnetTests.Discover(project);
        });
        script.Check("test configuration: switching clears results and durations without test-side cleanup", () => results.Count == 0 && waiting.Count == 0);
        script.Run(() =>
        {
            Run(project, null, "Debug configuration");
        });
        script.WaitFor(() => !running, 120f);
        script.Check("test configuration: Debug runs only Debug-compiled tests", () =>
            results.TryGetValue(Identity(project, "ConfigurationChecks.DebugOnly"), out var debug)
            && debug.Values.Single().Outcome == DotnetTestOutcome.Passed
            && !results.ContainsKey(Identity(project, "ConfigurationChecks.ReleaseOnly")));
        script.Run(() =>
        {
            File.WriteAllText(Path.Combine(folder, "Checks.cs"), "public class ConfigurationChecks { this does not compile }");
            Run(project, ["ConfigurationChecks.DebugOnly"], "Failed build after a passing test");
        });
        script.WaitFor(() => !running, 120f);
        script.Check("test configuration: failed build invalidates the selected test's old pass", () =>
            !results.ContainsKey(Identity(project, "ConfigurationChecks.DebugOnly")) && waiting.Count == 0);
        script.Run(() =>
        {
            File.WriteAllText(Path.Combine(folder, "Checks.cs"), "public class ConfigurationChecks { [Xunit.Fact] public void DebugOnly() { } }");
            Run(project, ["ConfigurationChecks.DebugOnly"], "Repaired build");
        });
        script.WaitFor(() => !running, 120f);
        script.Check("test configuration: repaired test reports a fresh pass", () =>
            results.TryGetValue(Identity(project, "ConfigurationChecks.DebugOnly"), out var repaired)
            && repaired.Values.Single().Outcome == DotnetTestOutcome.Passed);
        script.Run(() =>
        {
            // Keep the same configuration and existing result: a source change can
            // remove a test without a configuration switch doing the cleanup for us.
            File.WriteAllText(Path.Combine(folder, "Checks.cs"), "public class ConfigurationChecks { }");
            Run(project, ["ConfigurationChecks.DebugOnly"], "Removed test in the same configuration");
        });
        script.WaitFor(() => !running, 120f);
        script.Check("test configuration: zero matching tests never retain a previous pass", () =>
            !results.ContainsKey(Identity(project, "ConfigurationChecks.DebugOnly")));
        script.Run(() => configure(previous));
    }
}
