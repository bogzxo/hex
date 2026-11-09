using System.Text.Json.Nodes;
using Hex.Core;
using Horizon.UI.Scripting;

namespace Hex.Tests;

public sealed class CMakePresetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CMakeResolvesIncludedInheritedUserPresetsAndTheirOutputDirectory(bool multiConfiguration)
    {
        if (Environment.GetEnvironmentVariable("HEX_TEST_CMAKE") != "1") return;
        string cmake = Assert.IsType<string>(CMakeProject.Tool("cmake"));
        string root = Path.Combine(Path.GetTempPath(), "hex preset " + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.20)\nproject(PresetProbe LANGUAGES CXX)\nif(NOT PRESET_PROOF STREQUAL \"from included base\")\nmessage(FATAL_ERROR \"Preset was ignored\")\nendif()\nadd_executable(probe main.cpp)\n");
            File.WriteAllText(Path.Combine(root, "main.cpp"), "int main() { return 0; }\n");
            var variables = new JsonObject { ["CMAKE_BUILD_TYPE"] = "RelWithDebInfo", ["PRESET_PROOF"] = "from included base",
                ["CMAKE_MAKE_PROGRAM"] = CMakeProject.Tool("ninja")!.Replace('\\', '/'),
                ["CMAKE_CXX_COMPILER"] = LanguageServerProgram.Locate("clang++")!.Replace('\\', '/') };
            if (OperatingSystem.IsWindows())
            {
                variables["CMAKE_RC_COMPILER"] = LanguageServerProgram.Locate("llvm-rc")!.Replace('\\', '/');
                variables["CMAKE_MT"] = LanguageServerProgram.Locate("llvm-mt")!.Replace('\\', '/');
            }
            if (multiConfiguration) variables["CMAKE_CONFIGURATION_TYPES"] = "Debug;RelWithDebInfo";
            var included = new JsonObject { ["version"] = 6, ["configurePresets"] = new JsonArray(new JsonObject {
                ["name"] = "base", ["hidden"] = true, ["generator"] = multiConfiguration ? "Ninja Multi-Config" : "Ninja", ["binaryDir"] = "${sourceDir}/$env{HEX_PRESET_FOLDER}",
                ["environment"] = new JsonObject { ["HEX_PRESET_FOLDER"] = "chosen output" }, ["cacheVariables"] = variables }) };
            File.WriteAllText(Path.Combine(root, "included.json"), included.ToJsonString());
            File.WriteAllText(Path.Combine(root, "CMakePresets.json"), """
                { "version": 6, "include": ["included.json"], "configurePresets": [
                    { "name": "disabled", "inherits": "base", "condition": false }
                ] }
                """);
            File.WriteAllText(Path.Combine(root, "CMakeUserPresets.json"), """
                { "version": 6, "configurePresets": [
                    { "name": "local-debug", "displayName": "Local symbols", "inherits": "base" }
                ] }
                """);
            var preset = Assert.Single(await CMakeProject.Presets(cmake, root, default));
            Assert.Equal("local-debug", preset.Name);
            Assert.Equal("Local symbols", preset.DisplayName);
            var configured = await CMakeProject.ConfigurePreset(cmake, root, preset.Name, default);
            Assert.True(configured.Result.ExitCode == 0, configured.Result.Output);
            Assert.Equal(Path.Combine(root, "chosen output"), configured.BuildDirectory);
            var targets = CMakeProject.Targets(configured.BuildDirectory!, configuration: null);
            Assert.Equal(multiConfiguration ? 2 : 1, targets.Count);
            var target = Assert.Single(targets, t => t.Configuration == "RelWithDebInfo");
            Assert.Equal("RelWithDebInfo", target.Configuration);
            CMakeProject.RememberBuildConfiguration(root, configured.BuildDirectory!, target.Configuration, preset.Name);
            Assert.Equal(new CMakeBuildSelection(configured.BuildDirectory!, target.Configuration, preset.Name), CMakeProject.SelectedBuild(root));
            string originalCommands = File.ReadAllText(Path.Combine(configured.BuildDirectory!, "compile_commands.json"));
            string compilation = CMakeProject.CompilationForConfiguration(configured.BuildDirectory!, target.Configuration);
            Assert.Equal(originalCommands, File.ReadAllText(Path.Combine(configured.BuildDirectory!, "compile_commands.json")));
            var command = Assert.Single(JsonNode.Parse(File.ReadAllText(Path.Combine(compilation, "compile_commands.json")))!.AsArray())!;
            Assert.Equal(configured.BuildDirectory!.Replace('\\', '/'), command["directory"]!.GetValue<string>().Replace('\\', '/'));
            if (multiConfiguration)
            {
                Assert.NotEqual(configured.BuildDirectory, compilation);
                Assert.Contains("RelWithDebInfo", command["command"]!.GetValue<string>());
                Assert.DoesNotContain("CMAKE_INTDIR=\\\"Debug", command["command"]!.GetValue<string>());
            }
            else Assert.Equal(configured.BuildDirectory, compilation);
            var built = await CMakeProject.Build(cmake, root, configured.BuildDirectory!, target, default);
            Assert.True(built.ExitCode == 0, built.Output);
            Assert.True(File.Exists(target.Program));
            if (multiConfiguration) Assert.False(File.Exists(targets.Single(t => t.Configuration == "Debug").Program));
            Assert.False(Directory.Exists(Path.Combine(root, "build", "hex-debug")));
            var again = await CMakeProject.ConfigurePreset(cmake, root, preset.Name, default);
            Assert.True(again.Result.ExitCode == 0, again.Result.Output);
            File.WriteAllText(Path.Combine(root, "CMakeUserPresets.json"), "invalid json");
            await Assert.ThrowsAsync<IOException>(() => CMakeProject.Presets(cmake, root, default));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
