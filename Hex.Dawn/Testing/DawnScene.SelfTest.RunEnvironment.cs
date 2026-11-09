using System.Diagnostics;
using System.Text.Json.Nodes;
using Hex.Core.Projects;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestDotnetRunEnvironment(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "dotnet task environment"), project = Path.Combine(root, "Probe.csproj"), file = Path.Combine(root, "Program.cs");
        string result = Path.Combine(root, "env-result.json"), parentKey = "HEX_TASK_PARENT_" + Guid.NewGuid().ToString("N");
        const string source = "using System;\nusing System.IO;\nusing System.Text.Json;\n"
            + "string mode = Environment.GetEnvironmentVariable(\"HEX_TASK_MODE\") ?? \"<unset>\";\n"
            + "string urls = Environment.GetEnvironmentVariable(\"ASPNETCORE_URLS\") ?? \"<unset>\";\n"
            + "string profile = Environment.GetEnvironmentVariable(\"DOTNET_LAUNCH_PROFILE\") ?? \"<unset>\";\n"
            + "string extra = Environment.GetEnvironmentVariable(\"HEX_TASK_EXTRA\") ?? \"<unset>\";\n"
            + "string empty = Environment.GetEnvironmentVariable(\"HEX_TASK_EMPTY\") ?? \"<unset>\";\n"
            + "string literal = Environment.GetEnvironmentVariable(\"HEX_TASK_LITERAL\") ?? \"<unset>\";\n"
            + "File.WriteAllText(\"env-result.json\", JsonSerializer.Serialize(new { mode, urls, profile, extra, empty, literal, arguments = args }));\nConsole.WriteLine(\"task-environment-finished\");\n";
        string[] commands =
        [
            "dotnet run --project Probe.csproj --verbosity quiet --launch-profile Web -e HEX_TASK_MODE=command --environment=ASPNETCORE_URLS=http://localhost:7200 --environment:\"HEX_TASK_EXTRA=a=b c\" -e HEX_TASK_EMPTY -e HEX_TASK_LITERAL=%" + parentKey + "% -e DOTNET_LAUNCH_PROFILE=command-profile -- \"two words\" -e app=argument",
            "dotnet run --verbosity quiet --no-launch-profile -e HEX_TASK_MODE=unprofiled --environment HEX_TASK_EXTRA=first -e:HEX_TASK_EXTRA=second -e=HEX_TASK_EMPTY= -- --environment application=argument"
        ];
        Task<string>? sdk = null;
        string baseline = string.Empty, expectedMode = string.Empty;
        var inherited = new Dictionary<string, string?>();
        bool HasMode() => expectedMode.Length > 0 && sideTree.Items.FirstOrDefault(section => section.Text == Said("debugging.variables"))?.Children?
            .Any(row => row.Text.StartsWith("mode = ", StringComparison.Ordinal) && row.Text.Contains(expectedMode, StringComparison.Ordinal)) == true;
        async Task<string> RunSdk(string command)
        {
            var info = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in DotnetCommandLine.Words(command).Skip(1)) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            ChildProcesses.Adopt(process);
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string stdout = await output, stderr = await errors;
            if (process.ExitCode != 0) throw new InvalidOperationException(stdout + stderr);
            return File.ReadAllText(result);
        }
        test.Run(() =>
        {
            Directory.CreateDirectory(Path.Combine(root, "Properties"));
            File.WriteAllText(project, CSHARP_PROJECT);
            File.WriteAllText(file, source);
            File.WriteAllText(DotnetLaunch.SettingsOf(project), """{"profiles":{"Web":{"commandName":"Project","applicationUrl":"http://localhost:5000","environmentVariables":{"HEX_TASK_MODE":"profile","HEX_TASK_EXTRA":"profile extra","HEX_TASK_EMPTY":"profile empty"}}}}""");
            foreach (var pair in new Dictionary<string, string> { ["HEX_TASK_MODE"] = "ambient", ["HEX_TASK_EXTRA"] = "ambient extra",
                ["HEX_TASK_EMPTY"] = "ambient empty", ["HEX_TASK_LITERAL"] = "ambient literal", ["ASPNETCORE_URLS"] = "http://localhost:6000",
                ["DOTNET_LAUNCH_PROFILE"] = "ambient-profile", [parentKey] = "must remain literal in CLI values" })
            {
                inherited[pair.Key] = System.Environment.GetEnvironmentVariable(pair.Key);
                System.Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
            explorer.OpenFolder(root);
            Open(file);
        });
        foreach (string command in commands)
        {
            bool noProfile = command.Contains("--no-launch-profile", StringComparison.Ordinal);
            string label = noProfile ? "without a launch profile" : "with profile overrides";
            test.Run(() => sdk = RunSdk(command));
            test.WaitFor(() => sdk?.IsCompleted == true, 60);
            test.Check("Task environment: native SDK runs " + label, () => sdk?.IsCompletedSuccessfully == true);
            test.Run(() =>
            {
                baseline = sdk?.IsCompletedSuccessfully == true ? sdk.Result : string.Empty;
                expectedMode = baseline.Length > 0 ? JsonNode.Parse(baseline)!["mode"]!.GetValue<string>() : string.Empty;
                Console.WriteLine("[Hex self-test] SDK task environment " + label + ": " + baseline);
                if (File.Exists(result)) File.Delete(result);
                debugger.breakpoints.Clear();
                debugger.ToggleBreakpoint(file, 3);
                var run = DotnetRunLine.Of(command, root);
                if (run is not null) debugger.DebugRun(run, root, go => go());
            });
            test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == 3, 60);
            test.Check("Task environment: parsed task pauses under native C# debugging " + label, () => debugger.DebugStopped && code.ExecutionLine == 3);
            test.Run(debugger.StepOver);
            test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == 4 && HasMode(), 10);
            test.Check("Task environment: command-line value appears in native locals " + label, HasMode);
            test.Run(RunOrPlay);
            test.WaitFor(() => !debugger.Debugging && File.Exists(result), 20);
            test.Check("Task environment: debugger matches SDK environment and application arguments " + label, () => !debugger.Debugging
                && baseline.Length > 0 && File.Exists(result) && File.ReadAllText(result) == baseline);
            test.Check("Task environment: running a task preserves profile JSON and source " + label, () => File.ReadAllText(file) == source
                && DotnetLaunch.For(project, "Web")?.Environment.GetValueOrDefault("HEX_TASK_MODE") == "profile");
        }
        test.Run(() =>
        {
            if (debugger.Debugging) debugger.StopDebugging();
            debugger.breakpoints.Clear();
            foreach (var (name, value) in inherited) System.Environment.SetEnvironmentVariable(name, value);
        });
    }
}
