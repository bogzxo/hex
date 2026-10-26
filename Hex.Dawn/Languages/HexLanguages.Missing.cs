using Horizon.UI;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

// A file opened whose language server isn't on the machine. Said once a session for each server, in a card that goes by
// itself, with the command that installs it to copy and the settings where it can be pointed at one somewhere else. A
// Go file opened with no gopls was coloured and that was all, nobody knew there was more to be had. Don't show again
// keeps one server quiet for good, and the settings can turn the lot off (HexLanguages.Settings).
internal sealed partial class HexLanguages
{
    // The servers said to be missing this session, by name
    private readonly HashSet<string> missingSaid = new(StringComparer.OrdinalIgnoreCase);

    // Where Don't show again is kept, in the user's settings beside the versions said not to be offered again. A key of
    // its own in front of the server's name so it's never mistaken for a server's skipped version
    internal const string MISSING_QUIET = "missing_quiet_", MISSING_CARDS = "missing_cards";

    /// <summary>Whether a missing server is said at all. The self test says nothing unless a check of it asks.</summary>
    internal bool SayMissing { get; set; } = true;

    /// <summary>What was said of missing servers this session, in order, for the self test.</summary>
    internal List<string> MissingNotices { get; } = [];

    /// <summary>Whether the cards of missing servers are wanted at all, the setting under the servers.</summary>
    internal bool MissingCards
    {
        get => !settings.ServerSkipped.ContainsKey(MISSING_CARDS);
        set
        {
            if (value) settings.ServerSkipped.Remove(MISSING_CARDS);
            else settings.ServerSkipped[MISSING_CARDS] = "off";
        }
    }

    /// <summary>Whether Don't show again was pressed on a server's card, some time, in any session.</summary>
    internal bool MissingQuiet(string name) => settings.ServerSkipped.ContainsKey(MISSING_QUIET + name);

    /// <summary>A server's card not shown again, kept in the settings so the next session says nothing of it either.</summary>
    internal void QuietMissing(string name)
    {
        settings.ServerSkipped[MISSING_QUIET + name] = "quiet";
        context.Settings.Save();
    }

    /// <summary>What this session said forgotten, so a test can play the next session's start.</summary>
    internal void ForgetMissingSaid() => missingSaid.Clear();

    /// <summary>Helper method to say a file's server isn't on the machine, the first time only, when that's why it has none.</summary>
    internal void SayIfMissing(string serverPath)
    {
        if (!SayMissing || !MissingCards || LanguageServers.RegistrationFor(serverPath) is not { } registration || missingSaid.Contains(registration.Name)
            || MissingQuiet(registration.Name))
            return;

        // Started and gone wrong is another thing, HexLanguages.Stopped says that
        if (LanguageServers.IsAvailable(registration.Command))
            return;

        missingSaid.Add(registration.Name);
        string name = registration.Name;
        string command = Path.GetFileNameWithoutExtension(registration.Command);
        HexServerEntry? entry = ServerEntry(name);
        string install = entry is not null && !IsAdded(entry) ? entry.Install : string.Empty;

        // Every card has the same three, how it's got, the settings and Don't show again. One with no command line to copy
        // (marksman, serve-d) is downloaded, its releases opened the way Install in the settings does
        (string Label, Action Pressed) getIt = install.Length > 0
            ? (context.Strings.Said("code_server.copy_install"), () =>
            {
                UIClipboard.Copy(install);
                context.Notices.Say(context.Strings.Said("code_server.install_copied", ("install", install)));
            })
            : (context.Strings.Said("code_server.get_it"), () =>
            {
                if (entry is not null) InstallServer(entry);
                else context.Windows.OpenSettings(SERVER_SECTION);
            });
        (string Label, Action Pressed) settingsButton = (context.Strings.Said("code_server.open_settings"), () => context.Windows.OpenSettings(SERVER_SECTION));
        (string Label, Action Pressed) quiet = (context.Strings.Said("code_server.dont_show_again"), () => QuietMissing(name));

        string said = install.Length > 0
            ? context.Strings.Said("code_server.missing", ("command", command), ("install", install))
            : context.Strings.Said("code_server.missing_no_install", ("command", command));
        context.Notices.Notify(said, error: false, getIt, settingsButton, quiet);
        MissingNotices.Add(said);
    }
}
