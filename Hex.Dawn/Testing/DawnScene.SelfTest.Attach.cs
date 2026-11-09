using System.Diagnostics;
using Hex.Core.Projects;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestDebugAttach(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "external attach"), project = Path.Combine(root, "Attach.csproj"), file = Path.Combine(root, "Program.cs");
        string ready = Path.Combine(root, "ready"), heartbeat = Path.Combine(root, "heartbeat"), go = Path.Combine(root, "go");
        const string source = "int answer = 42;\nSystem.IO.File.WriteAllText(\"ready\", System.Environment.ProcessId.ToString());\n"
            + "while (!System.IO.File.Exists(\"go\")) System.Threading.Thread.Sleep(10);\nanswer++;\n"
            + "System.IO.File.WriteAllText(\"passed\", answer.ToString());\n"
            + "while (!System.IO.File.Exists(\"exit\")) { System.IO.File.WriteAllText(\"heartbeat\", System.DateTime.UtcNow.Ticks.ToString()); System.Threading.Thread.Sleep(50); }\n";
        Task<(int Code, string Output)>? build = null;
        Process? external = null;
        string label = string.Empty, beat = string.Empty;
        Action? stalePick = null;
        void AttachMenu() => PaletteChoices().FirstOrDefault(choice => choice.Text == Said("debugging.attach"))?.Picked?.Invoke();
        void Pick(string text)
        {
            if (textDialog is not { IsOpen: true } dialog) return;
            var box = Descendants(dialog.Body).OfType<TextBox>().First();
            box.Text = text;
            box.OnChanged?.Invoke(text);
            box.OnSubmitted?.Invoke(text);
        }
        bool Locals(string value) => sideTree.Items.FirstOrDefault(section => section.Text == Said("debugging.variables"))?.Children?
            .Any(row => row.Text.StartsWith("answer = " + value, StringComparison.Ordinal)) == true;
        bool Alive() => external is { HasExited: false };
        string Beat()
        {
            try
            {
                using var stream = new FileStream(heartbeat, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) { return string.Empty; }
        }
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            WriteSelfTestProject(project, CSHARP_PROJECT);
            File.WriteAllText(file, source);
            explorer.OpenFolder(root);
            Open(file);
            build = DotnetCli.Run(root, ["build", project, "--nologo"], TimeSpan.FromMinutes(2), CancellationToken.None);
            debugger.breakpoints.Clear();
            debugger.ToggleBreakpoint(file, 3);
        });
        test.WaitFor(() => build?.IsCompleted == true, 60);
        test.Check("Attach: native SDK builds the independently launched application", () => build?.IsCompletedSuccessfully == true && build.Result.Code == 0);
        test.Run(() =>
        {
            if (build?.IsCompletedSuccessfully != true || build.Result.Code != 0) return;
            var info = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add(Path.Combine(root, "bin", "Debug", "net11.0", "Attach.dll"));
            external = Process.Start(info);
            if (external is not null) ChildProcesses.Adopt(external); // The fixture owns cleanup, never the debugger.
        });
        test.WaitFor(() => Alive() && File.Exists(ready), 15);
        test.Run(() => { label = external is null ? "missing" : HexDebugProcess.Find(external.Id)?.Label ?? "missing"; AttachMenu(); });
        test.WaitFor(() => textDialog is { IsOpen: true }, 10);
        test.Check("Attach: searchable picker lists the running application's name and PID", () => textDialog is { IsOpen: true } dialog
            && Descendants(dialog.Body).OfType<ListBox>().Any(list => list.Items.Contains(label)));
        test.Run(() => Pick(Said("debugging.attach_refresh")));
        test.WaitFor(() => textDialog is { IsOpen: true }, 10);
        test.Check("Attach: refresh retains live processes", () => textDialog is { IsOpen: true } dialog
            && Descendants(dialog.Body).OfType<ListBox>().Any(list => list.Items.Contains(label)));
        PressKeys(test, "Escape");
        test.Check("Attach: cancel leaves the existing process running without a session", () => Alive() && !debugger.Debugging);
        test.Run(AttachMenu);
        test.WaitFor(() => textDialog is { IsOpen: true }, 10);
        test.Run(() => Pick(label));
        test.WaitFor(() => debugger.DebugStopped || debugger.DebugRunning, 20);
        test.Run(() => { if (!debugger.DebugStopped && !debugger.DebugRunning) foreach (var entry in said.TakeLast(15)) Console.WriteLine("[Attach probe] " + entry.Text); });
        test.Check("Attach: menu establishes a native .NET session", () => (debugger.DebugStopped || debugger.DebugRunning) && Alive());
        test.Check("Attach: detach replaces Stop and Restart is disabled", () => AllCommands().Any(command => command.Text.Contains(Said("debugging.detach"), StringComparison.Ordinal))
            && !PaletteChoices().Any(choice => choice.Text == Said("debugging.restart_debugging")));
        test.Run(() => { if (!debugger.DebugStopped) debugger.ContinueOrPause(); });
        test.WaitFor(() => debugger.DebugStopped && debugger.stoppedFrames.Count > 0, 15);
        test.Run(() => { if (!debugger.DebugStopped || debugger.stoppedFrames.Count == 0) foreach (var entry in said.TakeLast(15)) Console.WriteLine("[Attach pause probe] " + entry.Text); });
        test.Check("Attach: Pause obtains a native call stack", () => debugger.DebugStopped && debugger.stoppedFrames.Count > 0);
        test.Run(() => { File.WriteAllText(go, "go"); debugger.ContinueOrPause(); });
        test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == 3 && Locals("42"), 15);
        test.Check("Attach: source breakpoint resolves with native locals", () => debugger.DebugStopped && code.ExecutionLine == 3 && Locals("42"));
        test.Run(() => debugger.WatchExpression("answer * 2"));
        bool Watches(string value) => sideTree.Items.FirstOrDefault(section => section.Text == Said("debugging.watch"))?.Children?.Any(row => row.Text == "answer * 2 = " + value) == true;
        test.WaitFor(() => Watches("84"), 10);
        test.Check("Attach: Watch evaluates expressions in the attached source frame", () => Watches("84"));
        test.Run(debugger.StepOver);
        test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == 4 && Locals("43"), 15);
        test.Check("Attach: stepping updates the source marker and locals", () => debugger.DebugStopped && code.ExecutionLine == 4 && Locals("43"));
        test.WaitFor(() => Watches("86"), 10);
        test.Check("Attach: native Watch reevaluates after stepping", () => Watches("86"));
        test.Run(() => PaletteChoices().FirstOrDefault(choice => choice.Text == Said("debugging.detach"))?.Picked?.Invoke());
        test.WaitFor(() => !debugger.Debugging && File.Exists(heartbeat), 10);
        test.Run(() => beat = Beat());
        test.WaitFor(() => Beat() is { Length: > 0 } next && next != beat, 5);
        test.Check("Attach: detaching while paused resumes the process and clears source markers", () => !debugger.Debugging && Alive()
            && Beat().Length > 0 && Beat() != beat && documents.All(open => open.Editor.ExecutionLine < 0));
        test.Check("Attach: detach notice describes the debugging session, without claiming process exit", () => said.Any(entry => entry.Text == Said("debugging.detached", ("process", label))));

        test.Run(AttachMenu);
        test.WaitFor(() => textDialog is { IsOpen: true }, 10);
        test.Run(() => Pick(Said("debugging.attach_pid")));
        test.Run(() =>
        {
            if (textDialog is { IsOpen: true } dialog) Descendants(dialog.Body).OfType<TextBox>().First().OnSubmitted?.Invoke(external?.Id.ToString() ?? "0");
        });
        test.WaitFor(() => debugger.DebugStopped || debugger.DebugRunning, 20);
        test.Check("Attach: PID entry can reattach to the same application", () => debugger.Debugging && Alive());
        test.Run(() => { if (debugger.DebugStopped) debugger.ContinueOrPause(); beat = Beat(); });
        test.WaitFor(() => !debugger.DebugStopped && Beat().Length > 0 && Beat() != beat, 10);
        test.Run(debugger.StopDebugging);
        test.WaitFor(() => !debugger.Debugging, 10);
        test.Run(() => beat = Beat());
        test.WaitFor(() => Beat().Length > 0 && Beat() != beat, 5);
        test.Check("Attach: toolbar/shortcut Stop also detaches a running process", () => !debugger.Debugging && Alive() && Beat().Length > 0 && Beat() != beat);

        test.Run(AttachMenu);
        test.WaitFor(() => textDialog is { IsOpen: true }, 10);
        test.Run(() => Pick(label));
        test.WaitFor(() => debugger.DebugStopped || debugger.DebugRunning, 20);
        test.Run(() => { if (!debugger.DebugStopped) debugger.ContinueOrPause(); });
        test.WaitFor(() => debugger.DebugStopped, 15);
        test.Run(() =>
        {
            string replacement = Path.Combine(testFolder, "attach replacement");
            Directory.CreateDirectory(replacement);
            explorer.OpenFolder(replacement);
            beat = Beat();
        });
        test.WaitFor(() => !debugger.Debugging && Beat().Length > 0 && Beat() != beat, 10);
        test.Check("Attach: replacing the workspace detaches and resumes the application", () => !debugger.Debugging && Alive() && Beat().Length > 0 && Beat() != beat);

        test.Run(AttachMenu);
        test.WaitFor(() => textDialog is { IsOpen: true }, 10);
        test.Run(() => Pick(Said("debugging.attach_pid")));
        test.Run(() =>
        {
            if (textDialog is { IsOpen: true } dialog) stalePick = () => Descendants(dialog.Body).OfType<TextBox>().First().OnSubmitted?.Invoke(external?.Id.ToString() ?? "0");
            explorer.OpenFolder(root);
            stalePick?.Invoke();
        });
        test.Settle(1);
        test.Check("Attach: a stale picker from another workspace cannot attach", () => !debugger.Debugging && Alive());
        test.Run(AttachMenu);
        test.WaitFor(() => textDialog is { IsOpen: true }, 10);
        test.Run(() => Pick(Said("debugging.attach_pid")));
        test.Run(() =>
        {
            if (textDialog is { IsOpen: true } dialog) Descendants(dialog.Body).OfType<TextBox>().First().OnSubmitted?.Invoke("0");
        });
        test.Check("Attach: invalid PID leaves the IDE idle and existing program untouched", () => !debugger.Debugging && Alive());
        test.Run(AttachMenu);
        test.WaitFor(() => textDialog is { IsOpen: true }, 10);
        test.Run(() => { Pick(label); debugger.StopDebugging(); });
        test.Settle(1);
        test.Run(() => beat = Beat());
        test.WaitFor(() => !debugger.Debugging && Beat().Length > 0 && Beat() != beat, 10);
        test.Check("Attach: Stop during attachment leaves no late session and preserves the application", () => !debugger.Debugging && Alive() && Beat().Length > 0 && Beat() != beat);
        test.Check("Attach: process start time rejects reused picker identities", () => external is not null && HexDebugProcess.Find(external.Id) is {} current
            && current.IsCurrent() && !(current with { Started = current.Started.AddTicks(1) }).IsCurrent());
        test.Check("Attach: source and project are preserved", () => File.ReadAllText(file) == source && File.ReadAllText(project) == CSHARP_PROJECT);
        test.Run(AttachMenu);
        test.WaitFor(() => textDialog is { IsOpen: true }, 10);
        test.Run(() => { File.WriteAllText(Path.Combine(root, "exit"), "exit"); debugger.breakpoints.Clear(); });
        test.WaitFor(() => external?.HasExited == true, 10);
        test.Check("Attach: application can exit normally after detachment", () => external?.HasExited == true && external.ExitCode == 0);
        test.Run(() => Pick(label));
        test.Check("Attach: a process that exited while the picker was open cannot be attached", () => !debugger.Debugging
            && said.Any(entry => entry.Text == Said("debugging.attach_gone")));
        test.Run(() => external?.Dispose());
    }
}
