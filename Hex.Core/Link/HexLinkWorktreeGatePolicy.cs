using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed record HexLinkWorktreeGatePolicy(string Command, string? WorkingFolder, string? TestCommand, string? TestFilter, int TimeoutSeconds)
{
    public const int DefaultTimeout = 300;
    public HexLinkWorktreeGateOptions Options => new(Command, TestFilter, WorkingFolder, TestCommand, TimeoutSeconds);

    public JsonObject Json() => new()
    {
        ["command"] = Command, ["directory"] = WorkingFolder, ["test_command"] = TestCommand,
        ["test_filter"] = TestFilter, ["timeout_seconds"] = TimeoutSeconds
    };

    public static HexLinkWorktreeGatePolicy Of(JsonObject value)
    {
        string command = HexLinkData.String(value, "command", 4001) ?? string.Empty;
        string? folder = HexLinkData.String(value, "directory", HexLinkFiles.MOST_PATH + 1);
        string? test = HexLinkData.String(value, "test_command", 4001);
        string? filter = HexLinkData.String(value, "test_filter", 1001);
        int seconds = HexLinkData.Int(value, "timeout_seconds") ?? DefaultTimeout;
        if (string.IsNullOrWhiteSpace(command) || command.Length > 4000 || test?.Length > 4000 ||
            filter?.Length > 1000 || folder?.Length > HexLinkFiles.MOST_PATH || seconds is < 1 or > 3600 ||
            (test is not null && filter is not null) ||
            (value["timeout_seconds"] is not null && HexLinkData.Int(value, "timeout_seconds") is null) ||
            (value["directory"] is not null && folder is null) || (value["test_command"] is not null && test is null) ||
            (value["test_filter"] is not null && filter is null))
            throw HexLinkException.Invalid("Enter a build command, at most one test command or filter, and a timeout from 1 to 3600 seconds.");
        if (folder is { Length: > 0 } && folder != "." &&
            (folder.Contains('\\') || folder.Contains(':') || folder.Any(char.IsControl) ||
             folder.Split('/').Any(part => part.Length == 0 || part is "." or "..")))
            throw HexLinkException.Invalid("Use a folder relative to the shared workspace, with forward slashes.");
        return new(command, folder is "" or "." ? null : folder, test, filter, seconds);
    }
}

internal static class HexLinkWorktreeGatePolicyFile
{
    public static HexLinkWorktreeGatePolicy? Read(string file, HexLinkFolder folder, HexLinkAuthority owner) =>
        Values(file)[Key(folder, owner)] switch
        {
            null => null,
            JsonObject policy => HexLinkWorktreeGatePolicy.Of(policy),
            _ => throw new IOException("The stored Link worktree gate is invalid.")
        };

    public static void Save(string file, HexLinkFolder folder, HexLinkAuthority owner, HexLinkWorktreeGatePolicy? policy)
    {
        JsonObject values = Values(file);
        string key = Key(folder, owner);
        if (policy is null)
            values.Remove(key);
        else
            values[key] = policy.Json();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        HexLinkFiles.WriteAtomic(file, Encoding.UTF8.GetBytes(values.ToJsonString()));
    }

    private static string Key(HexLinkFolder folder, HexLinkAuthority owner)
    {
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Path));
        if (PlatformPaths.Comparison == StringComparison.OrdinalIgnoreCase)
            path = path.ToUpperInvariant();
        return $"{owner.DeviceId}:{owner.Epoch}:{path}";
    }

    private static JsonObject Values(string file)
    {
        try
        {
            using var read = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonNode.Parse(read) as JsonObject ?? throw new IOException("The Link worktree gate settings are invalid.");
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
    }
}
