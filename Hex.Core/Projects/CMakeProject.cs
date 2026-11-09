using System.Diagnostics;
using System.Text.Json.Nodes;
using Horizon.UI.Scripting;

namespace Hex.Core;

internal sealed record CMakeTarget(string Name, string Program, string Configuration);
internal sealed record CMakeResult(int ExitCode, string Output, string? StandardOutput = null);

internal static partial class CMakeProject
{
    internal static string? RootOf(string file, IReadOnlyList<string> roots)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(file))!;
        string? boundary = roots.Where(root => PlatformPaths.IsUnder(folder, root)).OrderByDescending(root => root.Length).FirstOrDefault();
        string? found = null;
        for (string? here = folder; here is not null; here = Path.GetDirectoryName(here))
        {
            if (File.Exists(Path.Combine(here, "CMakeLists.txt")))
            {
                found = here;
                if (boundary is null) break;
            }
            if (boundary is not null && Path.GetFullPath(here).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(boundary).TrimEnd(Path.DirectorySeparatorChar), PlatformPaths.Comparison)) break;
        }
        return found;
    }

    internal static string? Tool(string name)
    {
        if (LanguageServerProgram.Locate(name) is { } onPath) return onPath;
        if (!OperatingSystem.IsWindows()) return null;
        string exe = name + ".exe";
        string standalone = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CMake", "bin", exe);
        if (File.Exists(standalone)) return standalone;
        foreach (string programFiles in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            string vs = Path.Combine(programFiles, "Microsoft Visual Studio");
            if (!Directory.Exists(vs)) continue;
            foreach (string version in Directory.EnumerateDirectories(vs).OrderDescending())
            foreach (string edition in Directory.EnumerateDirectories(version))
            {
                string path = Path.Combine(edition, "Common7", "IDE", "CommonExtensions", "Microsoft", "CMake", name == "ninja" ? "Ninja" : "CMake/bin", exe);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    internal static async Task<CMakeResult> Configure(string cmake, string root, string build, CancellationToken cancel)
    {
        string query = Path.Combine(build, ".cmake", "api", "v1", "query");
        Directory.CreateDirectory(query);
        File.WriteAllText(Path.Combine(query, "codemodel-v2"), "");
        List<string> args = ["-S", root, "-B", build, "-DCMAKE_BUILD_TYPE=Debug", "-DCMAKE_EXPORT_COMPILE_COMMANDS=ON"];
        if (!File.Exists(Path.Combine(build, "CMakeCache.txt")) && Tool("ninja") is { } ninja)
        {
            args.AddRange(["-G", "Ninja", "-DCMAKE_MAKE_PROGRAM=" + ninja.Replace('\\', '/')]);
            if (LanguageServerProgram.Locate("clang") is { } c) args.Add("-DCMAKE_C_COMPILER=" + c.Replace('\\', '/'));
            if (LanguageServerProgram.Locate("clang++") is { } cpp) args.Add("-DCMAKE_CXX_COMPILER=" + cpp.Replace('\\', '/'));
            if (OperatingSystem.IsWindows() && LanguageServerProgram.Locate("llvm-rc") is { } rc) args.Add("-DCMAKE_RC_COMPILER=" + rc.Replace('\\', '/'));
            if (OperatingSystem.IsWindows() && LanguageServerProgram.Locate("llvm-mt") is { } mt) args.Add("-DCMAKE_MT=" + mt.Replace('\\', '/'));
        }
        return await Run(cmake, root, args, cancel);
    }

    private static JsonArray ConfigurationModel(string build)
    {
        string reply = Path.Combine(build, ".cmake", "api", "v1", "reply");
        string index = Directory.EnumerateFiles(reply, "index-*.json").OrderDescending(StringComparer.Ordinal).First();
        JsonNode data = JsonNode.Parse(File.ReadAllText(index))!;
        string modelFile = data["reply"]?["codemodel-v2"]?["jsonFile"]?.GetValue<string>() ?? throw new IOException("CMake did not return an executable target model.");
        JsonNode model = JsonNode.Parse(File.ReadAllText(Path.Combine(reply, modelFile)))!;
        return model["configurations"]!.AsArray();
    }

    internal static IReadOnlyList<string> Configurations(string build) => ConfigurationModel(build)
        .Select(configuration => configuration!["name"]!.GetValue<string>()).ToArray();

    internal static IReadOnlyList<CMakeTarget> Targets(string build, string? configuration = "Debug")
    {
        string reply = Path.Combine(build, ".cmake", "api", "v1", "reply");
        var configurations = ConfigurationModel(build);
        JsonNode? selected = configuration is null ? null : configurations.FirstOrDefault(c => c?["name"]?.GetValue<string>() == configuration)
            ?? configurations.FirstOrDefault(c => c?["name"]?.GetValue<string>() == "")
            ?? (configurations.Count == 1 ? configurations[0] : null)
            ?? throw new IOException($"CMake has no {configuration} configuration.");
        List<CMakeTarget> targets = [];
        foreach (JsonNode current in selected is null ? configurations.Select(c => c!) : [selected])
        foreach (JsonNode? entry in current["targets"]!.AsArray())
        {
            JsonNode target = JsonNode.Parse(File.ReadAllText(Path.Combine(reply, entry!["jsonFile"]!.GetValue<string>())))!;
            if (target["type"]?.GetValue<string>() != "EXECUTABLE") continue;
            string? artifact = target["artifacts"]?.AsArray().FirstOrDefault()?["path"]?.GetValue<string>();
            if (artifact is not null) targets.Add(new(target["name"]!.GetValue<string>(), Path.GetFullPath(artifact, build), current["name"]!.GetValue<string>()));
        }
        return targets.OrderBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Configuration, StringComparer.Ordinal).ToArray();
    }

    internal static Task<CMakeResult> Build(string cmake, string root, string build, CMakeTarget target, CancellationToken cancel) =>
        Run(cmake, root, ["--build", build, "--config", target.Configuration.Length == 0 ? "Debug" : target.Configuration, "--target", target.Name], cancel);

    internal static Task<CMakeResult> BuildAll(string cmake, string root, string build, string configuration, CancellationToken cancel) =>
        Run(cmake, root, ["--build", build, "--config", configuration.Length == 0 ? "Debug" : configuration], cancel);

    internal static async Task<CMakeResult> Run(string cmake, string root, IEnumerable<string> args, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(cmake) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Could not start CMake.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(TimeSpan.FromMinutes(10));
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            cancel.ThrowIfCancellationRequested();
            throw new TimeoutException("CMake exceeded ten minutes.");
        }
        string output = await stdout;
        return new(process.ExitCode, output + await stderr, output);
    }
}
