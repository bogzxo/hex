using Hex.Core;
using Hex.Dawn;
using Horizon.UI.Scripting;

namespace Hex.Tests;

public sealed class ClangdCompilationTests
{
    [Fact]
    public void SelectedBuildIsIndependentOfClangdAndRejectsReusedBuildDirectories()
    {
        string root = Path.Combine(Path.GetTempPath(), "hex-native-selection-" + Guid.NewGuid());
        string build = Path.Combine(root, "build"), clangd = Path.Combine(build, ".hex", "clangd");
        Directory.CreateDirectory(clangd);
        try
        {
            string cache = Path.Combine(build, "CMakeCache.txt");
            File.WriteAllText(cache, "CMAKE_HOME_DIRECTORY:INTERNAL=" + root + "\n");
            File.WriteAllText(Path.Combine(clangd, "compile_commands.json"), "[]");
            CMakeProject.RememberCompilationDirectory(root, clangd);
            CMakeProject.RememberBuildConfiguration(root, build, "Checked");
            Assert.Equal(clangd, CMakeProject.CompilationDirectory(root));
            Assert.Equal(new CMakeBuildSelection(build, "Checked"), CMakeProject.SelectedBuild(root));
            CMakeProject.RememberBuildConfiguration(root, build, "Checked", "local-toolchain");
            Assert.Equal(new CMakeBuildSelection(build, "Checked", "local-toolchain"), CMakeProject.SelectedBuild(root));
            CMakeProject.RememberBuildConfiguration(root, build, "Debug");
            Assert.Equal(new CMakeBuildSelection(build, "Debug"), CMakeProject.SelectedBuild(root));
            File.WriteAllText(cache, "CMAKE_HOME_DIRECTORY:INTERNAL=" + Path.Combine(root, "another-project") + "\n");
            Assert.Null(CMakeProject.SelectedBuild(root));
            File.Delete(cache);
            Assert.Null(CMakeProject.SelectedBuild(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedCompilationCommandsPreserveOriginalsAndRefresh(bool arguments)
    {
        string root = Path.Combine(Path.GetTempPath(), "hex-clangd-config-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            System.Text.Json.Nodes.JsonObject Row(string config, string flag) => new()
            {
                ["directory"] = root, ["file"] = "main.cpp",
                [arguments ? "arguments" : "command"] = arguments
                    ? new System.Text.Json.Nodes.JsonArray("clang++", "-DCMAKE_INTDIR=\"" + config + "\"", flag, "main.cpp")
                    : System.Text.Json.Nodes.JsonValue.Create("clang++ -DCMAKE_INTDIR=\\\"" + config + "\\\" " + flag + " main.cpp")
            };
            string original = Path.Combine(root, "compile_commands.json");
            void Write(string flag) => File.WriteAllText(original, new System.Text.Json.Nodes.JsonArray(Row("Debug", "-DWRONG"), Row("Checked", flag)).ToJsonString());
            Write("-DFIRST");
            string before = File.ReadAllText(original);
            string selected = CMakeProject.CompilationForConfiguration(root, "Checked");
            Assert.Equal(before, File.ReadAllText(original));
            string generated = Path.Combine(selected, "compile_commands.json");
            Assert.Contains("-DFIRST", File.ReadAllText(generated));
            Assert.DoesNotContain("-DWRONG", File.ReadAllText(generated));
            Write("-DUPDATED");
            Assert.Equal(selected, CMakeProject.CompilationForConfiguration(root, "Checked"));
            Assert.Contains("-DUPDATED", File.ReadAllText(generated));
            Assert.Throws<IOException>(() => CMakeProject.CompilationForConfiguration(root, "Absent"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FindsCMakeDatabaseWithoutOverridingExplicitClangdConfiguration()
    {
        string root = Path.Combine(Path.GetTempPath(), "hex-clangd-" + Guid.NewGuid());
        string build = Path.Combine(root, "build", "hex-debug");
        Directory.CreateDirectory(build);
        try
        {
            File.WriteAllText(Path.Combine(build, "compile_commands.json"), "[]");
            var entry = new LanguageServers.Registration("cpp", "clangd", [], "cpp", [".cpp"]);
            Assert.Equal("--compile-commands-dir=" + build, Assert.Single(HexLanguages.ClangdArguments(entry, root)));
            Assert.Empty(HexLanguages.ClangdArguments(entry with { Command = "another-server" }, root));
            Assert.Empty(HexLanguages.ClangdArguments(entry with { Arguments = ["--compile-commands-dir=/chosen"] }, root));
            Assert.Empty(HexLanguages.ClangdArguments(entry with { Arguments = ["-compile-commands-dir", "/chosen"] }, root));
            File.Delete(Path.Combine(build, "compile_commands.json"));
            Assert.Null(CMakeProject.CompilationDirectory(root));
        }
        finally { Directory.Delete(root, true); }
    }
}
