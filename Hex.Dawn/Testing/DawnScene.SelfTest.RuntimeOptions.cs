using System.Diagnostics;
using System.Text.Json.Nodes;
using Hex.Core.Projects;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestDotnetRuntimeOptions(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "dotnet runtime options"), project = Path.Combine(root, "Probe.csproj"), file = Path.Combine(root, "Program.cs");
        string result = Path.Combine(root, "runtime-result.json"), sentinel = Path.Combine(root, "query-only");
        string rid = string.Empty, os = string.Empty, arch = string.Empty, baseline = string.Empty;
        bool useAppHost = true;
        Task<string>? sdk = null;
        Task<(int Code, string Output)>? query = null;
        const string source = "using System;\nusing System.IO;\nusing System.Linq;\nusing System.Reflection;\nusing System.Text.Json;\n"
            + "var metadata = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().OrderBy(item => item.Key).ToDictionary(item => item.Key, item => item.Value);\n"
            + "string rid = metadata[\"rid\"] ?? \"\";\n"
            + "int answer = 42;\n"
            + "File.WriteAllText(\"runtime-result.json\", JsonSerializer.Serialize(new { metadata, args, location = Assembly.GetExecutingAssembly().Location, architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), process = Environment.ProcessPath }));\n"
            + "Console.WriteLine(answer);\n";
        string ProjectText() => """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFrameworks>net10.0;net11.0</TargetFrameworks><RuntimeIdentifier>hex-default-must-be-overridden</RuntimeIdentifier><SelfContained>true</SelfContained><UseAppHost>USE_HOST</UseAppHost><OutputPath>selected-output/$(Configuration)/</OutputPath></PropertyGroup>
              <Target Name="GuardQueries" BeforeTargets="Build;CoreCompile;Restore"><Error Condition="Exists('query-only')" Text="A property query must not build or restore."/></Target>
              <Target Name="GuardRuntime" BeforeTargets="CoreCompile"><Error Condition="'$(RuntimeIdentifier)' != 'EXPECTED_RID' Or '$(TargetFramework)' != 'net11.0'" Text="The selected runtime or framework was ignored."/></Target>
              <Target Name="RuntimeMetadata" BeforeTargets="GetAssemblyAttributes">
                <ItemGroup>
                  <AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute"><_Parameter1>rid</_Parameter1><_Parameter2>$(RuntimeIdentifier)</_Parameter2></AssemblyAttribute>
                  <AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute"><_Parameter1>cli_rid</_Parameter1><_Parameter2>$(_CommandLineDefinedRuntimeIdentifier)</_Parameter2></AssemblyAttribute>
                  <AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute"><_Parameter1>self_contained</_Parameter1><_Parameter2>$(SelfContained)</_Parameter2></AssemblyAttribute>
                </ItemGroup>
              </Target>
            </Project>
            """.Replace("EXPECTED_RID", rid, StringComparison.Ordinal).Replace("USE_HOST", useAppHost ? "true" : "false", StringComparison.Ordinal);
        async Task<string> RunSdk(string command)
        {
            var info = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
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
        bool HasRid() => rid.Length > 0 && sideTree.Items.FirstOrDefault(section => section.Text == Said("debugging.variables"))?.Children?
            .Any(row => row.Text.StartsWith("rid = ", StringComparison.Ordinal) && row.Text.Contains(rid, StringComparison.Ordinal)) == true;
        bool MatchesSdk()
        {
            if (baseline.Length == 0 || !File.Exists(result)) return false;
            string actual = File.ReadAllText(result);
            var expected = JsonNode.Parse(baseline)!;
            var observed = JsonNode.Parse(actual)!;
            if (expected["process"]?.GetValue<string>() is {} expectedProcess && observed["process"]?.GetValue<string>() is {} actualProcess
                && PlatformPaths.Same(expectedProcess, actualProcess)) observed["process"] = expectedProcess;
            bool matched = JsonNode.DeepEquals(expected, observed);
            if (!matched) Console.WriteLine("[Hex self-test] debugger runtime mismatch: " + actual);
            return matched;
        }
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            WriteSelfTestProject(project, ProjectText());
            File.WriteAllText(file, source);
            File.WriteAllText(sentinel, "evaluation only");
            query = DotnetCli.Run(root, DotnetDebugTarget.PropertyArguments(project, "Checked", null, "NETCoreSdkRuntimeIdentifier,TargetFrameworks"), TimeSpan.FromMinutes(2), CancellationToken.None);
            explorer.OpenFolder(root);
            Open(file);
        });
        test.WaitFor(() => query?.IsCompleted == true, 30);
        test.Check("Runtime: native SDK host context is available without a build or restore", () => query?.IsCompletedSuccessfully == true && query.Result.Code == 0);
        test.Run(() =>
        {
            if (query?.IsCompletedSuccessfully != true || query.Result.Code != 0) return;
            rid = DotnetEvaluation.Parse(project, query.Result.Output).Properties.GetValueOrDefault("NETCoreSdkRuntimeIdentifier") ?? string.Empty;
            int dash = rid.LastIndexOf('-');
            if (dash > 0) { os = rid[..dash]; arch = rid[(dash + 1)..]; }
            WriteSelfTestProject(project, ProjectText());
            debugger.breakpoints.Clear();
            debugger.ToggleBreakpoint(file, 8);
        });
        foreach (string kind in new[] { "runtime", "arch", "os", "self-contained", "no-apphost" })
        {
            string command = string.Empty;
            test.Run(() =>
            {
                useAppHost = kind != "no-apphost";
                WriteSelfTestProject(project, ProjectText());
                string options = kind switch
                {
                    "runtime" => "--runtime=" + rid + " --no-self-contained",
                    "arch" => "--arch " + arch + " --os " + os + " --self-contained=false",
                    "os" => "--os:" + os + " --sc:false",
                    "no-apphost" => "--runtime " + rid + " --self-contained false",
                    _ => "-r " + rid + " --self-contained"
                };
                command = "dotnet run " + options + " -c Checked -f net11.0 --no-launch-profile -- \"two words\" --runtime application --arch argument";
                var run = DotnetRunLine.Of(command, root)!;
                query = DotnetCli.Run(root, DotnetDebugTarget.PropertyArguments(project, "Checked", "net11.0", "RuntimeIdentifier,TargetPath,SelfContained,_CommandLineDefinedRuntimeIdentifier", run.RuntimeOptions), TimeSpan.FromMinutes(2), CancellationToken.None);
            });
            test.WaitFor(() => query?.IsCompleted == true, 30);
            test.Check("Runtime: " + kind + " property query preserves native SDK context without executing targets", () => query?.IsCompletedSuccessfully == true && query.Result.Code == 0
                && DotnetEvaluation.Parse(project, query.Result.Output).Properties.GetValueOrDefault("RuntimeIdentifier") == rid);
            test.Run(() => { File.Delete(sentinel); sdk = RunSdk(command); });
            test.WaitFor(() => sdk?.IsCompleted == true, 120);
            test.Check("Runtime: native SDK runs " + kind + " selection", () => sdk?.IsCompletedSuccessfully == true);
            test.Run(() =>
            {
                if (sdk?.IsFaulted == true) Console.WriteLine("[Hex self-test] runtime SDK failure: " + sdk.Exception);
                baseline = sdk?.IsCompletedSuccessfully == true ? sdk.Result : string.Empty;
                Console.WriteLine("[Hex self-test] SDK runtime " + kind + ": " + baseline);
                File.Delete(result);
                if (DotnetRunLine.Of(command, root) is {} run) debugger.DebugRun(run, root, go => go());
            });
            test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == 8 && HasRid(), 90);
            test.Check("Runtime: " + kind + " task builds and pauses with native runtime locals", () => debugger.DebugStopped && code.ExecutionLine == 8 && HasRid());
            if (kind == "runtime")
            {
                test.Run(debugger.RestartDebugging);
                test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == 8 && HasRid(), 90);
                test.Check("Runtime: Restart retains the explicit native runtime context", () => debugger.DebugStopped && HasRid());
            }
            test.Run(RunOrPlay);
            test.WaitFor(() => !debugger.Debugging && File.Exists(result), 30);
            test.Check("Runtime: " + kind + " debugger matches SDK metadata, target path, architecture and application arguments", () => MatchesSdk()
                && JsonNode.Parse(baseline)?["location"]?.GetValue<string>().Contains(rid, StringComparison.Ordinal) == true);
            test.Run(() => File.WriteAllText(sentinel, "evaluation only"));
        }
        string automatic = string.Empty;
        test.Run(() =>
        {
            automatic = "dotnet run --arch " + arch + " --os " + os + " --no-self-contained -c Checked --no-launch-profile";
            File.Delete(sentinel);
            sdk = RunSdk(automatic + " -f net11.0 -- picker-argument");
        });
        test.WaitFor(() => sdk?.IsCompleted == true, 120);
        test.Check("Runtime: native SDK baseline for the framework picker succeeds", () => sdk?.IsCompletedSuccessfully == true);
        test.Run(() =>
        {
            baseline = sdk?.IsCompletedSuccessfully == true ? sdk.Result : string.Empty;
            File.Delete(result);
            File.WriteAllText(sentinel, "chooser must only query");
            if (DotnetRunLine.Of(automatic + " -- picker-argument", root) is {} run) debugger.DebugRun(run, root, go => go());
        });
        test.WaitFor(() => textDialog is { IsOpen: true }, 30);
        test.Check("Runtime: architecture-aware framework discovery does not build or restore", () => textDialog is { IsOpen: true } dialog
            && Descendants(dialog.Body).OfType<ListBox>().Any(list => list.Items.SequenceEqual(new[] { "net10.0", "net11.0" })));
        test.Run(() =>
        {
            File.Delete(sentinel);
            if (textDialog is not { IsOpen: true } dialog) return;
            var box = Descendants(dialog.Body).OfType<TextBox>().First();
            box.Text = "net11.0";
            box.OnChanged?.Invoke("net11.0");
            box.OnSubmitted?.Invoke("net11.0");
        });
        test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == 8 && HasRid(), 90);
        test.Check("Runtime: choosing a framework retains every native runtime option", () => debugger.DebugStopped && HasRid());
        test.Run(() => { if (debugger.DebugStopped) RunOrPlay(); });
        test.WaitFor(() => !debugger.Debugging && File.Exists(result), 30);
        test.Check("Runtime: framework chooser launch matches the SDK baseline", MatchesSdk);
        test.Run(() =>
        {
            File.Delete(result);
            if (DotnetRunLine.Of(automatic + " -- picker-argument", root) is {} run) debugger.DebugRun(run, root, go => go());
        });
        test.WaitFor(() => debugger.DebugStopped && HasRid(), 90);
        test.Check("Runtime: repeated task launch remembers this runtime's framework", () => debugger.DebugStopped && HasRid() && textDialog is not { IsOpen: true });
        test.Run(debugger.StopDebugging);
        test.WaitFor(() => !debugger.Debugging, 10);
        test.Run(() =>
        {
            if (DotnetRunLine.Of("dotnet run -r " + rid + " --no-self-contained -c Checked --no-launch-profile", root) is {} run)
                debugger.DebugRun(run, root, go => go());
        });
        test.WaitFor(() => textDialog is { IsOpen: true }, 30);
        test.Check("Runtime: another runtime option context does not inherit the previous framework choice", () => textDialog is { IsOpen: true });
        PressKeys(test, "Escape");
        test.WaitFor(() => !debugger.Debugging && textDialog is not { IsOpen: true }, 5);
        test.Run(() =>
        {
            File.Delete(result);
            if (DotnetRunLine.Of("dotnet run -r hex-not-a-runtime --no-self-contained -f net11.0 -c Checked --no-launch-profile", root) is {} run)
                debugger.DebugRun(run, root, go => go());
        });
        test.WaitFor(() => !debugger.Debugging, 60);
        test.Check("Runtime: an invalid RID cannot launch an existing binary from another runtime", () => !debugger.Debugging && !File.Exists(result));
        test.Check("Runtime: debugging preserves source and project runtime settings", () => File.ReadAllText(file) == source && File.ReadAllText(project) == ProjectText());
        test.Run(() => { File.Delete(sentinel); debugger.breakpoints.Clear(); if (debugger.Debugging) debugger.StopDebugging(); });
    }
}
