using System.Diagnostics;
using Hex.Core;

namespace Hex.Tests;

public sealed class CMakeProjectTests
{
    [Fact]
    public void FindsTheProjectRootWithoutCrossingWorkspaceBoundary()
    {
        string root = Path.Combine(Path.GetTempPath(), "hex-cmake-root-" + Guid.NewGuid());
        string child = Path.Combine(root, "library");
        Directory.CreateDirectory(child);
        try
        {
            File.WriteAllText(Path.Combine(root, "CMakeLists.txt"), "");
            File.WriteAllText(Path.Combine(child, "CMakeLists.txt"), "");
            string source = Path.Combine(child, "answer.cpp");
            Assert.Equal(root, CMakeProject.RootOf(source, [root]));
            Assert.Equal(child, CMakeProject.RootOf(source, [child]));
            Assert.Equal(child, CMakeProject.RootOf(source, []));
            File.Delete(Path.Combine(child, "CMakeLists.txt"));
            Assert.Null(CMakeProject.RootOf(source, [child]));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DiscoversAndBuildsTheRealExecutableArtifact()
    {
        if (Environment.GetEnvironmentVariable("HEX_TEST_CMAKE") != "1") return;
        string cmake = Assert.IsType<string>(CMakeProject.Tool("cmake"));
        string root = Path.Combine(Path.GetTempPath(), "hex cmake " + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.20)\nproject(HexProbe LANGUAGES CXX)\nadd_library(answer STATIC answer.cpp)\nadd_executable(probe main.cpp)\ntarget_link_libraries(probe PRIVATE answer)\nset_target_properties(probe PROPERTIES OUTPUT_NAME actual-program RUNTIME_OUTPUT_DIRECTORY \"${CMAKE_BINARY_DIR}/output folder\")\n");
            File.WriteAllText(Path.Combine(root, "answer.cpp"), "int answer() { return 42; }\n");
            File.WriteAllText(Path.Combine(root, "main.cpp"), "#include <cstdio>\nint answer();\nint main() { std::printf(\"%d\\n\", answer()); }\n");
            string build = Path.Combine(root, "build");
            var configured = await CMakeProject.Configure(cmake, root, build, default);
            Assert.True(configured.ExitCode == 0, configured.Output);
            var target = Assert.Single(CMakeProject.Targets(build));
            Assert.Equal("probe", target.Name);
            Assert.Contains("actual-program", target.Program);
            Assert.Contains("output folder", target.Program);
            var built = await CMakeProject.Build(cmake, root, build, target, default);
            Assert.True(built.ExitCode == 0, built.Output);
            using var process = Process.Start(new ProcessStartInfo(target.Program) { UseShellExecute = false, RedirectStandardOutput = true });
            Assert.NotNull(process);
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("42", output.Trim());
            File.WriteAllText(Path.Combine(root, "answer.cpp"), "this is not C++;");
            Assert.NotEqual(0, (await CMakeProject.Build(cmake, root, build, target, default)).ExitCode);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CMakeProject.Build(cmake, root, build, target, cancelled.Token));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
