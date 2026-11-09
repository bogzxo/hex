using System.Diagnostics;
using Hex.Core.Projects;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestDotnetTargetFrameworks(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "dotnet target frameworks"), project = Path.Combine(root, "Probe.csproj"), file = Path.Combine(root, "Program.cs");
        string result = Path.Combine(root, "framework-result.txt"), baseline = string.Empty;
        Task<string>? sdk = null;
        const string source = "string framework = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>(System.Reflection.Assembly.GetExecutingAssembly())!.FrameworkName;\n"
            + "string runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;\n"
            + "System.IO.File.WriteAllText(\"framework-result.txt\", framework + \"|\" + runtime + \"|\" + string.Join(\"|\", args));\n"
            + "System.Console.WriteLine(\"framework-finished\");\n";
        void WriteProject(string expected) => WriteSelfTestProject(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFrameworks>net10.0;net11.0</TargetFrameworks><OutputPath>selected-output/$(Configuration)/</OutputPath></PropertyGroup>
              <Target Name="RejectOtherFramework" BeforeTargets="CoreCompile"><Error Condition="'$(TargetFramework)' != '
            """ + expected + "'\" Text=\"An unselected framework was built.\" /></Target></Project>");
        bool HasFramework(string version) => sideTree.Items.FirstOrDefault(section => section.Text == Said("debugging.variables"))?.Children?
            .Any(row => row.Text.StartsWith("framework = ", StringComparison.Ordinal) && row.Text.Contains("Version=v" + version, StringComparison.Ordinal)) == true;
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
        void Pick(string framework)
        {
            if (textDialog is not { IsOpen: true } dialog) return;
            var box = Descendants(dialog.Body).OfType<TextBox>().First();
            box.Text = framework;
            box.OnChanged?.Invoke(framework);
            box.OnSubmitted?.Invoke(framework);
        }
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            WriteProject("net10.0");
            File.WriteAllText(file, source);
            explorer.OpenFolder(root);
            Open(file);
            debugger.breakpoints.Clear();
            debugger.ToggleBreakpoint(file, 2);
        });
        foreach (string framework in new[] { "net10.0", "net11.0" })
        {
            string version = framework == "net10.0" ? "10.0" : "11.0";
            string command = "dotnet run --no-launch-profile -c Checked --framework " + framework + " -- \"two words\" -f application";
            test.Run(() => { sdk = null; WriteProject(framework); sdk = RunSdk(command); });
            test.WaitFor(() => sdk?.IsCompleted == true, 60);
            test.Check("Framework: native SDK runs only " + framework, () => sdk?.IsCompletedSuccessfully == true);
            test.Run(() =>
            {
                if (sdk?.IsFaulted == true) Console.WriteLine("[Hex self-test] framework SDK: " + sdk.Exception);
                baseline = sdk?.IsCompletedSuccessfully == true ? sdk.Result : string.Empty;
                Console.WriteLine("[Hex self-test] SDK " + framework + ": " + baseline);
                if (File.Exists(result)) File.Delete(result);
                if (DotnetRunLine.Of(command, root) is { } run) debugger.DebugRun(run, root, go => go());
            });
            test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == 2 && HasFramework(version), 60);
            test.Check("Framework: task builds and pauses in " + framework + " with its configuration", () => debugger.DebugStopped && code.ExecutionLine == 2 && HasFramework(version));
            test.Check("Framework: explicit task does not ask for a framework", () => textDialog is not { IsOpen: true });
            test.Run(RunOrPlay);
            test.WaitFor(() => !debugger.Debugging && File.Exists(result), 20);
            test.Check("Framework: debugger matches native SDK runtime and arguments for " + framework, () => baseline.Length > 0 && File.Exists(result) && File.ReadAllText(result) == baseline);
        }
        test.Run(() => { WriteProject("net10.0"); if (File.Exists(result)) File.Delete(result); RunOrPlay(); });
        test.WaitFor(() => textDialog is { IsOpen: true }, 30);
        test.Check("Framework: F5 offers evaluated targets in declaration order", () => textDialog is { IsOpen: true } dialog
            && Descendants(dialog.Body).OfType<ListBox>().Any(list => list.Items.SequenceEqual(new[] { "net10.0", "net11.0" })));
        PressKeys(test, "Escape");
        test.WaitFor(() => textDialog is not { IsOpen: true } && !debugger.Debugging, 5);
        test.Check("Framework: Escape leaves no build or debug session", () => !debugger.Debugging && textDialog is not { IsOpen: true } && !File.Exists(result));
        test.Run(RunOrPlay);
        test.WaitFor(() => textDialog is { IsOpen: true }, 30);
        test.Run(() => Pick("net10.0"));
        test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == 2 && HasFramework("10.0"), 60);
        test.Check("Framework: chosen F5 target builds alone and exposes native locals", () => debugger.DebugStopped && code.ExecutionLine == 2 && HasFramework("10.0"));
        test.Run(debugger.RestartDebugging);
        test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == 2 && HasFramework("10.0"), 60);
        test.Check("Framework: Restart retains the selected target", () => debugger.DebugStopped && HasFramework("10.0") && textDialog is not { IsOpen: true });
        test.Run(debugger.StopDebugging);
        test.WaitFor(() => !debugger.Debugging, 10);
        test.Run(RunOrPlay);
        test.WaitFor(() => debugger.DebugStopped && HasFramework("10.0"), 60);
        test.Check("Framework: subsequent F5 remembers the project selection", () => debugger.DebugStopped && HasFramework("10.0") && textDialog is not { IsOpen: true });
        test.Run(RunOrPlay);
        test.WaitFor(() => !debugger.Debugging && File.Exists(result), 20);
        test.Check("Framework: ordinary F5 launches the selected binary without task arguments", () => File.Exists(result)
            && File.ReadAllText(result).StartsWith(".NETCoreApp,Version=v10.0|.NET 10.", StringComparison.Ordinal) && File.ReadAllText(result).EndsWith('|'));
        test.Run(() =>
        {
            WriteProject("net11.0");
            if (File.Exists(result)) File.Delete(result);
            PaletteChoices().First(choice => choice.Text == Said("debugging.select_framework")).Picked?.Invoke();
        });
        test.WaitFor(() => textDialog is { IsOpen: true }, 30);
        test.Check("Framework: Run menu can change a remembered selection", () => textDialog is { IsOpen: true });
        test.Run(() => Pick("net11.0"));
        test.WaitFor(() => debugger.DebugStopped && HasFramework("11.0"), 60);
        test.Check("Framework: changing selection launches the other native runtime", () => debugger.DebugStopped && HasFramework("11.0"));
        test.Run(debugger.StopDebugging);
        test.WaitFor(() => !debugger.Debugging, 10);
        test.Run(() => PaletteChoices().First(choice => choice.Text == Said("debugging.select_framework")).Picked?.Invoke());
        test.WaitFor(() => textDialog is { IsOpen: true }, 30);
        Action? stalePick = null;
        test.Run(() =>
        {
            if (textDialog is { IsOpen: true } dialog)
            {
                var box = Descendants(dialog.Body).OfType<TextBox>().First();
                box.Text = "net10.0";
                box.OnChanged?.Invoke("net10.0");
                stalePick = () => box.OnSubmitted?.Invoke("net10.0");
            }
        });
        PressKeys(test, "Escape");
        test.Run(() =>
        {
            string replacement = Path.Combine(testFolder, "framework replacement workspace");
            Directory.CreateDirectory(replacement);
            explorer.OpenFolder(replacement);
            stalePick?.Invoke();
        });
        test.Settle(1);
        test.Check("Framework: a previous workspace's selection cannot launch a project", () => !debugger.Debugging && !File.Exists(result));
        test.Check("Framework: debugging preserves the project and source", () => File.ReadAllText(file) == source && File.ReadAllText(project).Contains("<TargetFrameworks>net10.0;net11.0</TargetFrameworks>", StringComparison.Ordinal));
        test.Run(() => { if (debugger.Debugging) debugger.StopDebugging(); debugger.breakpoints.Clear(); });
    }
}
