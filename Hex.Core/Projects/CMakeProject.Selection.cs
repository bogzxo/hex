using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed record CMakeBuildSelection(string Directory, string Configuration, string? Preset = null);

internal static partial class CMakeProject
{
    internal static void RememberBuildConfiguration(string root, string build, string configuration, string? preset = null)
    {
        string record = Path.ChangeExtension(CompilationRecord(root), ".build.json");
        Directory.CreateDirectory(Path.GetDirectoryName(record)!);
        var value = new JsonObject { ["directory"] = Path.GetFullPath(build), ["configuration"] = configuration, ["preset"] = preset };
        string temporary = record + "." + Guid.NewGuid() + ".tmp";
        try
        {
            File.WriteAllText(temporary, value.ToJsonString());
            File.Move(temporary, record, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static CMakeBuildSelection? SelectedBuild(string root)
    {
        try
        {
            string record = Path.ChangeExtension(CompilationRecord(root), ".build.json");
            if (!File.Exists(record)) return null;
            var value = JsonNode.Parse(File.ReadAllText(record));
            string? build = value?["directory"]?.GetValue<string>(), configuration = value?["configuration"]?.GetValue<string>();
            if (build is null || configuration is null || !Path.IsPathFullyQualified(build)) return null;
            string cache = Path.Combine(build, "CMakeCache.txt");
            if (!File.Exists(cache)) return null;
            string? home = File.ReadLines(cache).FirstOrDefault(line => line.StartsWith("CMAKE_HOME_DIRECTORY:INTERNAL=", StringComparison.Ordinal));
            if (home is null || !PlatformPaths.Same(home[(home.IndexOf('=') + 1)..], root)) return null;
            return new(build, configuration, value?["preset"]?.GetValue<string>());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException) { return null; }
    }
}
