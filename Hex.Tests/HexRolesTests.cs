using Hex.Core;

using Xunit;

namespace Hex.Tests;

/// <summary>
/// Which command lines of Hex are a window and which are a role with none. A role with none that became a foreground app on a
/// Mac was a Dock icon that came and went with every restart of Hex (the guard, a bridge for each agent, Hex --version).
/// </summary>
public sealed class HexRolesTests
{
    [Theory]
    [InlineData("--dev-guard", "--bin", "/b", "--good", "/g", "--resume", "/r", "--mcp", "--folder", "/f")]
    [InlineData("--mcp-connect", "--folder", "/f", "--agent-chat", "3")]
    [InlineData("--mcp", "--folder", "/f")]
    [InlineData("--harness-hook")]
    [InlineData("--mcp-proxy", "--name", "roslyn", "--folder", "/f", "--agent-chat", "4", "--hex", "77", "--", "server", "--mcp")]
    [InlineData("--lsp")]
    [InlineData("--local-agent", "--models")]
    [InlineData("--version")]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Roles_without_a_window_are_background(params string[] args)
    {
        Assert.True(HexRoles.IsBackground(args));
        Assert.False(HexRoles.IsWindowMode(args));
    }

    [Theory]
    [InlineData]
    [InlineData("/some/folder")]
    [InlineData("file.cs:12:3")]
    [InlineData("--window", "--mcp", "--folder", "/f")]
    [InlineData("--window", "--folder", "/f", "--face", "noon")]
    [InlineData("--dusk")]
    [InlineData("--dusk", "--cwd", "/f")]
    [InlineData("--selftest", "editor-look", "--exit")]
    [InlineData("--selftest", "--mcp")]
    [InlineData("--check", "a.hor")]
    [InlineData("--no-ai", "--fresh")]
    public void The_editor_and_the_terminal_are_window_modes(params string[] args)
    {
        Assert.True(HexRoles.IsWindowMode(args));
        Assert.False(HexRoles.IsBackground(args));
    }

    [Fact]
    public void The_guard_is_only_the_first_argument()
    {
        Assert.False(HexRoles.IsBackground(["--window", "--dev-guard"]));
        Assert.True(HexRoles.IsBackground(["--dev-guard"]));
    }

    [Fact]
    public void The_argument_names_are_the_ones_the_roles_listen_to()
    {
        Assert.Equal(Hex.Dawn.LocalAgentProgram.ARGUMENT, HexRoles.LOCAL_AGENT);
    }
}
