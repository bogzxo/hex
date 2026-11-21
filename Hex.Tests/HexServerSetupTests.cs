using Hex.Core;

namespace Hex.Tests;

/// <summary>A language's steps in the Language Servers window, worked out from what's found, with nothing looked for.</summary>
public sealed class HexServerSetupTests
{
    private static readonly Dictionary<string, HexTool> Tools = new(StringComparer.OrdinalIgnoreCase)
    {
        ["node"] = new("node", "Node.js and npm", "node", ["--version"], "18", "winget install node", "https://nodejs.org", "Runs npm.", []),
        ["rustup"] = new("rustup", "Rust (rustup)", "rustup", ["--version"], "", "winget install rustup", "", "", []),
        ["cargo"] = new("cargo", "Cargo", "cargo", ["--version"], "", "rustup default stable", "", "", ["rustup"]),
        ["shellcheck"] = new("shellcheck", "ShellCheck", "shellcheck", ["--version"], "", "winget install shellcheck", "", "", []),
        ["jdk"] = new("jdk", "Java JDK 21", "java", ["-version"], "21", "", "https://jdk.example", "", []),
    };

    private static HexServerEntry Server(string name, string install, string[]? tools = null, string[]? optional = null) =>
        new(name, name + "-ls", ["--stdio"], name, [".x"], "About " + name, install, install, string.Empty, ["--version"], Tools: tools, ToolsOptional: optional);

    private static List<SetupStep> Steps(HexServerEntry entry, Dictionary<string, SetupFound?> found, SetupFound? server, string? newer = null,
        Dictionary<string, SetupState>? running = null, IReadOnlyList<ProjectFact>? project = null, string releases = "") =>
        HexServerSetup.StepsFor(entry, Tools, name => found.GetValueOrDefault(name), server, newer,
            key => running is not null && running.TryGetValue(key, out SetupState state) ? state : null, project ?? [], releases);

    [Fact]
    public void A_server_from_npm_waits_for_node_and_says_so()
    {
        var steps = Steps(Server("python", "npm install -g pyright", ["node"]), new() { ["node"] = new(null, null) }, new(null, null));

        Assert.Equal(["tool:node", "server:python"], steps.Select(step => step.Key));
        Assert.Equal(SetupState.Needed, steps[0].State);
        Assert.Equal("winget install node", steps[0].Command);
        Assert.Equal(SetupState.Blocked, steps[1].State);
        Assert.Equal("Node.js and npm", steps[1].BlockedBy);
        Assert.Equal(2, HexServerSetup.LeftOf(steps));
    }

    [Fact]
    public void A_tool_older_than_its_minimum_is_outdated_and_one_new_enough_is_ready()
    {
        var old = Steps(Server("python", "npm i", ["node"]), new() { ["node"] = new("C:/node.exe", "v16.20.0") }, new("C:/ls.cmd", "1.0"));
        Assert.Equal(SetupState.Outdated, old[0].State);
        Assert.Equal("v16.20.0", old[0].Detail);

        var fine = Steps(Server("python", "npm i", ["node"]), new() { ["node"] = new("C:/node.exe", "v22.1.0") }, new("C:/ls.cmd", "1.0"));
        Assert.Equal(SetupState.Ready, fine[0].State);
        Assert.Equal(SetupState.Ready, fine[1].State);
        Assert.Equal(0, HexServerSetup.LeftOf(fine));
    }

    [Fact]
    public void The_server_once_its_tools_are_there_is_installed_by_its_command_and_updated_when_behind()
    {
        var needed = Steps(Server("python", "npm install -g pyright", ["node"]), new() { ["node"] = new("node", "v22.0.0") }, new(null, null));
        Assert.Equal(SetupState.Needed, needed[1].State);
        Assert.Equal("npm install -g pyright", needed[1].Command);
        Assert.True(needed[1].Runnable);

        var behind = Steps(Server("python", "npm install -g pyright", ["node"]), new() { ["node"] = new("node", "v22.0.0") }, new("ls", "1.0"), newer: "1.2");
        Assert.Equal(SetupState.Outdated, behind[1].State);
    }

    [Fact]
    public void An_optional_tool_comes_last_and_isnt_counted_as_left()
    {
        var steps = Steps(Server("bash", "npm i", ["node"], ["shellcheck"]), new() { ["node"] = new("node", "v22.0.0"), ["shellcheck"] = new(null, null) }, new("ls", "5"));

        Assert.Equal(["tool:node", "tool:shellcheck", "server:bash"], steps.Select(step => step.Key));
        Assert.Equal(SetupState.Optional, steps[1].State);
        Assert.True(steps[1].Runnable);
        Assert.Equal(0, HexServerSetup.LeftOf(steps));
    }

    [Fact]
    public void A_tool_installed_with_another_comes_after_it_and_waits_for_it()
    {
        var steps = Steps(Server("toml", "cargo install taplo-cli", ["cargo"]), new() { ["rustup"] = new(null, null), ["cargo"] = new(null, null) }, new(null, null));

        Assert.Equal(["tool:rustup", "tool:cargo", "server:toml"], steps.Select(step => step.Key));
        Assert.Equal(SetupState.Needed, steps[0].State);
        Assert.Equal(SetupState.Blocked, steps[1].State);
        Assert.Equal("Rust (rustup)", steps[1].BlockedBy);
        Assert.Equal(SetupState.Blocked, steps[2].State);
    }

    [Fact]
    public void A_tool_with_no_command_for_this_system_is_downloaded_by_hand()
    {
        var steps = Steps(Server("java", string.Empty, ["jdk"]), new() { ["jdk"] = new(null, null) }, new(null, null), releases: "https://jdtls.example");

        Assert.Equal(SetupState.Manual, steps[0].State);
        Assert.Equal("https://jdk.example", steps[0].Download);
        Assert.False(steps[0].Runnable);
        Assert.Equal(SetupState.Blocked, steps[1].State);
    }

