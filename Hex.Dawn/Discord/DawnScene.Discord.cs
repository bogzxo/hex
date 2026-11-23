using System.Numerics;

using Hex.Core.Git;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Discord Rich Presence (docs/discord.md). Once a second the editor takes what it is doing (the file, the workspace, whether
// the terminal has the keyboard, how long since anything happened) and gives it to the presence service, which does
// everything else on its own task. Nothing here waits for Discord. Off until discord_presence is switched on, and with no
// application id (Hex.Core.HexDiscord) it does nothing at all.
internal sealed partial class DawnScene
{
    private DiscordPresenceService? discord;
    private string discordShown = string.Empty;
    private float discordIn;
    private long discordActiveAt = System.Environment.TickCount64;
    private Vector2 discordMouse;
    private int discordVersion = -1;
    private object? discordFocus;
    private string discordWorkspace = string.Empty;
    private long discordWorkspaceSince = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private bool discordExitHooked;

    /// <summary>Whether presence is switched on and there's an application to show it on.</summary>
    internal bool DiscordOn => settings.DiscordPresence && HexDiscord.Configured;

    private void UpdateDiscord(float dt)
    {
        if (!DiscordOn)
        {
            if (discord is not null)
                StopDiscord();
            return;
        }

        // What counts as the person being there: the mouse moved, the text changed, something else has the keyboard
        Vector2 mouse = Engine.Input.Mouse.Position;
        int version = document.IsText ? document.Editor.Document.Version : -1;
        object? focus = compositor.Focus;
        if (mouse != discordMouse || version != discordVersion || !ReferenceEquals(focus, discordFocus))
        {
            discordActiveAt = System.Environment.TickCount64;
            (discordMouse, discordVersion, discordFocus) = (mouse, version, focus);
        }

        if ((discordIn -= dt) > 0.0f)
            return;
        discordIn = 1.0f;

        if (discord is null)
        {
            discord = new DiscordPresenceService(HexDiscord.EffectiveApplicationId(), new DiscordSystemConnector());
            discordShown = string.Empty;
            if (!discordExitHooked)
            {
                discordExitHooked = true;
                AppDomain.CurrentDomain.ProcessExit += (_, _) => StopDiscord();
            }
        }

        DiscordSnapshot snapshot = DiscordNow();
        DiscordPrivacy privacy = DiscordPrivacy.Of(settings);

        // The same words as last time aren't sent again
        DiscordActivity activity = DiscordActivityBuilder.Build(snapshot, privacy, HexAbout.Label);
        string key = $"{activity.Details}|{activity.State}|{activity.LargeImage}|{activity.StartUnixSeconds}";
        if (key == discordShown)
            return;

        discordShown = key;
        discord.Update(activity);
    }

    private DiscordSnapshot DiscordNow()
    {
        string workspace = explorer.workspace.IsEmpty ? string.Empty : explorer.workspace.Name;
        if (workspace != discordWorkspace)
        {
            discordWorkspace = workspace;
            discordWorkspaceSince = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        int idleMinutes = settings.DiscordIdleMinutes;
        bool idle = idleMinutes > 0 && System.Environment.TickCount64 - discordActiveAt > idleMinutes * 60_000L;
        string? path = document.IsText ? document.Path : null;

        DiscordMode mode = idle ? DiscordMode.Idle
            : compositor.Focus is TerminalView ? DiscordMode.Terminal
            : plainShown || path is null ? DiscordMode.Browsing
            : DiscordMode.Editing;

        bool ignored = false;
        bool privateTree = false;
        if (path is not null)
        {
            ignored = git.StateOf(path).Change == GitChange.Ignored;
            foreach (var draft in linkPrivateDrafts.Values)
                privateTree |= PlatformPaths.IsUnder(path, draft.Root);
        }

        return new DiscordSnapshot(mode, path, workspace.Length > 0 ? workspace : null, discordWorkspaceSince, ignored, privateTree, 0);
    }

    /// <summary>Takes the presence down and lets go of Discord. Quick, it runs while Hex is leaving.</summary>
    private void StopDiscord()
    {
        DiscordPresenceService? going = System.Threading.Interlocked.Exchange(ref discord, null);
        if (going is null)
            return;

        try
        {
            going.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1.5));
        }
        catch (Exception e) when (e is AggregateException or ObjectDisposedException)
        {
        }
    }
}
