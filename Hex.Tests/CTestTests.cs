using Hex.Core;

namespace Hex.Tests;

public sealed class CTestTests
{
    [Fact]
    public async Task NativeDiscoveryResultsAndExactSelection()
    {
        if (Environment.GetEnvironmentVariable("HEX_TEST_CMAKE") != "1") return;
        string cmake = CMakeProject.Tool("cmake")!, ctest = CMakeProject.Tool("ctest")!;
        string root = Path.Combine(Path.GetTempPath(), "hex-ctest-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "CMakeLists.txt"), """
cmake_minimum_required(VERSION 3.20)
project(CTestProbe NONE)
enable_testing()
add_test(NAME "pass (one)" COMMAND "${CMAKE_COMMAND}" -E true)
add_test(NAME fail COMMAND "${CMAKE_COMMAND}" -E false)
add_test(NAME skipped COMMAND "${CMAKE_COMMAND}" -E false)
add_test(NAME expected-failure COMMAND "${CMAKE_COMMAND}" -E false)
set_tests_properties(expected-failure PROPERTIES WILL_FAIL TRUE)
add_test(NAME output-rule COMMAND "${CMAKE_COMMAND}" -E echo expected)
set_tests_properties(output-rule PROPERTIES PASS_REGULAR_EXPRESSION expected)
set_tests_properties(skipped PROPERTIES SKIP_RETURN_CODE 1)
set_tests_properties("pass (one)" PROPERTIES ENVIRONMENT "HEX_FIRST=one=two;HEX_SECOND=three" WORKING_DIRECTORY "${CMAKE_SOURCE_DIR}")
""");
            string build = Path.Combine(root, "build");
            var configured = await CMakeProject.Configure(cmake, root, build, default);
            Assert.True(configured.ExitCode == 0, configured.Output);
            var found = await CTest.Discover(ctest, build, "Debug");
            Assert.Equal(5, found.Count);
            var launch = found.Single(test => test.Name == "pass (one)");
            Assert.Equal("one=two", launch.Environment["HEX_FIRST"]);
            Assert.Equal("three", launch.Environment["HEX_SECOND"]);
            Assert.Equal(root.Replace('\\', '/'), launch.WorkingDirectory.Replace('\\', '/'));
            Assert.Equal("true", launch.Command[^1]);
            Assert.Equal("passed", launch.DebugResult(0, 1)!.Outcome);
            Assert.Equal("failed", launch.DebugResult(1, 1)!.Outcome);
            Assert.Null(launch.DebugResult(-1, 1));
            Assert.Equal("skipped", found.Single(test => test.Name == "skipped").DebugResult(1, 1)!.Outcome);
            Assert.Null(found.Single(test => test.Name == "expected-failure").DebugResult(1, 1));
            Assert.Null(found.Single(test => test.Name == "output-rule").DebugResult(0, 1));
            Assert.All(found, test => Assert.Equal(Path.Combine(root, "CMakeLists.txt").Replace('\\', '/'), test.File.Replace('\\', '/')));
            var all = await CTest.Run(ctest, build, "Debug");
            Assert.NotEqual(0, all.ExitCode);
            Assert.Equal(["failed", "passed", "passed", "passed", "skipped"], all.Results.Select(r => r.Outcome).Order().ToArray());
            var selected = await CTest.Run(ctest, build, "Debug", ["pass (one)"]);
            Assert.Equal(0, selected.ExitCode);
            Assert.Equal("pass (one)", Assert.Single(selected.Results).Name);
            Assert.Empty((await CTest.Run(ctest, build, "Debug", [])).Results);
        }
        finally { Directory.Delete(root, true); }
    }
}
