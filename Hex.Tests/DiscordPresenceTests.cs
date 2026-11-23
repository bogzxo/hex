using Hex.Core;
using Hex.Shell;
using Horizon.HIDL;
using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Tests;

/// <summary>What Discord is told (the privacy rules), when it's told (the pacing), and who may switch it on (the settings).</summary>
public sealed class DiscordPresenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hex-discord-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }

    private static readonly DiscordPrivacy Open = new(true, true, true, HexSettings.UsualDiscordHideGlobs);

    private static DiscordSnapshot Editing(string path, bool ignored = false, bool privateTree = false, int peers = 0) =>
        new(DiscordMode.Editing, path, "hex", 1_700_000_000, ignored, privateTree, peers);

    [Fact]
    public void A_file_is_shown_by_its_name_and_language_never_its_path()
    {
        DiscordActivity activity = DiscordActivityBuilder.Build(Editing(@"C:\work\secret-project\src\Program.cs"), Open, "3.4.7");
        Assert.Equal("Editing Program.cs", activity.Details);
        Assert.Equal("in hex", activity.State);
        Assert.Equal("cs", activity.LargeImage);
        Assert.Equal("C#", activity.LargeText);
        Assert.Equal("hex", activity.SmallImage);
        Assert.Equal("Hex 3.4.7", activity.SmallText);
        Assert.Equal(1_700_000_000, activity.StartUnixSeconds);
        Assert.Equal(HexDiscord.Website, activity.ButtonUrl);
        foreach (string? said in new[] { activity.Details, activity.State, activity.LargeText, activity.SmallText })
            Assert.DoesNotContain("secret-project", said);
    }

    [Fact]
    public void An_unknown_language_falls_back_to_hexs_picture()
    {
        DiscordActivity activity = DiscordActivityBuilder.Build(Editing("/a/b/notes.zzz"), Open, "1");
        Assert.Equal("hex", activity.LargeImage);
        Assert.Equal("Editing notes.zzz", activity.Details);
    }

    [Fact]
    public void The_switches_say_less()
    {
        var snapshot = Editing("/a/Program.cs");
        DiscordActivity noFile = DiscordActivityBuilder.Build(snapshot, Open with { ShowFile = false }, "1");
        Assert.Equal("Editing a file", noFile.Details);
        Assert.Equal("cs", noFile.LargeImage);

        Assert.Null(DiscordActivityBuilder.Build(snapshot, Open with { ShowWorkspace = false }, "1").State);
        Assert.Equal("hex", DiscordActivityBuilder.Build(snapshot, Open with { ShowLanguageIcon = false }, "1").LargeImage);
    }

    [Theory]
    [InlineData("/work/app/.env")]
    [InlineData("/work/app/.env.local")]
    [InlineData(@"C:\work\app\secrets\keys.json")]
    [InlineData("/work/app/server.pem")]
    public void Files_the_hide_globs_match_are_not_named(string path)
    {
        DiscordActivity activity = DiscordActivityBuilder.Build(Editing(path), Open, "1");
        Assert.Equal("Editing a file", activity.Details);
        Assert.Equal("hex", activity.LargeImage);
    }

    [Fact]
    public void Ignored_files_and_files_in_a_private_worktree_are_not_named()
    {
        Assert.Equal("Editing a file", DiscordActivityBuilder.Build(Editing("/a/Program.cs", ignored: true), Open, "1").Details);
        Assert.Equal("Editing a file", DiscordActivityBuilder.Build(Editing("/a/Program.cs", privateTree: true), Open, "1").Details);
        var custom = Open with { HideGlobs = ["**/private/**"] };
        Assert.Equal("Editing a file", DiscordActivityBuilder.Build(Editing("/a/private/x.cs"), custom, "1").Details);
        Assert.Equal("Editing x.cs", DiscordActivityBuilder.Build(Editing("/a/public/x.cs"), custom, "1").Details);
    }

    [Fact]
    public void Other_modes_say_what_they_are_and_link_peers_are_counted()
    {
        Assert.Equal("In the terminal", DiscordActivityBuilder.Build(new DiscordSnapshot(DiscordMode.Terminal, "/a/x.cs", "w", null, false, false, 0), Open, "1").Details);
        Assert.Equal("Browsing files", DiscordActivityBuilder.Build(new DiscordSnapshot(DiscordMode.Browsing, null, "w", null, false, false, 0), Open, "1").Details);
        Assert.Equal("Idle", DiscordActivityBuilder.Build(new DiscordSnapshot(DiscordMode.Idle, "/a/x.cs", "w", null, false, false, 0), Open, "1").Details);
        Assert.Equal("in hex with 2 others", DiscordActivityBuilder.Build(Editing("/a/x.cs", peers: 2), Open, "1").State);
        Assert.Equal("in hex with 1 other", DiscordActivityBuilder.Build(Editing("/a/x.cs", peers: 1), Open, "1").State);
        Assert.Null(DiscordActivityBuilder.Build(new DiscordSnapshot(DiscordMode.Editing, "/a/x.cs", null, null, false, false, 0), Open, "1").State);
    }

    [Fact]
    public void Pacing_connects_with_backoff_and_sends_the_latest_at_most_every_fifteen_seconds()
    {
        var pacing = new DiscordPacing();
        Assert.False(pacing.ShouldConnect(0));
        pacing.Offer();
        Assert.True(pacing.ShouldConnect(0));

        // Discord isn't there: 5 s, 10 s, 20 s ... to 2 minutes
        long now = 0;
        long[] waits = [5_000, 10_000, 20_000, 40_000, 80_000, 120_000, 120_000];
        foreach (long expected in waits)
        {
            pacing.OnConnectFailed(now);
            Assert.Equal(expected, pacing.Backoff);
            Assert.False(pacing.ShouldConnect(now + expected - 1));
            Assert.True(pacing.ShouldConnect(now + expected));
            now += expected;
        }

        // A connection sends at once, then waits out the 15 s, and what is sent is whatever came last
        pacing.OnConnected();
        Assert.Equal(0, pacing.Backoff);
        Assert.True(pacing.ShouldSend(now));
        pacing.OnSent(now);
        pacing.Offer();
        pacing.Offer();
        Assert.False(pacing.ShouldSend(now + 14_999));
        Assert.Equal(15_000, pacing.WaitMs(now));
        Assert.True(pacing.ShouldSend(now + 15_000));
        pacing.OnSent(now + 15_000);
        Assert.False(pacing.Pending);
        Assert.Null(pacing.WaitMs(now + 15_001));

        // Lost, tried again in 5 s, and the state sent again
        pacing.OnLost(now + 20_000);
        Assert.False(pacing.ShouldConnect(now + 24_999));
        Assert.True(pacing.ShouldConnect(now + 25_000));
        pacing.OnConnected();
        Assert.True(pacing.ShouldSend(now + 25_000));
    }

    [Fact]
    public void Switching_off_when_not_connected_has_nothing_to_take_down()
    {
        var pacing = new DiscordPacing();
        pacing.Offer();
        pacing.Drop();
        Assert.False(pacing.ShouldConnect(0));
        Assert.Null(pacing.WaitMs(0));
    }

    // The settings

    private HexSettings UserSettings(params (string Key, IRuntimeValue Value)[] user)
    {
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "settings.hor");
        var editor = new HIDLObject();
        foreach (var (key, value) in user)
            editor.Add(key, value);
        File.WriteAllText(file, SettingsSections.Write(file, (SettingsSections.EDITOR, editor)));
        return new HexSettings(file);
    }

    private static string Project(string root, HIDLObject editor)
    {
        string folder = Path.Combine(root, "project");
        string file = HexSettings.WorkspacePathOf(folder);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, SettingsSections.Write(file, (SettingsSections.EDITOR, editor)));
        return folder;
    }

    [Fact]
    public void Presence_is_off_and_everything_else_on_until_somebody_says_otherwise()
    {
        var settings = new HexSettings(null);
        Assert.False(settings.DiscordPresence);
        Assert.True(settings.DiscordShowFile && settings.DiscordShowWorkspace && settings.DiscordShowLanguageIcon);
        Assert.Equal(10, settings.DiscordIdleMinutes);
        Assert.Contains("**/.env*", settings.DiscordHideGlobs);
        Assert.Contains("**/secrets/**", settings.DiscordHideGlobs);
        Assert.False(((BooleanValue)EditorSettingsCatalog.Usual["discord_presence"]).Value);
    }

    [Fact]
    public void A_folders_settings_cant_switch_presence_on_but_can_show_less()
    {
        HexSettings settings = UserSettings();
        string folder = Project(root, new HIDLObject()
            .Add("discord_presence", true).Add("discord_show_file", false).Add("discord_show_workspace", false).Add("discord_hide_globs", Natives.List(["x"])));
        Assert.Null(settings.LoadWorkspace(folder));

        Assert.False(settings.DiscordPresence);
        Assert.False(settings.DiscordShowFile);
        Assert.False(settings.DiscordShowWorkspace);
        Assert.True(settings.DiscordShowLanguageIcon);
        Assert.DoesNotContain("x", settings.DiscordHideGlobs);

        // and the folder letting go gives the user's own back
        Assert.Null(settings.LoadWorkspace(null));
        Assert.True(settings.DiscordShowFile);
        Assert.True(settings.DiscordShowWorkspace);
    }

    [Fact]
    public void A_folder_cant_switch_a_hidden_thing_back_on()
    {
        HexSettings settings = UserSettings(("discord_show_file", Values.Bool(false)));
        string folder = Project(root, new HIDLObject().Add("discord_show_file", true));
        Assert.Null(settings.LoadWorkspace(folder));
        Assert.False(settings.DiscordShowFile);
    }

    [Fact]
    public void Agents_cant_set_presence_and_the_rows_are_in_the_discord_section()
    {
        Assert.True(HexSettings.AgentsCannotSet(HexSettings.DISCORD_PRESENCE));
        Assert.False(HexSettings.AgentsCannotSet("discord_show_file"));
        Assert.False(HexSettings.IsWorkspaceKey(HexSettings.DISCORD_PRESENCE));
        foreach (string key in new[] { "discord_presence", "discord_show_file", "discord_show_workspace", "discord_show_language_icon", "discord_idle_minutes", "discord_hide_globs" })
            Assert.Equal(EditorSettingsCatalog.DISCORD, EditorSettingsCatalog.Settings.Single(setting => setting.Key == key).Group);
    }

    [Fact]
    public void The_settings_are_kept_in_the_file()
    {
        HexSettings settings = UserSettings(("discord_presence", Values.Bool(true)), ("discord_idle_minutes", new NumberValue(99999.0f)),
            ("discord_hide_globs", Natives.List(["**/a/**"])));
        Assert.True(settings.DiscordPresence);
        Assert.Equal(240, settings.DiscordIdleMinutes);
        Assert.Equal(["**/a/**"], settings.DiscordHideGlobs);
    }
}
