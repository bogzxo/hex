using Horizon.HIDL;
using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Dusk.Tests;

/// <summary>
/// The plugins Dusk loads, the terminal's own out of the engine (Assets/terminal) and Dusk's (Assets/dusk), run in a
/// bare HIDL runtime against an object that has exactly the names the real one has in Dusk (<see cref="DuskScene.ApiNames"/>),
/// under both its names, and only writes down what it was asked. Every plugin has to load, every command it registers
/// has to run through without asking for anything the object hasn't got, and every command a keys file names has to
/// be one of them.
/// </summary>
public class PluginTests
{
    private static readonly string Engine = Path.Combine(AppContext.BaseDirectory, "Assets", "terminal");
    private static readonly string Dusk = Path.Combine(AppContext.BaseDirectory, "Assets", "dusk");

    private static IEnumerable<string> Folders() =>
        Directory.GetDirectories(Path.Combine(Engine, "plugins")).Concat(Directory.GetDirectories(Path.Combine(Dusk, "plugins")))
            .Where(folder => File.Exists(Path.Combine(folder, TerminalPlugin.FILE))).Order();

    /// <summary>A terminal object that keeps what a plugin did with it.</summary>
    private sealed class Recording
    {
        public readonly Dictionary<string, (string Title, IRuntimeValue Function, string Key)> Commands = [];
        public readonly List<string> Called = [];
        public ObjectValue Object { get; }

        public Recording()
        {
            var members = new List<(string, IRuntimeValue)>();
            foreach (string name in DuskScene.ApiNames)
            {
                string called = name;
                IRuntimeValue value = name switch
                {
                    "command" => Natives.Action("command", args =>
                    {
                        Called.Add(called);
                        Commands[args.Text(0)] = (args.Text(1), args.Callable(2), args.Text(3, string.Empty));
                    }),
                    "themes" => Natives.Function("themes", _ => Natives.List(["dark", "light", "horizon"])),
                    "profiles" => Natives.Function("profiles", _ => Natives.List(["pwsh", "cmd", "wsl:Ubuntu", "hidl"])),
                    "profile_name" => Natives.Function("profile_name", args => new StringValue(args.Text(0).ToUpperInvariant())),
                    "setting" => Natives.Function("setting", _ => Values.Bool(true)),
                    "plugin_folder" or "host" or "version" or "platform" => new StringValue(name),
                    _ => Natives.Function(name, _ =>
                    {
                        Called.Add(called);
                        return Values.Null;
                    })
                };
                members.Add((name, value));
            }

            Object = Natives.Object([.. members]);
        }
    }

    private static (HIDLRuntime Runtime, Recording Terminal) Load(string folder)
    {
        var terminal = new Recording();
        var runtime = new HIDLRuntime { BaseDirectory = folder, Output = null };
        runtime.UserScope.DeclareSystem("terminal", terminal.Object);
        runtime.UserScope.DeclareSystem("dusk", terminal.Object);
        runtime.RunFile(Path.Combine(folder, TerminalPlugin.FILE));
        return (runtime, terminal);
    }

    public static TheoryData<string> Plugins()
    {
        var data = new TheoryData<string>();
        foreach (string folder in Folders())
            data.Add(folder);
        return data;
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void Every_plugin_loads_and_says_what_it_is(string folder)
    {
        var (runtime, terminal) = Load(folder);

        Assert.True(runtime.UserScope.Variables.TryGetValue("plugin", out IRuntimeValue? about) && about is ObjectValue);
        var said = (ObjectValue)about!;
        Assert.True(said.Properties.ContainsKey("name"));
        Assert.True(said.Properties.ContainsKey("description"));
        Assert.NotEmpty(terminal.Commands);
        Assert.True(TerminalFiles.IsTerminalFile(Path.Combine(folder, TerminalPlugin.FILE)), "a plugin says #define dusk, so Hex knows it");
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void Every_command_of_a_plugin_runs_on_the_object_it_will_get(string folder)
    {
        var (runtime, terminal) = Load(folder);
        foreach (var (name, (_, function, key)) in terminal.Commands)
        {
            var exception = Record.Exception(() => runtime.Invoke(function, terminal.Object));
            Assert.True(exception is null, $"{Path.GetFileName(folder)}: {name} went wrong, {exception?.Message}");
            Assert.True(key.Length == 0 || TerminalShortcut.Read(key) is not null, $"{Path.GetFileName(folder)}: {name} asks for {key}, which isn't a key");
        }
    }

    [Fact]
    public void Every_command_a_keys_file_names_is_registered_and_every_key_reads()
    {
        var registered = new HashSet<string>();
        foreach (string folder in Folders())
            registered.UnionWith(Load(folder).Terminal.Commands.Keys);

        foreach (string file in new[] { Path.Combine(Engine, "keys.hor"), Path.Combine(Dusk, "keys.hor") })
        {
            foreach (var (name, key) in TerminalCommands.ReadKeysText(File.ReadAllText(file)))
            {
                Assert.True(registered.Contains(name), $"{file} names {name}, which no plugin registers");
                Assert.True(key.Length == 0 || TerminalShortcut.Read(key) is not null, $"{file} puts {name} on {key}, which isn't a key");
            }
        }
    }

    [Fact]
    public void No_two_commands_with_a_key_share_one()
    {
        var keyed = new Dictionary<string, string>();
        foreach (string folder in Folders())
        {
            foreach (var (name, (_, _, key)) in Load(folder).Terminal.Commands)
            {
                if (key.Length == 0)
                    continue;

                string tidy = TerminalShortcut.Tidy(key)!;
                Assert.False(keyed.TryGetValue(tidy, out string? other), $"{name} and {other} are both on {tidy}");
                keyed[tidy] = name;
            }
        }
    }

    [Fact]
    public void The_profiles_plugin_makes_a_command_of_every_profile()
    {
        var (_, terminal) = Load(Path.Combine(Engine, "plugins", "profiles"));
        Assert.Contains("new_tab_wsl:Ubuntu", terminal.Commands.Keys);
        Assert.Contains("new_tab_hidl", terminal.Commands.Keys);
        Assert.Equal("New tab: CMD", terminal.Commands["new_tab_cmd"].Title);
    }

    [Fact]
    public void Dusk_adds_to_the_object_only_names_every_program_hasnt_got()
    {
        Assert.Empty(DuskScene.DuskApiFunctions.Select(function => function.Name).Intersect(TerminalHost.CoreApiNames));
        Assert.Equal(DuskScene.ApiNames.Length, DuskScene.ApiNames.Distinct().Count());
    }
}
