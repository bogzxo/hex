using Hex.Dawn;
using Horizon.UI.Components;

namespace Hex.Tests;

/// <summary>What a build said weighed against what the language server says, and which folders want a restore as they open.</summary>
public class BuildProblemsTests
{
    [Theory]
    [InlineData("C:/project with spaces/main.cpp:12", "C:/project with spaces/main.cpp")]
    [InlineData("main.cpp:12 (/source tree/main.cpp:12)", "/source tree/main.cpp")]
    public void LLVM_linker_references_point_to_source(string reference, string file)
    {
        string output = "lld-link: error: undefined symbol: missing\n>>> referenced by " + reference + "\n>>>               object.obj:(main)\n>>> referenced by " + reference + "\n";
        var problem = Assert.Single(HexBuildProblems.Read(output, 1, "CMakeLists.txt"));
        Assert.Equal(file, problem.File);
        Assert.Equal(12, problem.Line);
        Assert.Equal("undefined symbol: missing", problem.Message);
        Assert.Empty(HexBuildProblems.Read(output, 0, "CMakeLists.txt"));
    }

    [Fact]
    public void Linker_references_do_not_attach_to_unrelated_output()
    {
        const string output = "lld-link: error: undefined symbol: first\nordinary output\n>>> referenced by wrong.cpp:8\nld.lld: error: undefined symbol: second\n>>> referenced by right.cpp:3\n";
        var problems = HexBuildProblems.Read(output, 1, "CMakeLists.txt");
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, item => item.File == "CMakeLists.txt" && item.Message.Contains("first"));
        var problem = Assert.Single(problems, item => item.File == "right.cpp");
        Assert.Equal("right.cpp", problem.File);
        Assert.Contains("second", problem.Message);
    }

    [Fact]
    public async Task Native_LLVM_link_failure_has_a_navigable_source_location()
    {
        string? clang = Environment.GetEnvironmentVariable("HEX_TEST_CLANG");
        if (string.IsNullOrEmpty(clang) || !OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "hex linker source " + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "broken file.cpp"), obj = Path.Combine(root, "broken.obj");
            File.WriteAllText(source, "extern int missing();\nint main() { return missing(); }\n");
            var compile = await Hex.Core.CMakeProject.Run(clang, root, ["-g", "-gcodeview", "-c", source, "-o", obj], default);
            Assert.True(compile.ExitCode == 0, compile.Output);
            string linker = Path.Combine(Path.GetDirectoryName(clang)!, "lld-link.exe");
            var linked = await Hex.Core.CMakeProject.Run(linker, root, ["/entry:main", "/subsystem:console", "/nodefaultlib", obj, "/out:" + Path.Combine(root, "probe.exe")], default);
            Assert.NotEqual(0, linked.ExitCode);
            var problem = Assert.Single(HexBuildProblems.Read(linked.Output, linked.ExitCode, Path.Combine(root, "CMakeLists.txt")));
            Assert.Equal(source, problem.File);
            Assert.Equal(2, problem.Line);
            Assert.Contains("missing", problem.Message);
            string other = Path.Combine(root, "without debug.cpp"), otherObj = Path.Combine(root, "other.obj");
            File.WriteAllText(other, "extern int absent();\nextern \"C\" int other() { return absent(); }\n");
            var withoutDebug = await Hex.Core.CMakeProject.Run(clang, root, ["-g0", "-c", other, "-o", otherObj], default);
            Assert.True(withoutDebug.ExitCode == 0, withoutDebug.Output);
            var mixed = await Hex.Core.CMakeProject.Run(linker, root, ["/entry:main", "/subsystem:console", "/nodefaultlib", "/include:other", obj, otherObj, "/out:" + Path.Combine(root, "probe.exe")], default);
            Assert.NotEqual(0, mixed.ExitCode);
            string project = Path.Combine(root, "CMakeLists.txt");
            var both = HexBuildProblems.Read(mixed.Output, mixed.ExitCode, project);
            Assert.Equal(2, both.Count);
            Assert.Contains(both, item => item.File == source && item.Line == 2 && item.Message.Contains("missing"));
            Assert.Contains(both, item => item.File == project && item.Message.Contains("absent"));

        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CMake_paths_use_the_source_tree_without_rebasing_compiler_paths()
    {
        string root = Path.Combine(Path.GetTempPath(), "source project");
        var problems = HexBuildProblems.Read("CMake Error at cmake/check.cmake:4 (message):\n  Missing SDK\n\n../src/main.cpp:8:3: warning: unused value\n", 1, Path.Combine(root, "CMakeLists.txt"));
        Assert.Contains(problems, problem => problem.Matcher == "cmake" && problem.File == Path.Combine(root, "cmake", "check.cmake") && problem.Line == 4);
        Assert.Contains(problems, problem => problem.Matcher == "gcc" && problem.File == "../src/main.cpp" && problem.Line == 8);
    }

    [Fact]
    public async Task Native_CMake_build_regeneration_error_points_into_source_tree()
    {
        if (Environment.GetEnvironmentVariable("HEX_TEST_CMAKE") != "1") return;
        string cmake = Hex.Core.CMakeProject.Tool("cmake")!, ninja = Hex.Core.CMakeProject.Tool("ninja")!;
        Assert.NotNull(cmake);
        Assert.NotNull(ninja);
        string root = Path.Combine(Path.GetTempPath(), "hex regeneration " + Guid.NewGuid());
        string build = Path.Combine(root, "binary tree"), project = Path.Combine(root, "CMakeLists.txt");
        Directory.CreateDirectory(root);
        try
        {
            const string initial = "cmake_minimum_required(VERSION 3.20)\nproject(Regeneration NONE)\n";
            File.WriteAllText(project, initial);
            var configured = await Hex.Core.CMakeProject.Run(cmake, root,
                ["-S", root, "-B", build, "-G", "Ninja", "-DCMAKE_MAKE_PROGRAM=" + ninja], CancellationToken.None);
            Assert.True(configured.ExitCode == 0, configured.Output);
            File.WriteAllText(project, initial + "message(FATAL_ERROR \"Regeneration fixture failure\")\n");
            var result = await Hex.Core.CMakeProject.Run(cmake, build, ["--build", build], CancellationToken.None);
            Assert.NotEqual(0, result.ExitCode);
            var problem = Assert.Single(HexBuildProblems.Read(result.Output, result.ExitCode, project));
            Assert.Equal(project, problem.File);
            Assert.Equal(3, problem.Line);
            Assert.Contains("Regeneration fixture failure", problem.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("C:\\work with spaces\\global.json")]
    [InlineData("/work/project/global.json")]
    public void Missing_dotnet_sdk_points_to_global_json(string file)
    {
        string output = "      A compatible .NET SDK was not found.\nRequested SDK version: 999.0.100\nglobal.json file: " + file + "\nInstall the [999.0.100] .NET SDK or update [" + file + "] to match an installed SDK.\n";
        var problem = Assert.Single(HexBuildProblems.Read(output, 1, "App.csproj"));
        Assert.Equal(file, problem.File);
        Assert.Contains("999.0.100", problem.Message);
        Assert.Contains("Install the", problem.Message);
        Assert.False(problem.Warning);
        Assert.Empty(HexBuildProblems.Read(output, 0, "App.csproj"));
    }

    [Fact]
    public async Task Real_dotnet_sdk_resolution_failure_reaches_global_json()
    {
        if (Environment.GetEnvironmentVariable("HEX_TEST_DOTNET_SDK") != "1") return;
        string root = Path.Combine(Path.GetTempPath(), "hex missing sdk " + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            string global = Path.Combine(root, "global.json");
            File.WriteAllText(global, "{\"sdk\":{\"version\":\"999.0.100\",\"rollForward\":\"disable\"}}");
            File.WriteAllText(Path.Combine(root, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            var result = await Hex.Core.CMakeProject.Run("dotnet", root, ["build", "App.csproj", "-nologo"], CancellationToken.None);
            Assert.NotEqual(0, result.ExitCode);
            var problem = Assert.Single(HexBuildProblems.Read(result.Output, result.ExitCode, "App.csproj"));
            Assert.Equal(global, problem.File);
            Assert.Contains("999.0.100", problem.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CMake_diagnostics_preserve_locations_paragraphs_and_severity()
    {
        const string output = "CMake Error at C:/project with spaces/cmake/SDK.cmake:12 (find_package):\n  Missing SDK\n\n  Install it first.\nCall Stack (most recent call first):\n  CMakeLists.txt:3 (include)\n\nCMake Warning (dev) at CMakeLists.txt:8 (message):\n  Optional setting is deprecated\n\n-- Configuring incomplete\n";
        var problems = HexBuildProblems.Read(output, 1, "fallback.txt");
        Assert.Equal(2, problems.Count);
        Assert.Equal("C:/project with spaces/cmake/SDK.cmake", problems[0].File);
        Assert.Equal(12, problems[0].Line);
        Assert.Equal("Missing SDK\n\nInstall it first.", problems[0].Message);
        Assert.False(problems[0].Warning);
        Assert.Equal("CMakeLists.txt", problems[1].File);
        Assert.Equal(8, problems[1].Line);
        Assert.True(problems[1].Warning);
        Assert.Equal("Optional setting is deprecated", problems[1].Message);
    }

    [Theory]
    [InlineData("CMake Error in src/CMakeLists.txt:\n  No sources given\n", "src/CMakeLists.txt", "No sources given")]
    [InlineData("CMake Error: Could not create generator\n", "CMakeLists.txt", "Could not create generator")]
    [InlineData("CMake Error at CMakeLists.txt:2:\n  Parse error.\n", "CMakeLists.txt", "Parse error.")]
    public void CMake_errors_without_command_or_location_keep_the_explanation(string output, string file, string message)
    {
        var problem = Assert.Single(HexBuildProblems.Read(output, 1, "CMakeLists.txt"));
        Assert.Equal(file, problem.File);
        Assert.Equal(message, problem.Message);
        Assert.False(problem.Warning);
    }

    [Fact]
    public async Task Native_CMake_configuration_failure_is_navigable()
    {
        if (Environment.GetEnvironmentVariable("HEX_TEST_CMAKE") != "1") return;
        string cmake = Hex.Core.CMakeProject.Tool("cmake")!;
        Assert.NotNull(cmake);
        string root = Path.Combine(Path.GetTempPath(), "hex-cmake-diagnostic-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.20)\nproject(Diagnostic NONE)\nmessage(FATAL_ERROR \"Missing fixture SDK\\nInstall it first\")\n");
            var result = await Hex.Core.CMakeProject.Run(cmake, root, ["-S", root, "-B", Path.Combine(root, "build")], CancellationToken.None);
            Assert.NotEqual(0, result.ExitCode);
            var problem = Assert.Single(HexBuildProblems.Read(result.Output, result.ExitCode, Path.Combine(root, "CMakeLists.txt")));
            Assert.Equal(Path.Combine(root, "CMakeLists.txt"), problem.File);
            Assert.Equal(3, problem.Line);
            Assert.Contains("Missing fixture SDK", problem.Message);
            Assert.Contains("Install it first", problem.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("clang++: error: unknown argument: '--bad-option'")]
    [InlineData("clang-cl.exe: error: no input files")]
    [InlineData("clang++-18: error: linker command failed with exit code 1")]
    [InlineData("lld-link: error: undefined symbol: missing")]
    [InlineData("ld.lld: error: unable to find library -lmissing")]
    [InlineData("g++: fatal error: cannot execute 'cc1plus'")]
    [InlineData("collect2: error: ld returned 1 exit status")]
    public void Native_tool_failures_without_locations_reach_the_build_file(string output)
    {
        var problem = Assert.Single(HexBuildProblems.Read(output, 1, "CMakeLists.txt"));
        Assert.Equal("CMakeLists.txt", problem.File);
        Assert.Equal(output, problem.Message);
        Assert.False(problem.Warning);
    }

    [Theory]
    [InlineData("clang++: warning: argument unused during compilation")]
    [InlineData("PS C:\\project> clang++: error: no input files")]
    [InlineData("program: error: ordinary program output")]
    public void Non_errors_do_not_create_build_fallbacks(string output) =>
        Assert.Empty(HexBuildProblems.Read(output, 1, "CMakeLists.txt"));

    [Fact]
    public void Native_fallback_requires_failure_and_preserves_source_diagnostics()
    {
        const string driver = "clang++: error: linker command failed with exit code 1";
        Assert.Empty(HexBuildProblems.Read(driver, 0, "CMakeLists.txt"));
        var problem = Assert.Single(HexBuildProblems.Read("main.cpp:2:3: error: unknown name\n" + driver, 1, "CMakeLists.txt"));
        Assert.Equal("main.cpp", problem.File);
        Assert.Equal(2, problem.Line);
    }

    [Fact]
    public async Task Installed_clang_driver_errors_reach_build_problems()
    {
        string? compiler = Environment.GetEnvironmentVariable("HEX_TEST_CLANG");
        if (string.IsNullOrEmpty(compiler)) return;
        var info = new System.Diagnostics.ProcessStartInfo(compiler)
        {
            UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true
        };
        info.ArgumentList.Add("--hex-invalid-compiler-option");
        using var process = System.Diagnostics.Process.Start(info)!;
        string output = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains(HexBuildProblems.Read(output, process.ExitCode, "main.cpp"), problem =>
            problem.File == "main.cpp" && !problem.Warning && problem.Message.Contains("--hex-invalid-compiler-option"));
    }

    [Fact]
    public void Native_build_diagnostics_keep_compiler_locations_and_warning_severity()
    {
        var problems = HexBuildProblems.Read("src/main.cpp(9,14): error C2065: 'missing': undeclared identifier\ninclude/api.h:3:5: warning: unused variable 'value'\n", 1, "CMakeLists.txt");
        Assert.Contains(problems, problem => problem.File == "src/main.cpp" && problem.Line == 9 && problem.Column == 14 && !problem.Warning);
        Assert.Contains(problems, problem => problem.File == "include/api.h" && problem.Line == 3 && problem.Column == 5 && problem.Warning);
    }

    [Fact]
    public void Link_errors_without_source_locations_link_to_the_build_file()
    {
        var problems = HexBuildProblems.Read("main.obj : error LNK2019: unresolved external symbol missing referenced in function main\n", 1, "CMakeLists.txt");
        Assert.Contains(problems, problem => problem.File == "CMakeLists.txt" && problem.Line == 1 && !problem.Warning && problem.Message.Contains("LNK2019"));
    }

    [Theory]
    [InlineData("Cannot implicitly convert type 'string' to 'int' (CS0029)", "CS0029")]
    [InlineData("Use 'AsSpan' instead (https://learn.microsoft.com/dotnet/ca1831) (CA1831)", "CA1831")]
    [InlineData("something with no code", "")]
    [InlineData("a function (in brackets)", "")]
    public void A_problem_is_known_by_the_code_at_its_end(string message, string code) =>
        Assert.Equal(code, DawnScene.CodeOf(message));

    [Fact]
    public void A_build_error_the_server_says_too_on_its_line_is_the_servers()
    {
        var built = new HexProblem("A.cs", 129, 46, "Cannot implicitly convert type 'string' to 'int' (CS0029)", false);
        UICodeDiagnostic[] live = [new(129, 46, 5, "Cannot implicitly convert type 'string' to 'int' (CS0029)", false)];

        Assert.True(DawnScene.SaidLive(live, built));
        Assert.False(DawnScene.SaidLive(live, built with { Line = 12 }));
        Assert.False(DawnScene.SaidLive(live, built with { Text = "Dereference of a possibly null reference. (CS8602)" }));
        Assert.False(DawnScene.SaidLive([], built));
        Assert.False(DawnScene.SaidLive(null, built));

        // The server words it differently and puts it a column along, the code and the line are what match
        Assert.True(DawnScene.SaidLive([new(129, 50, 1, "Cannot convert (CS0029)", false)], built));
    }

    [Fact]
    public void A_folder_is_restored_when_a_project_of_its_solution_has_no_assets()
    {
        string folder = Path.Combine(Path.GetTempPath(), "hex restore " + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "Game"));
            Directory.CreateDirectory(Path.Combine(folder, "Engine", "obj"));
            File.WriteAllText(Path.Combine(folder, "Game", "Game.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(folder, "Engine", "Engine.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(folder, "Engine", "obj", HexLanguages.ASSETS), "{}");
            string solution = Path.Combine(folder, "Both.sln");
            File.WriteAllText(solution, """
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Game", "Game\Game.csproj", "{11111111-1111-1111-1111-111111111111}"
                EndProject
                Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "Solution Items", "Solution Items", "{22222222-2222-2222-2222-222222222222}"
                EndProject
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Engine", "Engine\Engine.csproj", "{33333333-3333-3333-3333-333333333333}"
                EndProject
                """);

            Assert.Equal(2, HexLanguages.ProjectsOf(solution).Count);
            Assert.Equal(solution, HexLanguages.Unrestored(folder));

            Directory.CreateDirectory(Path.Combine(folder, "Game", "obj"));
            File.WriteAllText(Path.Combine(folder, "Game", "obj", HexLanguages.ASSETS), "{}");
            Assert.Null(HexLanguages.Unrestored(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void An_slnx_names_its_projects_too()
    {
        string folder = Path.Combine(Path.GetTempPath(), "hex slnx " + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            string solution = Path.Combine(folder, "Game.slnx");
            File.WriteAllText(solution, "<Solution>\n  <Project Path=\"Game/Game.csproj\" />\n  <Folder Name=\"/docs/\" />\n</Solution>\n");
            Assert.Equal(Path.Combine(folder, "Game", "Game.csproj"), Assert.Single(HexLanguages.ProjectsOf(solution)));

            // A project the solution names that isn't there isn't one to restore
            Assert.Null(HexLanguages.Unrestored(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_folder_with_nothing_of_dotnet_wants_no_restore()
    {
        string folder = Path.Combine(Path.GetTempPath(), "hex nothing " + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "readme.md"), "hi");
            Assert.Null(HexLanguages.Unrestored(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
