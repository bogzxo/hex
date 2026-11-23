using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Hex.Core;

// Discord Rich Presence's settings (docs/discord.md). discord_presence is off until the user switches it on, and only the
// user can: a folder's .hex/settings.hor can't turn it on (it isn't a workspace key) and no agent can set it. A folder may
// only say less, discord_show_file, discord_show_workspace and discord_show_language_icon are read from it when they're false.
internal sealed partial class HexSettings
{
    /// <summary>The key that switches Discord presence on, only the person's to change by hand.</summary>
    public const string DISCORD_PRESENCE = "discord_presence";

    /// <summary>The keys of the Discord settings a folder may set, and only to false (to share less).</summary>
    public static readonly string[] DiscordFolderKeys = ["discord_show_file", "discord_show_workspace", "discord_show_language_icon"];

    /// <summary>The files never shown as they come: environment files, secrets folders, keys and certificates.</summary>
    public static readonly string[] UsualDiscordHideGlobs = ["**/.env*", "**/secrets/**", "**/*.pem", "**/*.key", "**/*.pfx", "**/*.p12", "**/id_rsa*", "**/id_ed25519*"];

    /// <summary>Whether Discord shows what is being edited. Off until the user switches it on.</summary>
    public bool DiscordPresence;

    /// <summary>Whether the file's name is shown, "Editing a file" when not.</summary>
    public bool DiscordShowFile = true;

    /// <summary>Whether the workspace's name is shown.</summary>
    public bool DiscordShowWorkspace = true;

    /// <summary>Whether the language's picture is shown.</summary>
    public bool DiscordShowLanguageIcon = true;

    /// <summary>Minutes without input before the presence says Idle, 0 never.</summary>
    public int DiscordIdleMinutes = 10;

    /// <summary>The globs of files never shown by name. Each change is a new array.</summary>
    public string[] DiscordHideGlobs = UsualDiscordHideGlobs;

    /// <summary>Whether a folder's settings may set a key: only the keys that say less, and only to false.</summary>
    internal static bool DiscordFolderMaySet(string key, IRuntimeValue value) =>
        Array.IndexOf(DiscordFolderKeys, key) >= 0 && value is BooleanValue { Value: false };

    private static HexSettingRow[] DiscordRows =>
    [
        Flag(DISCORD_PRESENCE, settings => settings.DiscordPresence, (settings, value) => settings.DiscordPresence = value),
        Flag("discord_show_file", settings => settings.DiscordShowFile, (settings, value) => settings.DiscordShowFile = value),
        Flag("discord_show_workspace", settings => settings.DiscordShowWorkspace, (settings, value) => settings.DiscordShowWorkspace = value),
        Flag("discord_show_language_icon", settings => settings.DiscordShowLanguageIcon, (settings, value) => settings.DiscordShowLanguageIcon = value),
        Whole("discord_idle_minutes", settings => settings.DiscordIdleMinutes, (settings, value) => settings.DiscordIdleMinutes = value, 0.0f, 240.0f),
        Special("discord_hide_globs",
            (settings, values) =>
            {
                if (values.GetValueOrDefault("discord_hide_globs") is ListValue hide) settings.DiscordHideGlobs = Patterns(hide);
            },
            (settings, written) => written.Add("discord_hide_globs", Horizon.HIDL.Library.Natives.List(settings.DiscordHideGlobs)))
    ];
}
