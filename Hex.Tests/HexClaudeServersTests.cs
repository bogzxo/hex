using System.Text.Json.Nodes;

using Hex.Dawn;

namespace Hex.Tests;

/// <summary>
/// The MCP servers a Claude Code agent is given when Hex says them all (--strict-mcp-config): the user's own, their
/// project's for the folder and the folder's .mcp.json, Hex's own left for the extension to write.
/// </summary>
public sealed class HexClaudeServersTests : IDisposable
{
    private readonly string home = Directory.CreateTempSubdirectory("hex-claude-home").FullName;
    private readonly string folder = Directory.CreateTempSubdirectory("hex-claude-folder").FullName;

    public void Dispose()
    {
        File.Delete(Path.Combine(home, ".claude.json"));
        File.Delete(Path.Combine(folder, ".mcp.json"));
        Directory.Delete(home);
        Directory.Delete(folder);
    }

    [Fact]
    public void The_users_servers_their_projects_and_the_folders_are_taken_later_over_earlier()
    {
        string key = folder.Replace('\\', '/').ToUpperInvariant();
        File.WriteAllText(Path.Combine(home, ".claude.json"), $$"""
            {
              "mcpServers": { "rea": { "command": "node" }, "shared": { "command": "user" }, "hex": { "command": "old" } },
              "projects": { "{{key}}": { "mcpServers": { "project": { "command": "p" } } }, "C:/elsewhere": { "mcpServers": { "other": { "command": "o" } } } }
            }
            """);
        File.WriteAllText(Path.Combine(folder, ".mcp.json"), """{ "mcpServers": { "shared": { "command": "folder" } } }""");

        JsonObject servers = HexExtensions.ClaudeServers(home, folder);

        Assert.Equal(["rea", "shared", "project"], servers.Select(pair => pair.Key));
        Assert.Equal("folder", servers["shared"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void Nothing_to_read_is_no_servers()
    {
        File.WriteAllText(Path.Combine(home, ".claude.json"), "{ not json");
        Assert.Empty(HexExtensions.ClaudeServers(home, folder));
    }
}