    [Fact]
    public void A_server_with_no_install_command_is_manual_with_its_releases()
    {
        var steps = Steps(Server("markdown", string.Empty), [], new(null, null), releases: "https://github.com/x/releases");
        var server = Assert.Single(steps);
        Assert.Equal(SetupState.Manual, server.State);
        Assert.Equal("https://github.com/x/releases", server.Download);
    }

    [Fact]
    public void Whats_going_on_with_a_command_wins_and_a_failure_doesnt_hide_a_step_thats_ready_now()
    {
        var entry = Server("python", "npm i", ["node"]);
        var running = Steps(entry, new() { ["node"] = new(null, null) }, new(null, null), running: new() { ["tool:node"] = SetupState.Running, ["server:python"] = SetupState.Queued });
        Assert.Equal(SetupState.Running, running[0].State);
        Assert.Equal(SetupState.Queued, running[1].State);

        var failed = Steps(entry, new() { ["node"] = new(null, null) }, new(null, null), running: new() { ["tool:node"] = SetupState.Failed });
        Assert.Equal(SetupState.Failed, failed[0].State);
        Assert.True(failed[0].Runnable);

        // Installed by hand after it failed, it's ready, not failed
        var fixedSince = Steps(entry, new() { ["node"] = new("node", "v22.0.0") }, new("ls", "1"), running: new() { ["tool:node"] = SetupState.Failed });
        Assert.Equal(SetupState.Ready, fixedSince[0].State);
    }

    [Fact]
    public void Project_steps_follow_the_server_only_where_they_apply_and_wait_for_the_tools()
    {
        var entry = Server("csharp", "dotnet tool install csharp-ls", ["node"]);
        ProjectFact restore = new("dotnet-restore", "Restore Signal.csproj", "Packages", true, false, "Not restored", "dotnet restore Signal.csproj", "C:/signal");
        ProjectFact cmake = new("compile-commands", "compile_commands.json", "Flags", false, false, string.Empty, "cmake", "C:/signal");

        var blocked = Steps(entry, new() { ["node"] = new(null, null) }, new(null, null), project: [restore, cmake]);
        Assert.Equal(["tool:node", "server:csharp", "project:csharp:dotnet-restore"], blocked.Select(step => step.Key));
        Assert.Equal(SetupState.Blocked, blocked[2].State);

        var needed = Steps(entry, new() { ["node"] = new("node", "v22") }, new("ls", "1"), project: [restore]);
        Assert.Equal(SetupState.Needed, needed[2].State);
        Assert.Equal("C:/signal", needed[2].Folder);

        var done = Steps(entry, new() { ["node"] = new("node", "v22") }, new("ls", "1"), project: [restore with { Done = true }]);
        Assert.Equal(SetupState.Ready, done[2].State);
    }

    [Fact]
    public void Set_up_everything_runs_what_it_can_in_order_and_stops_at_a_download()
    {
        var steps = Steps(Server("java", "install jdtls", ["jdk", "node"]), new() { ["jdk"] = new(null, null), ["node"] = new(null, null) }, new(null, null));
        // The JDK is a download, nothing after it goes
        Assert.Empty(HexServerSetup.RunnableInOrder(steps));

        var python = Steps(Server("python", "npm i", ["node"]), new() { ["node"] = new(null, null) }, new(null, null));
        Assert.Equal(["tool:node"], HexServerSetup.RunnableInOrder(python).Select(step => step.Key));
    }

    [Fact]
    public void A_servers_own_settings_are_saved_and_read_back_in_their_shape()
    {
        string folder = Path.Combine(Path.GetTempPath(), "hex-server-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "settings.hor");
            var settings = new HexSettings(file);
            settings.ServerSettings["html"] = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(
                """{ "html": { "format": { "wrapLineLength": 80, "enable": false }, "suggest": { "html5": true } }, "css": { "lint": ["a", "b"] } }""")!;
            Assert.Null(settings.Save());

            var again = new HexSettings(file);
            var html = again.ServerSettings["html"];
            Assert.Equal("80", html["html"]!["format"]!["wrapLineLength"]!.ToJsonString());
            Assert.False(html["html"]!["format"]!["enable"]!.GetValue<bool>());
            Assert.True(html["html"]!["suggest"]!["html5"]!.GetValue<bool>());
            Assert.Equal(["a", "b"], html["css"]!["lint"]!.AsArray().Select(item => item!.GetValue<string>()));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_catalogs_tools_are_read_with_this_systems_commands_and_every_server_names_tools_that_are_there()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "Assets", "hex", "language_servers.hor");
        if (!File.Exists(file))
            file = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Hex.Dawn", "Assets", "hex", "language_servers.hor"));
        string text = File.ReadAllText(file);
        var tools = HexServerSetup.ReadTools(text);

        Assert.Contains("node", tools.Keys);
        Assert.Equal(OperatingSystem.IsWindows() ? "python" : "python3", tools["python"].Command);
        Assert.Equal(["rustup"], tools["cargo"].Needs);
        foreach (HexServerEntry entry in HexServerCatalog.Read(text))
            foreach (string named in (entry.Tools ?? []).Concat(entry.ToolsOptional ?? []))
                Assert.True(tools.ContainsKey(named), $"{entry.Name} names a tool {named} that isn't in the catalog");
        Assert.Equal(["dotnet"], HexServerCatalog.Read(text).Single(entry => entry.Name == "csharp").Tools!);
    }
}
