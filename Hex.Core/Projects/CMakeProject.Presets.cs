using System.Text.RegularExpressions;

namespace Hex.Core;

internal sealed record CMakePreset(string Name, string DisplayName);
internal sealed record CMakeConfigured(CMakeResult Result, string? BuildDirectory);

internal static partial class CMakeProject
{
    internal static bool HasPresets(string root) => File.Exists(Path.Combine(root, "CMakePresets.json")) || File.Exists(Path.Combine(root, "CMakeUserPresets.json"));

    // Let CMake evaluate includes, inheritance, macros and conditions. Reading the JSON directly would
    // offer hidden/disabled presets and easily disagree with the selected toolchain's CMake version.
    internal static async Task<IReadOnlyList<CMakePreset>> Presets(string cmake, string root, CancellationToken cancel)
    {
        var result = await Run(cmake, root, ["--list-presets=configure"], cancel);
        if (result.ExitCode != 0) throw new IOException(result.Output);
        List<CMakePreset> presets = [];
        foreach (string line in result.Output.Split('\n'))
        {
            var match = Regex.Match(line.TrimEnd('\r'), "^  \"([^\"]+)\"(?:\\s+-\\s+(.*))?$");
            if (match.Success)
            {
                string name = match.Groups[1].Value;
                presets.Add(new(name, match.Groups[2].Success ? match.Groups[2].Value : name));
            }
        }
        return presets;
    }

    internal static async Task<CMakeConfigured> ConfigurePreset(string cmake, string root, string preset, CancellationToken cancel)
    {
        string[] args = ["--preset", preset, "--log-level=STATUS", "-DCMAKE_EXPORT_COMPILE_COMMANDS=ON"];
        var configured = await Run(cmake, root, args, cancel);
        if (configured.ExitCode != 0) return new(configured, null);
        // CMake reports the fully expanded binary directory after generation, including user presets
        // and inherited environment macros. Do not override binaryDir or the preset's cache variables.
        const string prefix = "-- Build files have been written to: ";
        string? path = configured.Output.Split('\n').LastOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal));
        if (path is null) throw new IOException("CMake did not report the preset's build directory.");
        string build = Path.GetFullPath(path[prefix.Length..].TrimEnd('\r'), root);
        string cache = Path.Combine(build, "CMakeCache.txt");
        string? source = File.ReadLines(cache).FirstOrDefault(line => line.StartsWith("CMAKE_HOME_DIRECTORY:INTERNAL=", StringComparison.Ordinal));
        if (source is null || !PlatformPaths.Same(source[(source.IndexOf('=') + 1)..], root))
            throw new IOException("The preset's build directory does not belong to this CMake project.");
        string query = Path.Combine(build, ".cmake", "api", "v1", "query", "codemodel-v2");
        if (!File.Exists(query))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(query)!);
            File.WriteAllText(query, "");
            var queried = await Run(cmake, root, args, cancel);
            configured = new(queried.ExitCode, configured.Output + queried.Output);
        }
        return new(configured, build);
    }
}
