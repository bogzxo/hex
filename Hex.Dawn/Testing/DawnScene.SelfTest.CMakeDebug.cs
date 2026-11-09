using Horizon.UI.Components;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestCMakeDebug(HexPointerScript test) => TestCMakeDebug(test, false);

    private void TestCMakeDebug(HexPointerScript test, bool withPresets, bool multiConfiguration = false)
    {
        string folder = Path.Combine(testFolder, withPresets ? "cmake preset debug" : "cmake debug");
        string file = Path.Combine(folder, "answer.cpp");
        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.20)\nproject(HexProbe LANGUAGES CXX)\nadd_library(answer STATIC answer.cpp)\nadd_executable(probe main.cpp)\ntarget_link_libraries(probe PRIVATE answer)\nset_target_properties(probe PROPERTIES OUTPUT_NAME renamed-program)\n");
            File.WriteAllText(Path.Combine(folder, "main.cpp"), "#include <cstdio>\nint answer();\nint main() { std::printf(\"%d\\n\", answer()); }\n");
            Directory.CreateDirectory(Path.Combine(folder, "project include"));
            File.WriteAllText(Path.Combine(folder, "project include", "project_answer.h"), "#pragma once\n");
            File.WriteAllText(file, "#include <project_answer.h>\nint answer() {\n    int value = PROJECT_VALUE;\n    value++;\n    return value - 1;\n}\n");
            File.AppendAllText(Path.Combine(folder, "CMakeLists.txt"), "target_include_directories(answer PRIVATE \"${CMAKE_SOURCE_DIR}/project include\")\ntarget_compile_definitions(answer PRIVATE PROJECT_VALUE=42)\n");
            File.AppendAllText(Path.Combine(folder, "CMakeLists.txt"), "add_executable(other other.cpp)\n");
            File.WriteAllText(Path.Combine(folder, "other.cpp"), "int main() { return 0; }\n");
            if (withPresets)
            {
                var variables = new JsonObject {
                    ["CMAKE_BUILD_TYPE"] = "Debug", ["HEX_PRESET_PROOF"] = "yes",
                    ["CMAKE_MAKE_PROGRAM"] = CMakeProject.Tool("ninja")!.Replace('\\', '/'),
                    ["CMAKE_CXX_COMPILER"] = LanguageServerProgram.Locate("clang++")!.Replace('\\', '/') };
                if (multiConfiguration)
                {
                    variables["CMAKE_CONFIGURATION_TYPES"] = "Debug;Checked";
                    File.AppendAllText(Path.Combine(folder, "CMakeLists.txt"), "set(CMAKE_CXX_FLAGS_CHECKED \"${CMAKE_CXX_FLAGS_DEBUG}\")\nset(CMAKE_EXE_LINKER_FLAGS_CHECKED \"${CMAKE_EXE_LINKER_FLAGS_DEBUG}\")\n");
                    File.AppendAllText(Path.Combine(folder, "CMakeLists.txt"), "target_compile_definitions(answer PRIVATE $<$<CONFIG:Checked>:CHECKED_BUILD>)\n");
                    File.AppendAllText(file, "#ifndef CHECKED_BUILD\n#error Wrong editor configuration\n#endif\n");
                    File.AppendAllText(Path.Combine(folder, "CMakeLists.txt"), "enable_testing()\nadd_test(NAME chosen COMMAND probe)\n");
                    File.AppendAllText(Path.Combine(folder, "CMakeLists.txt"), "add_test(NAME debug_only COMMAND other CONFIGURATIONS Debug)\n");
                }
                if (OperatingSystem.IsWindows())
                {
                    variables["CMAKE_RC_COMPILER"] = LanguageServerProgram.Locate("llvm-rc")!.Replace('\\', '/');
                    variables["CMAKE_MT"] = LanguageServerProgram.Locate("llvm-mt")!.Replace('\\', '/');
                }
                var presets = new JsonObject { ["version"] = 6, ["configurePresets"] = new JsonArray(
                    new JsonObject { ["name"] = "base", ["hidden"] = true, ["generator"] = multiConfiguration ? "Ninja Multi-Config" : "Ninja", ["binaryDir"] = "${sourceDir}/preset output/${presetName}", ["cacheVariables"] = variables },
                    new JsonObject { ["name"] = "first", ["inherits"] = "base" },
                    new JsonObject { ["name"] = "selected", ["inherits"] = "base" }) };
                File.WriteAllText(Path.Combine(folder, "CMakePresets.json"), presets.ToJsonString());
                File.AppendAllText(Path.Combine(folder, "CMakeLists.txt"), "if(NOT HEX_PRESET_PROOF STREQUAL \"yes\")\nmessage(FATAL_ERROR \"Preset was ignored\")\nendif()\n");
            }
            explorer.OpenFolder(folder);
            Open(file);
        });
        test.Wait();
        test.WaitFor(() => languages.ServerDiagnosticsOf(document) is { Count: > 0 }, 20f);
        test.Check("CMake clangd: missing project flags are diagnosed before configuration", () => languages.ServerDiagnosticsOf(document) is { Count: > 0 });
        test.Run(() => { debugger.ToggleBreakpoint(file, 3); RunOrPlay(); });
        if (withPresets)
        {
            test.WaitFor(() => textDialog is { IsOpen: true }, 30f);
            test.Check("CMake presets: available configurations open the chooser", () => textDialog is { IsOpen: true } dialog && Descendants(dialog.Body).OfType<ListBox>().Any(list => list.Items.Length == 2));
            test.Run(() =>
            {
                if (textDialog is { IsOpen: true } dialog)
                {
                    var box = Descendants(dialog.Body).OfType<TextBox>().First();
                    box.Text = "selected";
                    box.OnChanged?.Invoke("selected");
                    box.OnSubmitted?.Invoke("selected");
                }
            });
        }
        test.WaitFor(() => textDialog is { IsOpen: true }, 30f);
        test.Check("CMake: executable targets and configurations open the chooser", () => textDialog is { IsOpen: true } dialog && Descendants(dialog.Body).OfType<ListBox>().Any(list => list.Items.Length == (multiConfiguration ? 4 : 2)));
        if (multiConfiguration) testExplorer.TestCTestBeforeBuildSelection(test);
        string selectedTarget = multiConfiguration ? "probe (Checked)" : "probe";
        test.Run(() =>
        {
            if (textDialog is { IsOpen: true } dialog)
            {
                var box = Descendants(dialog.Body).OfType<TextBox>().First();
                box.Text = selectedTarget;
                box.OnChanged?.Invoke(selectedTarget);
            }
        });
        test.Wait();
        test.Run(() =>
        {
            if (textDialog is { IsOpen: true } dialog) Descendants(dialog.Body).OfType<TextBox>().First().OnSubmitted?.Invoke(selectedTarget);
        });
        test.WaitFor(() => debugger.DebugStopped, 45f);
        test.Check("CMake: F5 from a library source builds and stops in the project", () => debugger.DebugStopped && document.Editor.ExecutionLine == 3 && document.Path is { } path && PlatformPaths.Same(path, file));
        test.WaitFor(() => languages.ServerDiagnosticsOf(document) is { Count: 0 }, 20f);
        test.Check("CMake clangd: compilation database resolves include paths and defines", () => languages.ServerDiagnosticsOf(document) is { Count: 0 });
        bool HasValue() => sideTree.Items.Any(section => section.Text == "Variables" && section.Children?.Any(row => row.Text == "value = 42") == true);
        test.WaitFor(HasValue, 10f);
        test.Check("CMake: debugger shows locals from linked library", HasValue);
        test.Run(debugger.StepOver);
        test.WaitFor(() => debugger.DebugStopped && document.Editor.ExecutionLine == 4, 10f);
        test.Check("CMake: Step Over advances in the library source", () => debugger.DebugStopped && document.Editor.ExecutionLine == 4);
        test.Run(debugger.StopDebugging);
        test.WaitFor(() => !debugger.Debugging, 10f);
        test.Check("CMake: Stop ends the session", () => !debugger.Debugging);
        if (withPresets)
            test.Check("CMake presets: selected binary directory used without a default build", () => File.Exists(Path.Combine(folder, "preset output", "selected", "CMakeCache.txt")) && !Directory.Exists(Path.Combine(folder, "build", "hex-debug")));
        if (multiConfiguration)
        {
            test.Check("CMake configurations: only the chosen configuration was built", () => CMakeProject.Targets(Path.Combine(folder, "preset output", "selected"), null)
                .Where(target => target.Name == "probe").All(target => File.Exists(target.Program) == (target.Configuration == "Checked")));
            test.Run(RunOrPlay);
            test.WaitFor(() => debugger.DebugStopped, 30f);
            test.Check("CMake configurations: subsequent F5 remembers the target and configuration", () => debugger.DebugStopped && document.Editor.ExecutionLine == 3 && textDialog is not { IsOpen: true });
            test.Run(debugger.StopDebugging);
            test.WaitFor(() => !debugger.Debugging, 10f);
            testExplorer.TestSelectedCTest(test, folder, file);
        }
        test.Run(() => { debugger.breakpoints.Clear(); CloseDocument(document); });
    }
}
