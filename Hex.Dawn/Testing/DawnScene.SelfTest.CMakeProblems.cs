namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestCMakeProblems(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "cmake configure problems");
        string project = Path.Combine(root, "CMakeLists.txt");
        string check = Path.Combine(root, "cmake", "check.cmake");
        test.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(check)!);
            File.WriteAllText(project, "cmake_minimum_required(VERSION 3.20)\nproject(ConfigurationOnly NONE)\ninclude(cmake/check.cmake)\n");
            File.WriteAllText(check, "# Configuration fixture\nmessage(FATAL_ERROR \"Missing fixture SDK\\nInstall the fixture SDK first\")\n");
            explorer.OpenFolder(root);
            Open(project);
            debugger.ConfigureCMake();
        });
        test.WaitFor(() => !debugger.Debugging, 30f);
        test.Settle(0.2f);
        test.Check("CMake problems: failure identifies the included configuration file", () => terminalProblems.Any(problem =>
            PlatformPaths.Same(problem.Path, check) && problem.Line == 1 && !problem.Warning
            && problem.Text.Contains("Missing fixture SDK") && problem.Text.Contains("Install the fixture SDK first")));
        test.Run(() => Descendants(problems).OfType<Button>().FirstOrDefault(button => button.Tooltip.Contains("Missing fixture SDK"))?.OnPressed?.Invoke());
        test.Check("CMake problems: row opens the failing configuration command", () => PlatformPaths.Same(document.Path!, check)
            && code.Document.Caret.Line == 1);
        test.Run(() =>
        {
            code.Text = "# Configuration fixture\nmessage(STATUS \"Fixture SDK available\")\n";
            Save();
            Context.Terminals.AddProblem(project, 1, 1, "stale CMake configuration error", false);
            debugger.ConfigureCMake();
        });
        test.WaitFor(() => !debugger.Debugging, 30f);
        test.Settle(0.2f);
        test.Check("CMake problems: repaired configuration succeeds", () =>
            File.Exists(Path.Combine(root, "build", "hex-debug", "CMakeCache.txt"))
            && said.Any(row => row.Text.Contains("Fixture SDK available")));
        test.Check("CMake problems: successful reconfigure clears old errors", () => !terminalProblems.Any(problem =>
            !problem.Warning && (PlatformPaths.Same(problem.Path, check) || PlatformPaths.Same(problem.Path, project))));
        string presets = Path.Combine(root, "CMakePresets.json"), userPresets = Path.Combine(root, "CMakeUserPresets.json");
        test.Run(() =>
        {
            File.WriteAllText(presets, "{\"version\":6,\"configurePresets\":[]}\n");
            File.WriteAllText(userPresets, "invalid json\n");
            Open(presets);
        });
        test.Check("CMake presets: configure is available from shared presets", () => debugger.CanConfigureCMake);
        test.Run(() => Open(userPresets));
        test.Check("CMake presets: configure is available from user presets", () => debugger.CanConfigureCMake);
        int messages = 0;
        test.Run(() => { messages = said.Count; debugger.ConfigureCMake(); });
        test.WaitFor(() => !debugger.Debugging, 30);
        test.Check("CMake presets: malformed configuration preserves native error details", () => said.Skip(messages).Any(row =>
            row.Error && row.Text.Contains("CMakeUserPresets.json", StringComparison.Ordinal)));
    }
}
