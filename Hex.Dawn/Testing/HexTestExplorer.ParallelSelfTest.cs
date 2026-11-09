using System.Diagnostics;
using Hex.Core.Projects;

namespace Hex.Dawn;

internal sealed partial class HexTestExplorer
{
    internal void TestParallelFrameworks(HexPointerScript script, string root, Action<string, string> open, Action<string> replace)
    {
        string project = Path.Combine(root, "Parallel.csproj"), source = Path.Combine(root, "Checks.cs"), control = Path.Combine(root, "control.txt");
        string session = string.Empty;
        DotnetExecution? execution = null;
        Process[] hosts = [];
        int previousLimit = parallelTestHosts;
        static string Literal(string path) => "@\"" + path.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        const string projectText = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFrameworks>net10.0;net11.0</TargetFrameworks><IsTestProject>true</IsTestProject>EXTRA</PropertyGroup>
              <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.10.1"/><PackageReference Include="xunit" Version="2.9.3"/><PackageReference Include="xunit.runner.visualstudio" Version="3.1.5"/></ItemGroup>
              <Target Name="TraceBuild" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' != 'true'"><WriteLinesToFile File="build.trace" Lines="$(TargetFramework)" Overwrite="false"/></Target>
              <Target Name="HoldBuild" BeforeTargets="CoreCompile" Condition="'$(HoldPreparation)' == 'true' and '$(DesignTimeBuild)' != 'true'">
                <WriteLinesToFile File="build-entered" Lines="$(TargetFramework)" Overwrite="true"/>
                <Exec Command="HOLD_COMMAND"/>
                <WriteLinesToFile File="build-finished" Lines="$(TargetFramework)" Overwrite="true"/>
              </Target>
              <Target Name="ChangeOutputPolicy" BeforeTargets="CoreCompile" Condition="'$(ChangeOutputsDuringBuild)' == 'true' and '$(DesignTimeBuild)' != 'true'">
                <WriteLinesToFile File="Directory.Build.props" Lines="&lt;Project&gt;&lt;PropertyGroup&gt;&lt;AppendTargetFrameworkToOutputPath&gt;false&lt;/AppendTargetFrameworkToOutputPath&gt;&lt;/PropertyGroup&gt;&lt;/Project&gt;" Overwrite="true"/>
              </Target>
            </Project>
            """;
        string text = $$"""
            using System;
            using System.IO;
            using System.Reflection;
            using System.Runtime.Versioning;
            using System.Threading;
            public class ParallelChecks
            {
                [Xunit.Fact] public void Overlap()
                {
                    string framework = Assembly.GetExecutingAssembly().GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName.Contains("v10.0") ? "net10.0" : "net11.0";
                    string[] control = File.ReadAllLines({{Literal(control)}});
                    string mode = control[0], root = control[1], other = framework == "net10.0" ? "net11.0" : "net10.0";
                    File.WriteAllText(Path.Combine(root, framework + ".started"), Environment.ProcessId + "|" + DateTime.UtcNow.Ticks);
                    var limit = DateTime.UtcNow.AddSeconds(15);
                    if (mode == "overlap")
                    {
                        while (!File.Exists(Path.Combine(root, other + ".started")) && DateTime.UtcNow < limit) Thread.Sleep(10);
                        Xunit.Assert.True(File.Exists(Path.Combine(root, other + ".started")), "Framework hosts did not overlap");
                        File.WriteAllText(Path.Combine(root, framework + ".observed"), "saw other host");
                        while (!File.Exists(Path.Combine(root, other + ".observed")) && DateTime.UtcNow < limit) Thread.Sleep(10);
                        Xunit.Assert.True(File.Exists(Path.Combine(root, other + ".observed")), "Other host did not reach the barrier");
                        Thread.Sleep(500);
                    }
                    else if (mode == "hold")
                    {
                        while (!File.Exists(Path.Combine(root, "release")) && DateTime.UtcNow < limit) Thread.Sleep(25);
                        Xunit.Assert.True(File.Exists(Path.Combine(root, "release")), "Cancellation did not terminate the held host");
                    }
                    else Thread.Sleep(300);
                    File.WriteAllText(Path.Combine(root, framework + ".finished"), DateTime.UtcNow.Ticks.ToString());
                }
            }
            """;
        (int Pid, long Ticks)? StartOf(string framework)
        {
            try
            {
                var parts = File.ReadAllText(Path.Combine(session, framework + ".started")).Split('|');
                return parts.Length == 2 && int.TryParse(parts[0], out int pid) && long.TryParse(parts[1], out long ticks) ? (pid, ticks) : null;
            }
            catch (IOException) { return null; }
        }
        bool BothStarted() => StartOf("net10.0") is not null && StartOf("net11.0") is not null;
        bool PassedBoth() => results.GetValueOrDefault(Identity(project, "ParallelChecks.Overlap"))?.Values is { Count: 2 } found
            && found.All(result => result.Outcome == DotnetTestOutcome.Passed) && found.Select(result => result.Framework).Order().SequenceEqual(new[] { "net10.0", "net11.0" });
        void Start(string mode, string extra = "")
        {
            foreach (var host in hosts) host.Dispose();
            hosts = [];
            session = Directory.CreateDirectory(Path.Combine(root, "sessions", Guid.NewGuid().ToString("N"))).FullName;
            File.WriteAllLines(control, [mode, session]);
            File.Delete(Path.Combine(root, "Directory.Build.props"));
            File.WriteAllText(project, projectText.Replace("EXTRA", extra, StringComparison.Ordinal)
                .Replace("HOLD_COMMAND", OperatingSystem.IsWindows() ? "ping -n 12 127.0.0.1 &gt;nul" : "sleep 11", StringComparison.Ordinal));
            File.Delete(Path.Combine(root, "build.trace"));
            File.Delete(Path.Combine(root, "build-entered"));
            File.Delete(Path.Combine(root, "build-finished"));
            Run(project, null, mode);
            execution = dotnetRuns.GetValueOrDefault(project);
        }
        void CaptureHosts()
        {
            if (!BothStarted()) return;
            hosts = new[] { "net10.0", "net11.0" }.Select(framework => Process.GetProcessById(StartOf(framework)!.Value.Pid)).ToArray();
        }
        bool HostsExited() => hosts.Length == 2 && hosts.All(host => host.HasExited);
        bool Sequential() => BothStarted() && long.TryParse(File.ReadAllText(Path.Combine(session, "net10.0.finished")), out long firstEnd)
            && firstEnd <= StartOf("net11.0")!.Value.Ticks;
        script.Run(() =>
        {
            parallelTestHosts = 2;
            Directory.CreateDirectory(root);
            File.WriteAllText(project, projectText.Replace("EXTRA", string.Empty, StringComparison.Ordinal));
            File.WriteAllText(source, text);
            open(root, source);
            Show();
        });
        script.WaitFor(() => discovered && !discovering && tests.ContainsKey(project), 30);
        script.Run(() => Start("overlap"));
        script.WaitFor(() => BothStarted() && execution?.Active == 2, 90);
        script.Check("Parallel tests: native .NET 10 and .NET 11 hosts are active together in separate terminal tabs", () => BothStarted()
            && execution is { Parallel: true, Active: 2 } && execution.TerminalKeys.Count == 2
            && execution.TerminalKeys.Any(key => key.EndsWith(":net10.0", StringComparison.Ordinal)) && execution.TerminalKeys.Any(key => key.EndsWith(":net11.0", StringComparison.Ordinal)));
        script.WaitFor(() => !running, 60);
        script.Check("Parallel tests: both native hosts cross the overlap barrier and retain their outcomes", () => PassedBoth()
            && File.Exists(Path.Combine(session, "net10.0.observed")) && File.Exists(Path.Combine(session, "net11.0.observed")));
        script.Check("Parallel tests: frameworks build once in order before prebuilt tests run", () => File.ReadAllLines(Path.Combine(root, "build.trace")).SequenceEqual(new[] { "net10.0", "net11.0" }));
        script.Run(() =>
        {
            if (BothStarted()) Console.WriteLine("[Hex self-test] native framework start gap: " + TimeSpan.FromTicks(Math.Abs(StartOf("net11.0")!.Value.Ticks - StartOf("net10.0")!.Value.Ticks)).TotalMilliseconds.ToString("0") + " ms");
            Start("serial", "<TestTfmsInParallel>false</TestTfmsInParallel>");
        });
        script.WaitFor(() => !running, 90);
        script.Check("Parallel tests: native SDK opt-out retains sequential execution", () => execution is { Parallel: false } && PassedBoth() && Sequential());
        script.Run(() => Start("serial", "<AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>"));
        script.WaitFor(() => !running, 90);
        script.Check("Parallel tests: shared output folders rebuild and test sequentially with correct binaries", () => execution is { Parallel: false } && PassedBoth() && Sequential());
        script.Run(() => Start("serial", "<ChangeOutputsDuringBuild>true</ChangeOutputsDuringBuild>"));
        script.WaitFor(() => !running, 90);
        script.Check("Parallel tests: output isolation is rechecked after build imports change", () => execution is { Parallel: false } && PassedBoth() && Sequential());
        script.Run(() => Start("serial", "<HoldPreparation>true</HoldPreparation>"));
        script.WaitFor(() => File.Exists(Path.Combine(root, "build-entered")), 90);
        script.Check("Parallel tests: preparation cancellation has an owned native build before any test host", () => execution is { Parallel: true }
            && execution.TerminalKeys.Contains("tests:" + project) && StartOf("net10.0") is null && StartOf("net11.0") is null);
        script.Run(Cancel);
        script.WaitFor(() => !running, 20);
        script.Settle(12);
        script.Check("Parallel tests: cancellation during build preparation prevents later builds and test hosts", () => !running
            && execution is { Cancelled: true } && execution.TerminalKeys.Count == 0 && dotnetRuns.Count == 0 && waiting.Count == 0
            && !File.Exists(Path.Combine(root, "build-finished")) && StartOf("net10.0") is null && StartOf("net11.0") is null);
        script.Run(() => { parallelTestHosts = 1; Start("serial"); });
        script.WaitFor(() => !running, 90);
        script.Check("Parallel tests: worker capacity bounds active hosts and drains queued frameworks", () => execution is { Parallel: true, Active: 0 } && PassedBoth() && Sequential());
        script.Run(() => Start("hold"));
        script.WaitFor(() => StartOf("net10.0") is not null, 90);
        script.Run(() =>
        {
            if (StartOf("net10.0") is { } first) hosts = [Process.GetProcessById(first.Pid)];
        });
        script.Run(Cancel);
        script.WaitFor(() => !running && hosts.Length == 1 && hosts[0].HasExited, 20);
        script.Check("Parallel tests: cancellation cannot start a framework waiting for worker capacity", () => !running && hosts.Length == 1 && hosts[0].HasExited
            && execution is { Cancelled: true, Active: 0 } && execution.Pending.Count == 0 && StartOf("net11.0") is null);
        script.Run(() => parallelTestHosts = 2);
        script.Run(() => Start("hold"));
        script.WaitFor(() => BothStarted(), 90);
        script.Run(CaptureHosts);
        script.Check("Parallel tests: cancellation probe owns two live native hosts", () => hosts.Length == 2 && hosts.All(host => !host.HasExited));
        script.Run(Cancel);
        script.WaitFor(() => !running && HostsExited(), 20);
        script.Check("Parallel tests: Cancel terminates every host and drains one logical run", () => !running && HostsExited()
            && execution is { Cancelled: true, Active: 0 } && execution.TerminalKeys.Count == 0 && dotnetRuns.Count == 0 && waiting.Count == 0);
        script.Check("Parallel tests: cancelled hosts cannot retain earlier passing results", () => results.Values.SelectMany(cases => cases.Values).All(result => result.Outcome != DotnetTestOutcome.Passed));
        script.Run(() => Start("hold"));
        script.WaitFor(() => BothStarted(), 90);
        script.Run(CaptureHosts);
        script.Run(() => replace(Directory.CreateDirectory(Path.Combine(root, "replacement")).FullName));
        script.WaitFor(HostsExited, 20);
        script.Settle(0.5f);
        script.Check("Parallel tests: workspace replacement terminates both hosts and rejects late results", () => HostsExited() && !running
            && results.Count == 0 && waiting.Count == 0 && dotnetRuns.Count == 0 && !File.Exists(Path.Combine(session, "net10.0.finished")) && !File.Exists(Path.Combine(session, "net11.0.finished")));
        script.Run(() => { foreach (var host in hosts) host.Dispose(); parallelTestHosts = previousLimit; });
    }
}
