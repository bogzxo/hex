using Horizon.UI.Scripting;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestPythonEnvironment(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "python environment");
        string file = Path.Combine(root, "sample.py");
        string interpreter = PythonEnvironment.ExecutableIn(Path.Combine(root, ".venv"));
        Task? setup = null;
        LanguageServer? server = null;
        Task<string?>? hover = null;
        test.Run(() => setup = Task.Run(async () =>
        {
            Directory.CreateDirectory(root);
            var created = await CMakeProject.Run("python", root, ["-m", "venv", "--without-pip", Path.Combine(root, ".venv")], CancellationToken.None);
            if (created.ExitCode != 0) throw new IOException(created.Output);
            var installed = await CMakeProject.Run(interpreter, root, ["-c", "import pathlib,sysconfig; pathlib.Path(sysconfig.get_path('purelib'), 'hex_environment_only.py').write_text('answer: str = \\\"environment value\\\"\\n')"], CancellationToken.None);
            if (installed.ExitCode != 0) throw new IOException(installed.Output);
            File.WriteAllText(file, "from hex_environment_only import answer\nvalue: str = answer\n");
        }));
        test.WaitFor(() => setup?.IsCompleted == true, 30);
        test.Check("Python environment: isolated interpreter and package created", () => setup?.IsCompletedSuccessfully == true);
        test.Run(() => { explorer.OpenFolder(root); Open(file); });
        test.WaitFor(() => (server = LanguageServers.Running.FirstOrDefault(s => s.Name == "python" && PlatformPaths.Same(s.RootDirectory, root))) is { IsReady: true }, 30);
        test.Check("Python environment: Pyright receives the project interpreter", () => server?.Settings?["python"]?["pythonPath"]?.GetValue<string>() == interpreter);
        test.WaitFor(() => languages.ServerDiagnosticsOf(document) is { Count: 0 }, 30);
        test.Check("Python environment: environment-only import resolves", () => languages.ServerDiagnosticsOf(document) is { Count: 0 });
        test.Run(() => hover = server?.Hover(file, 1, 14));
        test.WaitFor(() => hover?.IsCompleted == true, 15);
        test.Check("Python environment: imported value has its declared type", () => hover is { IsCompletedSuccessfully: true } && hover.Result?.Contains("str") == true);

        // Two interpreters under one workspace root must not share a server's import
        // resolution. Give the same module a different type in the nested environment.
        string nested = Path.Combine(root, "nested project");
        string nestedFile = Path.Combine(nested, "sample.py");
        string nestedInterpreter = PythonEnvironment.ExecutableIn(Path.Combine(nested, "venv"));
        LanguageServer? nestedServer = null;
        Task<string?>? nestedHover = null;
        test.Run(() => setup = Task.Run(async () =>
        {
            Directory.CreateDirectory(nested);
            var created = await CMakeProject.Run("python", nested, ["-m", "venv", "--without-pip", Path.Combine(nested, "venv")], CancellationToken.None);
            if (created.ExitCode != 0) throw new IOException(created.Output);
            var installed = await CMakeProject.Run(nestedInterpreter, nested, ["-c", "import pathlib,sysconfig; pathlib.Path(sysconfig.get_path('purelib'), 'hex_environment_only.py').write_text('answer: int = 42\\n')"], CancellationToken.None);
            if (installed.ExitCode != 0) throw new IOException(installed.Output);
            File.WriteAllText(nestedFile, "from hex_environment_only import answer\nvalue: int = answer\n");
        }));
        test.WaitFor(() => setup?.IsCompleted == true, 30);
        test.Check("Python environment: second isolated package created", () => setup?.IsCompletedSuccessfully == true);
        test.Run(() => Open(nestedFile));
        test.WaitFor(() => (nestedServer = LanguageServers.Running.FirstOrDefault(s => s.Name == "python"
            && s.Settings?["python"]?["pythonPath"]?.GetValue<string>() == nestedInterpreter)) is { IsReady: true }, 30);
        test.Check("Python environment: nested interpreter has a separate server", () => nestedServer is { IsReady: true }
            && !ReferenceEquals(server, nestedServer) && PlatformPaths.Same(server!.RootDirectory, nestedServer.RootDirectory));
        test.WaitFor(() => languages.ServerDiagnosticsOf(document) is { Count: 0 }, 30);
        test.Check("Python environment: nested import uses its own package", () => languages.ServerDiagnosticsOf(document) is { Count: 0 });
        test.Run(() => { nestedHover = nestedServer?.Hover(nestedFile, 1, 14); hover = server?.Hover(file, 1, 14); });
        test.WaitFor(() => nestedHover?.IsCompleted == true && hover?.IsCompleted == true, 15);
        test.Check("Python environment: package types stay isolated in both directions", () =>
            nestedHover is { IsCompletedSuccessfully: true } && nestedHover.Result?.Contains("int") == true
            && hover is { IsCompletedSuccessfully: true } && hover.Result?.Contains("str") == true);

        LanguageServer? replacement = null, nestedReplacement = null;
        test.Run(() => Crash(server!));
        test.WaitFor(() => (replacement = LanguageServers.Running.FirstOrDefault(s => s.Name == "python"
            && s.Settings?["python"]?["pythonPath"]?.GetValue<string>() == interpreter)) is { IsReady: true }
            && !ReferenceEquals(server, replacement), 30);
        test.Check("Python environment: failed server recovers without restarting its sibling", () =>
            replacement is { IsReady: true, Failure: null } && !ReferenceEquals(server, replacement)
            && nestedServer is { IsRunning: true, Failure: null } && LanguageServers.Running.Contains(nestedServer));
        test.Run(() => Crash(nestedServer!));
        test.WaitFor(() => (nestedReplacement = LanguageServers.Running.FirstOrDefault(s => s.Name == "python"
            && s.Settings?["python"]?["pythonPath"]?.GetValue<string>() == nestedInterpreter)) is { IsReady: true }
            && !ReferenceEquals(nestedServer, nestedReplacement), 30);
        test.Check("Python environment: sibling has its own automatic recovery budget", () =>
            nestedReplacement is { IsReady: true, Failure: null } && !ReferenceEquals(nestedServer, nestedReplacement)
            && replacement is { IsRunning: true, Failure: null } && LanguageServers.Running.Contains(replacement));
        test.Run(() => { nestedHover = nestedReplacement?.Hover(nestedFile, 1, 14); hover = replacement?.Hover(file, 1, 14); });
        test.WaitFor(() => nestedHover?.IsCompleted == true && hover?.IsCompleted == true, 15);
        test.Check("Python environment: recovery restores both documents and their interpreter settings", () =>
            nestedHover is { IsCompletedSuccessfully: true } && nestedHover.Result?.Contains("int") == true
            && hover is { IsCompletedSuccessfully: true } && hover.Result?.Contains("str") == true);

        LanguageServer? manuallyRestarted = null;
        test.Run(() => AllCommands().First(command => command.Text.EndsWith(Said("languages.restart_server"), StringComparison.Ordinal)).Picked());
        test.WaitFor(() => (manuallyRestarted = LanguageServers.Running.FirstOrDefault(s => s.Name == "python"
            && s.Settings?["python"]?["pythonPath"]?.GetValue<string>() == nestedInterpreter)) is { IsReady: true }
            && !ReferenceEquals(nestedReplacement, manuallyRestarted), 30);
        test.Check("Python environment: manual restart targets the current file's server", () =>
            manuallyRestarted is { IsReady: true, Failure: null } && !ReferenceEquals(nestedReplacement, manuallyRestarted)
            && replacement is { IsRunning: true, Failure: null } && LanguageServers.Running.Contains(replacement));
        test.Run(() => nestedHover = manuallyRestarted?.Hover(nestedFile, 1, 14));
        test.WaitFor(() => nestedHover?.IsCompleted == true, 15);
        test.Check("Python environment: manual restart restores language features", () =>
            nestedHover is { IsCompletedSuccessfully: true } && nestedHover.Result?.Contains("int") == true);

        test.Run(() => { Open(file); languages.UsePythonInterpreter(root, nestedInterpreter); });
        test.WaitFor(() => languages.ServerDiagnosticsOf(document) is { Count: > 0 }, 30);
        test.Check("Python selection: custom interpreter refreshes import types", () =>
            PythonEnvironment.FindLocal(root, root) == nestedInterpreter
            && languages.ServerDiagnosticsOf(document)?.Any(d => d.Message.Contains("int") && d.Message.Contains("str")) == true);
        test.Run(() => languages.UsePythonInterpreter(root, null));
        test.WaitFor(() => languages.ServerDiagnosticsOf(document) is { Count: 0 }, 30);
        test.Check("Python selection: automatic environment can be restored", () =>
            PythonEnvironment.FindLocal(root, root) == interpreter && languages.ServerDiagnosticsOf(document) is { Count: 0 });

        // Terminate only the process owned by this isolated fixture. Dispose is an
        // intentional shutdown and does not represent a server unexpectedly exiting.
        static void Crash(LanguageServer target)
        {
            var field = typeof(LanguageServer).GetField("process", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var process = (System.Diagnostics.Process)field.GetValue(target)!;
            process.Kill(entireProcessTree: true);
        }
    }
}
