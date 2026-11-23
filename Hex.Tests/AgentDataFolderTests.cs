using Hex.Core;
using Hex.Shell;
using Horizon.HIDL;
using Horizon.UI.Components;

namespace Hex.Tests;

/// <summary>The agent data folder setting: how it's resolved, and that only the user's own settings can change it.</summary>
[Collection(HexHome.NAME)]
public sealed class AgentDataFolderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hex-agent-folder-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        HexData.UseAgentFolder(string.Empty);
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void Empty_means_the_usual_folder()
    {
        string usual = Path.Combine(root, "usual");
        Assert.Equal(usual, HexData.ResolveAgentFolder("  ", usual, out string? problem));
        Assert.Null(problem);
        Assert.Equal(usual, HexData.ResolveAgentFolder("", usual, out _));
    }

    [Fact]
    public void A_folder_is_made_and_variables_are_expanded()
    {
        string usual = Path.Combine(root, "usual");
        string chosen = Path.Combine(root, "chosen", "deep");
        Assert.Equal(chosen, HexData.ResolveAgentFolder(chosen, usual, out string? problem));
        Assert.Null(problem);
        Assert.True(Directory.Exists(chosen));

        Environment.SetEnvironmentVariable("HEX_TEST_AGENT_ROOT", root);
        try
        {
            string viaVariable = HexData.ResolveAgentFolder("%HEX_TEST_AGENT_ROOT%/viavar", usual, out problem);
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "viavar")), viaVariable);
            Assert.Null(problem);
            Assert.True(Directory.Exists(viaVariable));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HEX_TEST_AGENT_ROOT", null);
        }
    }

    [Fact]
    public void A_folder_that_cant_be_made_or_isnt_whole_falls_back_to_the_usual_one()
    {
        string usual = Path.Combine(root, "usual");
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "a-file");
        File.WriteAllText(file, "x");

        Assert.Equal(usual, HexData.ResolveAgentFolder(Path.Combine(file, "inside"), usual, out string? problem));
        Assert.NotNull(problem);

        Assert.Equal(usual, HexData.ResolveAgentFolder("relative/folder", usual, out problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void Every_agent_path_follows_the_setting_and_goes_back_with_it_emptied()
    {
        string usual = HexData.Agents;
        string chosen = Path.Combine(root, "mine");
        HexData.UseAgentFolder(chosen);
        Assert.Equal(chosen, HexData.Agents);
        Assert.Equal(Path.Combine(chosen, "chats"), HexData.AgentChats);
        Assert.Equal(Path.Combine(chosen, "temp", "scratch"), HexData.AgentScratch);
        Assert.Equal(Path.Combine(chosen, "limits.tsv"), HexData.AgentLimits);
        Assert.Null(HexData.AgentFolderProblem);

        HexData.UseAgentFolder(string.Empty);
        Assert.Equal(usual, HexData.Agents);
    }

    [Fact]
    public void A_folders_settings_cant_change_it_and_the_users_own_can()
    {
        Directory.CreateDirectory(root);
        string user = Path.Combine(root, "settings.hor");
        string mine = Path.Combine(root, "mine");
        File.WriteAllText(user, SettingsSections.Write(user, (SettingsSections.EDITOR, new HIDLObject().Add("agent_data_folder", mine))));
        var settings = new HexSettings(user);
        Assert.Equal(mine, settings.AgentDataFolder);

        string project = Path.Combine(root, "project");
        string workspaceFile = HexSettings.WorkspacePathOf(project);
        Directory.CreateDirectory(Path.GetDirectoryName(workspaceFile)!);
        File.WriteAllText(workspaceFile, SettingsSections.Write(workspaceFile, (SettingsSections.EDITOR, new HIDLObject().Add("agent_data_folder", Path.Combine(root, "theirs")))));
        Assert.Null(settings.LoadWorkspace(project));

        Assert.Equal(mine, settings.AgentDataFolder);
        Assert.Empty(settings.WorkspaceValues);
        Assert.False(HexSettings.IsWorkspaceKey("agent_data_folder"));
    }

    [Fact]
    public void Agents_cant_set_it_and_it_shows_under_agents()
    {
        Assert.True(HexSettings.AgentsCannotSet("agent_data_folder"));
        Assert.False(HexSettings.AgentsCannotSet("agents_auto_settle_seconds"));
        var row = EditorSettingsCatalog.Settings.Single(setting => setting.Key == "agent_data_folder");
        Assert.Equal(EditorSettingsCatalog.AGENTS, row.Group);
    }
}
