using Horizon.UI.Components;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestCMakeSwitchPreset(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "switch cmake preset");
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.20)\nproject(SwitchPreset LANGUAGES CXX)\nadd_library(answer STATIC answer.cpp)\ntarget_compile_definitions(answer PRIVATE PRESET_VALUE=${PRESET_VALUE})\n");
            File.WriteAllText(Path.Combine(root, "answer.cpp"), "#if PRESET_VALUE != 42\n#error Unexpected preset\n#endif\nint answer() { return PRESET_VALUE; }\n");
            var variables = new JsonObject {
                ["CMAKE_BUILD_TYPE"] = "Debug", ["PRESET_VALUE"] = "42",
                ["CMAKE_MAKE_PROGRAM"] = CMakeProject.Tool("ninja")!.Replace('\\', '/'),
                ["CMAKE_CXX_COMPILER"] = LanguageServerProgram.Locate("clang++")!.Replace('\\', '/') };
            if (OperatingSystem.IsWindows())
            {
                variables["CMAKE_RC_COMPILER"] = LanguageServerProgram.Locate("llvm-rc")!.Replace('\\', '/');
                variables["CMAKE_MT"] = LanguageServerProgram.Locate("llvm-mt")!.Replace('\\', '/');
            }
            File.WriteAllText(Path.Combine(root, "CMakePresets.json"), new JsonObject {
                ["version"] = 6, ["configurePresets"] = new JsonArray(
                    new JsonObject { ["name"] = "base", ["hidden"] = true, ["generator"] = "Ninja", ["binaryDir"] = "${sourceDir}/output/${presetName}", ["cacheVariables"] = variables },
                    new JsonObject { ["name"] = "first", ["inherits"] = "base" },
                    new JsonObject { ["name"] = "second", ["inherits"] = "base", ["cacheVariables"] = new JsonObject { ["PRESET_VALUE"] = "7" } }) }.ToJsonString());
            explorer.OpenFolder(root);
            Open(Path.Combine(root, "answer.cpp"));
        });
        test.Wait();
        Choose("first", debugger.ConfigureCMake);
        test.WaitFor(() => languages.ServerDiagnosticsOf(document) is { Count: 0 }, 20f);
        test.Check("CMake preset: first preset configures clangd", () => languages.ServerDiagnosticsOf(document) is { Count: 0 });
        Choose("second", debugger.SelectCMakePreset);
        test.WaitFor(() => languages.ServerDiagnosticsOf(document)?.Any(item => item.Message.Contains("Unexpected preset")) == true, 20f);
        test.Check("CMake preset: switching updates compiler definitions", () => languages.ServerDiagnosticsOf(document)?.Any(item => item.Message.Contains("Unexpected preset")) == true);
        test.Run(() => document.Editor.Text = "int answer() { return PRESET_VALUE; }\n");
        test.Wait();
        test.Run(debugger.BuildCMake);
        test.WaitFor(() => !debugger.Debugging, 30f);
        test.Check("CMake preset: build uses newly selected output directory", () => HasLibrary("second") && !HasLibrary("first"));
        Choose("first", debugger.SelectCMakePreset);
        test.Run(debugger.BuildCMake);
        test.WaitFor(() => !debugger.Debugging, 30f);
        test.Check("CMake preset: switching back builds original directory", () => HasLibrary("first"));

        bool HasLibrary(string preset) => Directory.Exists(Path.Combine(root, "output", preset))
            && Directory.EnumerateFiles(Path.Combine(root, "output", preset), "*answer*", SearchOption.AllDirectories).Any(path => Path.GetExtension(path) is ".lib" or ".a");

        void Choose(string preset, Action command)
        {
            test.Run(command);
            test.WaitFor(() => textDialog is { IsOpen: true }, 30f);
            test.Check("CMake preset: chooser excludes hidden base", () => textDialog is { IsOpen: true } dialog && Descendants(dialog.Body).OfType<ListBox>().Any(list => list.Items.Length == 2));
            test.Run(() =>
            {
                if (textDialog is { IsOpen: true } dialog)
                {
                    var box = Descendants(dialog.Body).OfType<TextBox>().First();
                    box.Text = preset;
                    box.OnChanged?.Invoke(preset);
                }
            });
            test.Wait();
            test.Run(() => { if (textDialog is { IsOpen: true } dialog) Descendants(dialog.Body).OfType<TextBox>().First().OnSubmitted?.Invoke(preset); });
            test.WaitFor(() => !debugger.Debugging && CMakeProject.SelectedBuild(root) is { } build && PlatformPaths.Same(build.Directory, Path.Combine(root, "output", preset)), 30f);
            test.Check("CMake preset: selection is persisted for " + preset, () => CMakeProject.SelectedBuild(root) is { } build && build.Preset == preset && PlatformPaths.Same(build.Directory, Path.Combine(root, "output", preset)));
        }
    }
}
