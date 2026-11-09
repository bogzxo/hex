using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hex.Core;

internal static partial class CMakeProject
{
    internal static string CompilationForConfiguration(string build, string configuration)
    {
        string original = Path.Combine(build, "compile_commands.json");
        if (configuration.Length == 0 || !File.Exists(original)) return build;
        var commands = JsonNode.Parse(File.ReadAllText(original))!.AsArray();
        // CMake marks multi-configuration compile commands with CMAKE_INTDIR. Keep
        // the original working directories and flags, and leave CMake's database intact.
        var entries = commands.Select(row => (Row: row!, Configuration: CommandConfiguration(row!))).ToArray();
        if (!entries.Any(entry => entry.Configuration is not null)) return build;
        if (!entries.Any(entry => entry.Configuration == configuration))
            throw new IOException($"CMake has no compilation commands for {configuration}.");
        var selected = new JsonArray(entries.Where(entry => entry.Configuration is null || entry.Configuration == configuration)
            .Select(entry => entry.Row.DeepClone()).ToArray());
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configuration)));
        string directory = Path.Combine(build, ".hex", "clangd", key);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "compile_commands.json");
        string temporary = path + "." + Guid.NewGuid() + ".tmp";
        try
        {
            File.WriteAllText(temporary, selected.ToJsonString());
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return directory;
    }

    private static string? CommandConfiguration(JsonNode row)
    {
        string command = row["command"]?.GetValue<string>()
            ?? string.Join(" ", row["arguments"]?.AsArray().Select(value => value!.GetValue<string>()) ?? []);
        var match = Regex.Match(command, """[-/]DCMAKE_INTDIR=(?:\\?"(?<name>.*?)\\?"|'(?<name>.*?)'|(?<name>[^\s]+))""");
        return match.Success ? match.Groups["name"].Value : null;
    }

    private static string CompilationRecord(string root)
    {
        string normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hex", "CMake", hash + ".txt");
    }

    internal static void RememberCompilationDirectory(string root, string build)
    {
        if (!File.Exists(Path.Combine(build, "compile_commands.json"))) return;
        try
        {
            string record = CompilationRecord(root);
            Directory.CreateDirectory(Path.GetDirectoryName(record)!);
            File.WriteAllText(record, Path.GetFullPath(build));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal static string? CompilationDirectory(string root)
    {
        try
        {
            string record = CompilationRecord(root);
            if (File.Exists(record))
            {
                string selected = File.ReadAllText(record);
                if (Path.IsPathFullyQualified(selected) && File.Exists(Path.Combine(selected, "compile_commands.json"))) return selected;
            }
            return new[] { root, Path.Combine(root, "build"), Path.Combine(root, "build", "hex-debug") }
                .FirstOrDefault(folder => File.Exists(Path.Combine(folder, "compile_commands.json")));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}
