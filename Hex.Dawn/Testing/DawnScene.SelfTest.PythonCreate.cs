namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestCreatePythonEnvironment(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "create python environment");
        string interpreter = PythonEnvironment.ExecutableIn(Path.Combine(root, ".venv"));
        Task<CMakeResult>? pip = null, run = null;
        Task<string>? duplicate = null;
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "main.py");
            File.WriteAllText(file, "print('hello')\n");
            explorer.OpenFolder(root);
            Open(file);
            languages.CreatePythonEnvironment(root, Hex.Core.Notebooks.KernelSpecs.Pythons().First());
        });
        test.WaitFor(() => PythonEnvironment.Selected(root) == interpreter, 45f);
        test.Check("Python creation: completed environment is selected", () => PythonEnvironment.Selected(root) == interpreter
            && File.Exists(Path.Combine(root, ".venv", "pyvenv.cfg")));
        test.Run(() => pip = CMakeProject.Run(interpreter, root, ["-m", "pip", "--version"], default));
        test.WaitFor(() => pip?.IsCompleted == true, 15f);
        test.Check("Python creation: pip is available in the new environment", () => pip is { IsCompletedSuccessfully: true }
            && pip.Result.ExitCode == 0 && pip.Result.Output.Contains(Path.Combine(root, ".venv"), StringComparison.OrdinalIgnoreCase));
        test.Run(() => run = CMakeProject.Run(PythonEnvironment.FindLocal(root, root)!, root, ["-c", "import sys; print(sys.prefix)"], default));
        test.WaitFor(() => run?.IsCompleted == true, 10f);
        test.Check("Python creation: selected interpreter runs in the environment", () => run is { IsCompletedSuccessfully: true }
            && run.Result.ExitCode == 0 && PlatformPaths.Same(run.Result.Output.Trim(), Path.Combine(root, ".venv")));
        test.Run(() =>
        {
            File.WriteAllText(Path.Combine(root, ".venv", "preserve.txt"), "existing environment");
            duplicate = PythonEnvironment.Create("must-not-start", root, _ => { }, default);
        });
        test.WaitFor(() => duplicate?.IsCompleted == true, 5f);
        test.Check("Python creation: existing environment is preserved", () => duplicate is { IsFaulted: true }
            && duplicate.Exception?.GetBaseException() is IOException
            && File.ReadAllText(Path.Combine(root, ".venv", "preserve.txt")) == "existing environment"
            && PythonEnvironment.Selected(root) == interpreter);
        test.Run(() => PythonEnvironment.Select(root, null));
    }
}
