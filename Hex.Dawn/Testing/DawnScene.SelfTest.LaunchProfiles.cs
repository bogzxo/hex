using System.Diagnostics;
using System.Text.Json.Nodes;
using Hex.Core.Projects;
using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestLaunchProfileEnvironment(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "launch profile environment"), project = Path.Combine(root, "Probe.csproj"), file = Path.Combine(root, "Program.cs");
        string result = Path.Combine(root, "launch-result.json"), settingsFile = DotnetLaunch.SettingsOf(project);
        string inherited = "HEX_LAUNCH_INHERITED_" + Guid.NewGuid().ToString("N");
        const string webUrls = "http://localhost:5211;https://localhost:7211";
        string source = "using System;\nusing System.IO;\nusing System.Text.Json;\n"
            + "string urls = Environment.GetEnvironmentVariable(\"ASPNETCORE_URLS\") ?? \"\";\n"
            + "string profile = Environment.GetEnvironmentVariable(\"DOTNET_LAUNCH_PROFILE\") ?? \"\";\n"
            + "string mode = Environment.GetEnvironmentVariable(\"HEX_LAUNCH_MODE\") ?? \"\";\n"
            + "string expanded = Environment.GetEnvironmentVariable(\"HEX_LAUNCH_EXPANDED\") ?? \"\";\n"
            + "File.WriteAllText(\"launch-result.json\", JsonSerializer.Serialize(new { urls, profile, mode, expanded, arguments = args }));\n"
            + "Console.WriteLine(\"launch-profile-result \" + profile);\n";
        Task<string>? sdk = null;
        string baseline = string.Empty, expectedUrls = string.Empty;
        bool UrlLocalMatches() => expectedUrls.Length > 0 && sideTree.Items
            .FirstOrDefault(section => section.Text == Said("debugging.variables"))?.Children?
            .Any(row => row.Text.StartsWith("urls = ", StringComparison.Ordinal) && row.Text.Contains(expectedUrls, StringComparison.Ordinal)) == true;
        async Task<string> RunSdk(string profile)
        {
            var info = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "run", "--project", project, "--launch-profile", profile, "--verbosity", "quiet" }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            ChildProcesses.Adopt(process);
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string stdout = await output, stderr = await error;
            if (process.ExitCode != 0) throw new InvalidOperationException(stdout + stderr);
            return File.ReadAllText(result);
        }
        test.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
            System.Environment.SetEnvironmentVariable(inherited, "expanded native value");
            File.WriteAllText(project, CSHARP_PROJECT);
            File.WriteAllText(file, source);
            var profiles = new JsonObject
            {
                ["Web"] = new JsonObject { ["commandName"] = "Project", ["applicationUrl"] = "http://localhost:5000", ["commandLineArgs"] = "--mode \"two words\"", ["launchBrowser"] = true,
                    ["environmentVariables"] = new JsonObject { ["HEX_LAUNCH_MODE"] = "web", ["HEX_LAUNCH_EXPANDED"] = "%" + inherited + "%" } },
                ["Override"] = new JsonObject { ["commandName"] = "Project", ["applicationUrl"] = "http://localhost:6000", ["commandLineArgs"] = "--mode override",
                    ["environmentVariables"] = new JsonObject { ["ASPNETCORE_URLS"] = "http://localhost:7999", ["DOTNET_LAUNCH_PROFILE"] = "custom-profile", ["HEX_LAUNCH_MODE"] = "override" } }
            };
            File.WriteAllText(settingsFile, new JsonObject { ["profiles"] = profiles }.ToJsonString());
            explorer.OpenFolder(root);
            explorer.workspace.LaunchProfiles[project] = "Web";
            Open(file);
        });
        test.Settle(0.3f);
        test.Run(() => ShowPropertiesOf([project]));
        test.WaitFor(() => properties.ShowsProjectFile && !properties.ProjectBusy && properties.Page.ValueText("application_urls") == "http://localhost:5000", 60);
        test.Check("Launch profiles: Application URLs appears in Project Properties", () => properties.ShowsProjectFile
            && properties.Page.ValueText("application_urls") == "http://localhost:5000");
        test.Run(() => properties.Page.Enter("application_urls", new StringValue(webUrls)));
        test.Check("Launch profiles: URL editing preserves other profiles and launch settings", () => DotnetLaunch.For(project, "Web")?.ApplicationUrl == webUrls
            && DotnetLaunch.For(project, "Override")?.ApplicationUrl == "http://localhost:6000"
            && JsonNode.Parse(File.ReadAllText(settingsFile))?["profiles"]?["Web"]?["launchBrowser"]?.GetValue<bool>() == true);
        foreach (string selected in new[] { "Web", "Override" })
        {
            test.Run(() => { sdk = RunSdk(selected); });
            test.WaitFor(() => sdk?.IsCompleted == true, 60);
            test.Check("Launch profiles: native SDK launches " + selected + " successfully", () => sdk?.IsCompletedSuccessfully == true);
            test.Run(() =>
            {
                baseline = sdk?.IsCompletedSuccessfully == true ? sdk.Result : string.Empty;
                expectedUrls = baseline.Length > 0 ? JsonNode.Parse(baseline)!["urls"]!.GetValue<string>() : string.Empty;
                if (baseline.Length > 0) Console.WriteLine("[Hex self-test] SDK launch profile " + selected + ": " + baseline);
                if (File.Exists(result)) File.Delete(result);
                explorer.workspace.LaunchProfiles[project] = selected;
                Open(file);
                debugger.breakpoints.Clear();
                debugger.ToggleBreakpoint(file, 3);
                RunOrPlay();
            });
            test.WaitFor(() => debugger.DebugStopped && document.Path == file && code.ExecutionLine == 3, 60);
            test.Check("Launch profiles: F5 pauses " + selected + " under the native C# debugger", () => debugger.DebugStopped && document.Path == file && code.ExecutionLine == 3);
            test.Run(debugger.StepOver);
            test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == 4 && UrlLocalMatches(), 10);
            test.Check("Launch profiles: " + selected + " URL is available in native locals", UrlLocalMatches);
            test.Run(RunOrPlay);
            test.WaitFor(() => !debugger.Debugging && File.Exists(result), 20);
            test.Check("Launch profiles: F5 matches SDK URLs, profile, arguments and environment for " + selected, () => !debugger.Debugging
                && baseline.Length > 0 && File.Exists(result) && File.ReadAllText(result) == baseline);
        }
        test.Run(() =>
        {
            explorer.workspace.LaunchProfiles[project] = "Web";
            ShowPropertiesOf([project]);
        });
        test.WaitFor(() => properties.ShowsProjectFile && !properties.ProjectBusy && properties.Page.ValueText("application_urls") == webUrls, 60);
        test.Run(() => properties.Page.Enter("application_urls", new StringValue("")));
        test.Check("Launch profiles: clearing Application URLs removes only the selected profile URL", () => DotnetLaunch.For(project, "Web")?.ApplicationUrl is null
            && DotnetLaunch.For(project, "Override")?.ApplicationUrl == "http://localhost:6000"
            && DotnetLaunch.For(project, "Web")?.Environment.GetValueOrDefault("HEX_LAUNCH_MODE") == "web");
        test.Run(() => { if (debugger.Debugging) debugger.StopDebugging(); debugger.breakpoints.Clear(); System.Environment.SetEnvironmentVariable(inherited, null); });
    }

    private void TestAspNetLaunchProfile(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "aspnet launch profile"), project = Path.Combine(root, "WebProbe.csproj"), file = Path.Combine(root, "Program.cs");
        string addressFile = Path.Combine(root, "web-address.txt");
        const string source = "using System.IO;\nusing System.Linq;\nusing Microsoft.AspNetCore.Builder;\nusing Microsoft.AspNetCore.Hosting.Server;\nusing Microsoft.AspNetCore.Hosting.Server.Features;\nusing Microsoft.Extensions.DependencyInjection;\nusing Microsoft.Extensions.Hosting;\n"
            + "var app = WebApplication.CreateBuilder(args).Build();\napp.MapGet(\"/\", () => \"Hex launch profile response\");\nawait app.StartAsync();\n"
            + "var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;\nFile.WriteAllText(\"web-address.txt\", addresses.Addresses.First());\nawait app.WaitForShutdownAsync();\n";
        Task<string>? response = null;
        string endpoint = string.Empty;
        async Task<string> Request()
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            return await client.GetStringAsync(endpoint);
        }
        test.Run(() =>
        {
            Directory.CreateDirectory(Path.Combine(root, "Properties"));
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup></Project>");
            File.WriteAllText(file, source);
            File.WriteAllText(DotnetLaunch.SettingsOf(project), """{"profiles":{"Web":{"commandName":"Project","applicationUrl":"http://127.0.0.1:0","environmentVariables":{"ASPNETCORE_ENVIRONMENT":"Development"}}}}""");
            explorer.OpenFolder(root);
            Open(file);
            debugger.breakpoints.Clear();
            RunOrPlay();
        });
        test.WaitFor(() => File.Exists(addressFile), 60);
        test.Check("Launch profiles: a real ASP.NET Core project starts under F5", () => debugger.Debugging && File.Exists(addressFile));
        test.Run(() =>
        {
            if (!File.Exists(addressFile)) return;
            endpoint = File.ReadAllText(addressFile);
            response = Request();
        });
        test.WaitFor(() => response?.IsCompleted == true, 10);
        test.Check("Launch profiles: Kestrel uses the profile's loopback address and dynamic port", () => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            && uri.Host == "127.0.0.1" && uri.Port > 0 && uri.Port != 5000);
        test.Check("Launch profiles: the debugged web endpoint serves HTTP requests", () => response?.IsCompletedSuccessfully == true && response.Result == "Hex launch profile response");
        test.Run(debugger.StopDebugging);
        test.WaitFor(() => !debugger.Debugging, 15);
        test.Check("Launch profiles: stopping the web debug session preserves its project and source", () => !debugger.Debugging && File.ReadAllText(file) == source
            && DotnetLaunch.For(project, "Web")?.ApplicationUrl == "http://127.0.0.1:0");
    }
}
