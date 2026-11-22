using System.Text.Json.Nodes;

using Hex.Core;

namespace Hex.Tests;

public sealed class HexLinkWorktreeGateTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "link-gate-" + Guid.NewGuid().ToString("N")[..12]);

    private void Add(string relative)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
    }

    [Fact]
    public void Multi_repo_defaults_choose_the_nearest_projects_and_skip_the_superproject()
    {
        Add("WholeWorkspace.sln");
        Add("hex/Hex.sln");
        Add("hex/docs/_linkpass/proj/Small.csproj");
        Add("horizon/Horizon.csproj");
        var folder = new HexLinkFolder("workspace", root);
        string[] repositories = ["", "hex", "horizon"];
        var gates = HexLinkWorktreeGateSelection.Defaults(folder, repositories,
            ["workspace/hex/docs/_linkpass/proj/Class1.cs", "workspace/hex/docs/_linkpass/proj/Other.cs",
             "workspace/hex/README.md", "workspace/horizon/lib/Core.cs"], _ => null);

        Assert.Equal(["Hex.sln", "Small.csproj", "Horizon.csproj"], gates.Select(gate => gate.Target));
        Assert.Equal("hex/docs/_linkpass/proj", gates[1].WorkingFolder);
        Assert.Throws<IOException>(() => HexLinkWorktreeGateSelection.Defaults(folder, repositories, ["workspace/README.md"], _ => null));
    }

    [Fact]
    public void Newly_added_projects_are_selected_from_the_candidate()
    {
        Add("shared/hex/Hex.sln");
        Add("candidate/hex/Hex.sln");
        Add("candidate/hex/docs/_linkpass/proj/Small.csproj");
        var folder = new HexLinkFolder("workspace", Path.Combine(root, "shared"));
        var selected = HexLinkWorktreeGateSelection.Defaults(folder, ["", "hex"],
            ["workspace/hex/docs/_linkpass/proj/Small.csproj", "workspace/hex/docs/_linkpass/proj/Class.cs"],
            _ => null, Path.Combine(root, "candidate"));
        Assert.Equal("Small.csproj", Assert.Single(selected).Target);
    }

    [Fact]
    public void A_nested_build_task_takes_priority_over_its_projects()
    {
        Add("nested/Local.sln");
        Add("nested/lib/Local.csproj");
        var task = new HexLinkWorktreeGateOptions(Build: "echo nested gate", WorkingFolder: "nested");
        var selected = HexLinkWorktreeGateSelection.Defaults(new("work", root), ["", "nested"],
            ["work/nested/lib/Foo.cs", "work/nested/Bar.cs"],
            directory => directory == Path.Combine(root, "nested") ? task : null);
        Assert.Equal(task, Assert.Single(selected));
    }

    [Fact]
    public void Configured_gate_is_bound_to_the_folder_and_authority_epoch()
    {
        string file = Path.Combine(root, "settings", "gates.json");
        var folder = new HexLinkFolder("work", Path.Combine(root, "workspace"));
        var pin = new HexLinkAuthority(new string('A', 64), 1);
        var policy = HexLinkWorktreeGatePolicy.Of(new JsonObject
        {
            ["command"] = "dotnet build", ["directory"] = "hex/docs/_linkpass/proj",
            ["test_command"] = "dotnet test", ["timeout_seconds"] = 30
        });
        HexLinkWorktreeGatePolicyFile.Save(file, folder, pin, policy);
        Assert.Equal(policy, HexLinkWorktreeGatePolicyFile.Read(file, folder, pin));
        Assert.Null(HexLinkWorktreeGatePolicyFile.Read(file, folder, pin with { Epoch = 2 }));
        var invalid = policy.Json();
        invalid["directory"] = "../outside";
        Assert.Throws<HexLinkException>(() => HexLinkWorktreeGatePolicy.Of(invalid));
        HexLinkWorktreeGatePolicyFile.Save(file, folder, pin, null);
        Assert.Null(HexLinkWorktreeGatePolicyFile.Read(file, folder, pin));
    }

    [Fact]
    public async Task The_paired_replica_gets_changes_to_the_authoritys_gate()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View, HexLinkRights.View);
        var folder = Assert.Single(pair.B.Workspace.Folders);
        var pin = new HexLinkAuthority(pair.B.Device.Id, 2);
        pair.B.Workspace = pair.B.Workspace with { Folders = [folder with { Authority = pin }] };
        var policy = new HexLinkWorktreeGatePolicy("dotnet build", "hex/docs/_linkpass/proj", null, "Name=Small", 45);
        pair.B.GateFor = each => each.Authority == pin ? policy : null;
        await pair.PeerB.RefreshWorkspace(CancellationToken.None);

        HexLinkFolder remote = Assert.Single(pair.PeerB.Workspace.Folders);
        Assert.Equal(pin, remote.Authority);
        Assert.Equal(policy, remote.WorktreeGate);
        Assert.False(HexLinkAllowed.May(HexLinkRights.View, "hex_link_worktree_gate"));
        Assert.True(HexLinkAllowed.May(HexLinkRights.View | HexLinkRights.Edit, "hex_link_worktree_gate"));

        pair.B.GateFor = null;
        await pair.PeerB.RefreshWorkspace(CancellationToken.None);
        Assert.Null(Assert.Single(pair.PeerB.Workspace.Folders).WorktreeGate);
    }

    [Fact]
    public async Task Gate_uses_the_configured_directory_runs_its_test_command_and_reports_timeout()
    {
        Add("narrow/marker.txt");
        string log = Path.Combine(root, "run.log");
        string check = OperatingSystem.IsWindows() ? "if (!(Test-Path marker.txt)) { exit 6 }" : "test -f marker.txt";
        var gate = new HexLinkWorktreeGateOptions(Build: check, WorkingFolder: "narrow", TestCommand: "exit 7");
        var failed = await HexLinkWorktreeGate.Run(root, log, gate, CancellationToken.None);
        Assert.Equal(7, failed.ExitCode);
        Assert.Contains("narrow", Assert.Single(failed.Ran));

        string sleep = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 20" : "sleep 20";
        var timedOut = await HexLinkWorktreeGate.Run(root, log, gate with { Build = sleep, TimeoutSeconds = 1 }, CancellationToken.None);
        Assert.Equal(124, timedOut.ExitCode);
        Assert.Contains("timed out", timedOut.Output);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
