using Horizon.UI.Components;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestCMakeConfigure(HexPointerScript test) => TestCMakeConfigure(test, false);

    private void TestCMakeConfigure(HexPointerScript test, bool multiConfiguration)
    {
        string root = Path.Combine(testFolder, "cmake library configure");
        string source = Path.Combine(root, "answer.cpp");
        string project = Path.Combine(root, "CMakeLists.txt");
        string build = Path.Combine(root, "build", "hex-debug");
        HexDocument? code = null;
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(project, "cmake_minimum_required(VERSION 3.20)\nproject(LibraryOnly LANGUAGES CXX)\nadd_library(answer STATIC answer.cpp)\ntarget_compile_definitions(answer PRIVATE PROJECT_ANSWER=42)\n");
            File.WriteAllText(source, "int answer() { return PROJECT_ANSWER; }\n");
            if (multiConfiguration)
            {
                File.AppendAllText(project, "target_compile_definitions(answer PRIVATE $<$<CONFIG:Checked>:CHECKED_BUILD>)\nenable_testing()\nadd_test(NAME debug_only COMMAND \"${CMAKE_COMMAND}\" -E true CONFIGURATIONS Debug)\nadd_test(NAME checked_only COMMAND \"${CMAKE_COMMAND}\" -E true CONFIGURATIONS Checked)\n");
                File.AppendAllText(source, "#ifndef CHECKED_BUILD\n#error Wrong editor configuration\n#endif\n");
                var variables = new JsonObject {
                    ["CMAKE_CONFIGURATION_TYPES"] = "Debug;Checked",
                    ["CMAKE_MAKE_PROGRAM"] = CMakeProject.Tool("ninja")!.Replace('\\', '/'),
                    ["CMAKE_CXX_COMPILER"] = LanguageServerProgram.Locate("clang++")!.Replace('\\', '/') };
                if (OperatingSystem.IsWindows())
                {
                    variables["CMAKE_RC_COMPILER"] = LanguageServerProgram.Locate("llvm-rc")!.Replace('\\', '/');
                    variables["CMAKE_MT"] = LanguageServerProgram.Locate("llvm-mt")!.Replace('\\', '/');
                }
                File.WriteAllText(Path.Combine(root, "CMakePresets.json"), new JsonObject {
                    ["version"] = 6, ["configurePresets"] = new JsonArray(new JsonObject {
                        ["name"] = "library", ["generator"] = "Ninja Multi-Config",
                        ["binaryDir"] = build.Replace('\\', '/'), ["cacheVariables"] = variables }) }.ToJsonString());
            }
            explorer.OpenFolder(root);
            Open(source);
            code = document;
        });
        test.WaitFor(() => code is not null && languages.ServerDiagnosticsOf(code) is { Count: > 0 }, 20f);
        test.Check("CMake configure: compiler definition initially missing", () => code is not null && languages.ServerDiagnosticsOf(code) is { Count: > 0 });
        test.Run(() => Open(project));
        test.Check("CMake configure: command is available from CMakeLists.txt", () => debugger.CanConfigureCMake);
        test.Run(debugger.ConfigureCMake);
        if (multiConfiguration) Choose("Checked");
        test.WaitFor(() => !debugger.Debugging && File.Exists(Path.Combine(build, "compile_commands.json")), 30f);
        test.Check("CMake configure: library-only project configures without target or debugger", () => !debugger.Debugging && !debugger.DebugStopped && textDialog is not { IsOpen: true } && File.Exists(Path.Combine(build, "compile_commands.json")));
        test.WaitFor(() => code is not null && languages.ServerDiagnosticsOf(code) is { Count: 0 }, 20f);
        test.Check("CMake configure: live clangd uses project compiler definition", () => code is not null && languages.ServerDiagnosticsOf(code) is { Count: 0 });
        test.Check("CMake configure: no library was compiled", () => !File.Exists(Path.Combine(build, "answer.lib")) && !File.Exists(Path.Combine(build, "libanswer.a")));
        test.Run(debugger.BuildCMake);
        test.WaitFor(() => !debugger.Debugging, 30f);
        test.Check("CMake build: library compiles without launching a debugger", () => !debugger.DebugStopped
            && textDialog is not { IsOpen: true }
            && Directory.EnumerateFiles(build, "*answer*", SearchOption.AllDirectories).Any(path => Path.GetExtension(path) is ".lib" or ".a"));
        test.Run(() =>
        {
            Open(source);
            document.Editor.Text += "\n#error HEX_BUILD_FAILURE\n";
        });
        test.Wait();
        test.Run(debugger.BuildCMake);
        test.WaitFor(() => !debugger.Debugging, 30f);
        test.Check("CMake build: compiler errors reach Problems", () => terminalProblems.Any(problem =>
            PlatformPaths.Same(problem.Path, source) && !problem.Warning && problem.Text.Contains("HEX_BUILD_FAILURE")));
        test.Run(() =>
        {
            document.Editor.Text = document.Editor.Text.Replace("\n#error HEX_BUILD_FAILURE\n", "");
        });
        test.Wait();
        test.Run(debugger.BuildCMake);
        test.WaitFor(() => !debugger.Debugging, 30f);
        test.Check("CMake build: repaired build clears compiler errors", () => !terminalProblems.Any(problem =>
            PlatformPaths.Same(problem.Path, source) && !problem.Warning));
        test.Run(() => Open(project));
        if (multiConfiguration)
        {
            testExplorer.TestConfiguredCTest(test, build, "Checked", "checked_only");
            test.Run(debugger.ConfigureCMake);
            Choose("Debug");
            test.WaitFor(() => !debugger.Debugging && CMakeProject.SelectedBuild(root)?.Configuration == "Debug", 30f);
            test.WaitFor(() => code is not null && languages.ServerDiagnosticsOf(code) is { Count: > 0 }, 20f);
            test.Check("CMake configure: changing configuration refreshes compiler diagnostics", () => code is not null && languages.ServerDiagnosticsOf(code) is { Count: > 0 });
            testExplorer.TestConfiguredCTest(test, build, "Debug", "debug_only");
            test.Run(() =>
            {
                string changed = File.ReadAllText(project).Replace("NAME debug_only", "NAME replacement");
                document.Editor.Text = changed;
                debugger.ConfigureCMake();
            });
            Choose("Debug");
            test.WaitFor(() => !debugger.Debugging, 30f);
            testExplorer.TestConfiguredCTest(test, build, "Debug", "replacement");
            test.Run(() =>
            {
                document.Editor.Text = string.Join("\n", File.ReadAllLines(project).Where(line => !line.StartsWith("add_test(")));
                debugger.ConfigureCMake();
            });
            Choose("Debug");
            test.WaitFor(() => !debugger.Debugging, 30f);
            testExplorer.TestConfiguredCTest(test, build, "Debug", null);
            test.Run(() =>
            {
                document.Editor.Text += "\nadd_test(NAME newly_added COMMAND \"${CMAKE_COMMAND}\" -E true CONFIGURATIONS Debug)\n";
                debugger.ConfigureCMake();
            });
            Choose("Debug");
            test.WaitFor(() => !debugger.Debugging, 30f);
            testExplorer.TestConfiguredCTest(test, build, "Debug", "newly_added");
        }
        test.Run(() => { if (code is not null) CloseDocument(code); CloseDocument(document); });

        void Choose(string configuration)
        {
            test.WaitFor(() => textDialog is { IsOpen: true }, 30f);
            test.Check("CMake configure: library configurations are offered", () => textDialog is { IsOpen: true } dialog
                && Descendants(dialog.Body).OfType<ListBox>().Any(list => list.Items.Length == 2));
            test.Run(() =>
            {
                if (textDialog is { IsOpen: true } dialog)
                {
                    var box = Descendants(dialog.Body).OfType<TextBox>().First();
                    box.Text = configuration;
                    box.OnChanged?.Invoke(configuration);
                }
            });
            test.Wait();
            test.Run(() =>
            {
                if (textDialog is { IsOpen: true } dialog) Descendants(dialog.Body).OfType<TextBox>().First().OnSubmitted?.Invoke(configuration);
            });
        }
    }
}
