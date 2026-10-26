using Horizon.UI;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

// A file opened whose language server isn't on the machine. Said once a session for each server, in a card that goes by
// itself, with the command that installs it to copy and the settings where it can be pointed at one somewhere else. A
// Go file opened with no gopls was coloured and that was all, nobody knew there was more to be had.
internal sealed partial class HexLanguages
{
    // The servers said to be missing this session, by name
    private readonly HashSet<string> missingSaid = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a missing server is said at all. The self test says nothing unless a check of it asks.</summary>
    internal bool SayMissing { get; set; } = true;

    /// <summary>What was said of missing servers this session, in order, for the self test.</summary>
    internal List<string> MissingNotices { get; } = [];

    /// <summary>Helper method to say a file's server isn't on the machine, the first time only, when that's why it has none.</summary>
    private void SayIfMissing(string serverPath)
    {
        if (!SayMissing || LanguageServers.RegistrationFor(serverPath) is not { } registration || missingSaid.Contains(registration.Name))
            return;

        // Started and gone wrong is another thing, HexLanguages.Stopped says that
        if (LanguageServers.IsAvailable(registration.Command))
            return;

        missingSaid.Add(registration.Name);
        string command = Path.GetFileNameWithoutExtension(registration.Command);
        string install = ServerEntry(registration.Name) is { } entry && !IsAdded(entry) ? entry.Install : string.Empty;
        (string Label, Action Pressed) settingsButton = (context.Strings.Said("code_server.open_settings"), () => context.Windows.OpenSettings(SERVER_SECTION));

        string said;
        if (install.Length > 0)
        {
            said = context.Strings.Said("code_server.missing", ("command", command), ("install", install));
            context.Notices.Notify(said, error: false,
                (context.Strings.Said("code_server.copy_install"), () =>
                {
                    UIClipboard.Copy(install);
                    context.Notices.Say(context.Strings.Said("code_server.install_copied", ("install", install)));
                }),
                settingsButton);
        }
        else
        {
            said = context.Strings.Said("code_server.missing_no_install", ("command", command));
            context.Notices.Notify(said, error: false, settingsButton);
        }

        MissingNotices.Add(said);
    }
}
