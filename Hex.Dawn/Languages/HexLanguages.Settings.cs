using Hex.Shell;

using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The language servers' section of the settings (Editor, Language servers). Whether Hex looks for new versions, Check
// now, and a row a server of language_servers.hor, where it is (empty for its command on the PATH), what it is and how
// it is installed, and Choose, Install or update (a download for one there's no command line for) and What changed.
// Under them every server with what was found of it, where, which version and whether there is a newer one, written
// again as that changes, and the way to add one of your own (HexLanguages.Editor).
internal sealed partial class HexLanguages
{
    internal const string SERVER_SECTION = "Editor · Language servers";

    // What a server's row is keyed by, after this its name
    internal const string SERVER_KEY = "server_path_";

    // What a server's buttons say, which is what a press is told apart by. Download says the same as Install now, the two
    // were different widths and threw the rows' buttons out of line, and the self test still asks for it by that name
    internal const string CHOOSE = "Choose...", INSTALL = "Install or update", DOWNLOAD = INSTALL, CHANGES = "What changed";

    // The line a server has under the rows, by its name, while the settings are up
    internal readonly Dictionary<string, Label> serverStatus = new(StringComparer.OrdinalIgnoreCase);

    // How far the installs that are going had got when the lines under the rows last said
    private int installsShown;

    /// <summary>Every tick, while the settings are up, the lines of the servers being installed say how it's going.</summary>
    private void UpdateInstallStatus()
    {
        if (serverStatus.Count == 0)
            return;

        int now = 0;
        foreach (HexServerEntry entry in serverEntries)
        {
            if (Installing(entry) is { } going)
                now += going.Version + 1;
        }

        if (now != installsShown)
        {
            installsShown = now;
            RefreshServerStatus();
        }
    }

    /// <summary>What each server's line under the rows says, written again, while the settings are up.</summary>
    private void RefreshServerStatus()
    {
        foreach (HexServerEntry entry in ServerEntries)
        {
            if (serverStatus.TryGetValue(entry.Name, out Label? line))
                line.Text = ServerStatusOf(entry);
        }
    }

    /// <summary>
    /// A server's line in the list, a dot of how it is (there, being installed or behind, missing), the language, the
    /// program and its files, and what's known of it, quietly. What isn't there isn't an error, it was red, and the
    /// commands that install them were chips of code wrapping across the page, a wall of them.
    /// </summary>
    private string ServerStatusOf(HexServerEntry entry)
    {
        string files = UIRichText.MarkdownLiteral(string.Join(" ", entry.Extensions));
        string Line(string dot, string said) =>
            $"[color:{dot}]$(circle-filled)[/color]  **{UIRichText.MarkdownLiteral(LanguageNameOf(entry))}**  {UIRichText.MarkdownLiteral(entry.Command)}  [color:dim]{files}  ·  {said}[/color]";

        if (Installing(entry) is { } going)
            return Line("warning", $"{UIRichText.MarkdownLiteral(going.Title.ToLowerInvariant())}{(going.Progress is { } progress and < 1.0f ? $" {progress:P0}" : "...")} {UIRichText.MarkdownLiteral(going.Message)}");
        if (serversProbing.Contains(entry.Name) || !serversFound.TryGetValue(entry.Name, out ServerFound? found))
            return Line("dim", context.Strings.Said("language_settings.looking"));

        string latest = settings.ServerLatest.TryGetValue(entry.Name, out string? newest) ? newest : string.Empty;
        if (found.Path is null)
        {
            string how = entry.Install.Length > 0 ? context.Strings.Said("language_settings.installs_it", ("command", UIRichText.MarkdownLiteral(entry.Install)))
                : context.Strings.Said("language_settings.download_from_releases");
            return Line("dim", $"{context.Strings.Said("language_settings.not_found")}, {how}");
        }

        string version = found.Version is { } said ? UIRichText.MarkdownLiteral(said) : context.Strings.Said("language_settings.version_unsaid");
        if (NewerServerVersion(entry) is { } newer)
            return Line("warning", $"{version}, {context.Strings.Said("language_settings.newer_out", ("version", UIRichText.MarkdownLiteral(newer)))}") + PathLine(found.Path);

        string news = latest.Length > 0 && found.Version is not null ? ", " + context.Strings.Said("language_settings.the_newest") : string.Empty;
        return Line("success", version + news) + PathLine(found.Path);
    }

    // Where a server that's there is, quiet under its line, lined up with its name behind a dot nobody sees. Its extension
    // the way the file has it, found by the PATH's own it came out shouting ".EXE"
    private static string PathLine(string path)
    {
        string extension = Path.GetExtension(path);
        string shown = extension.Length > 1 && extension == extension.ToUpperInvariant() ? path[..^extension.Length] + extension.ToLowerInvariant() : path;
        return $"\n[color:#00000000]$(circle-filled)[/color]  [color:dim]{UIRichText.MarkdownLiteral(shown)}[/color]";
    }

    /// <summary>The servers' section, its rows read from and put in force on the editor.</summary>
    private sealed class ServerSource : ISettingsSource
    {
        private readonly HexLanguages languages;

        public ServerSource(HexLanguages languages)
        {
            this.languages = languages;
            Settings =
            [
                new("server_updates", HexText.Get("language_settings.updates"), UIFormKind.Flag,
                    HexText.Get("language_settings.updates_help"),
                    SERVER_SECTION, Buttons: [HexText.Get("language_settings.check_now")]),
                .. languages.ServerEntries.Select(entry => new UISetting(SERVER_KEY + entry.Name, entry.Command, UIFormKind.Text, HelpOf(entry), SERVER_SECTION,
                    Buttons: entry.Repo.Length > 0 ? [CHOOSE, INSTALL, CHANGES] : [CHOOSE, INSTALL],
                    Placeholder: HexText.Get("language_settings.on_the_path", ("command", entry.Command))))
            ];
        }

        private static string HelpOf(HexServerEntry entry)
        {
            string how = entry.Install.Length > 0 ? HexText.Get("language_settings.installed_with", ("command", entry.Install))
                : entry.Repo.Length > 0 ? HexText.Get("language_settings.downloaded_from", ("repo", entry.Repo)) : string.Empty;
            return $"{entry.About} {how} {HexText.Get("language_settings.where_help", ("command", entry.Command))}".Trim();
        }

        public string Section => SettingsSections.EDITOR;

        public IReadOnlyList<UISetting> Settings { get; }

        private HexServerEntry? EntryOf(string key) => key.StartsWith(SERVER_KEY, StringComparison.Ordinal) ? languages.ServerEntry(key[SERVER_KEY.Length..]) : null;

        public IRuntimeValue? Get(string key)
        {
            if (key == "server_updates")
                return Values.Bool(languages.settings.ServerUpdates);
            return EntryOf(key) is { } entry ? new StringValue(languages.settings.ServerPaths.GetValueOrDefault(entry.Name, string.Empty)) : null;
        }

        public bool IsModified(string key) => key == "server_updates" ? !languages.settings.ServerUpdates
            : EntryOf(key) is { } entry && languages.settings.ServerPaths.GetValueOrDefault(entry.Name, string.Empty).Length > 0;

        public void Set(string key, IRuntimeValue value)
        {
            switch (key, value)
            {
                case ("server_updates", BooleanValue on):
                    languages.settings.ServerUpdates = on.Value;
                    languages.context.Settings.Save();
                    return;
                case (_, StringValue path) when EntryOf(key) is { } entry:
                    string at = path.Value.Trim().Trim('"');
                    if (at == languages.settings.ServerPaths.GetValueOrDefault(entry.Name, string.Empty))
                        return;
                    if (at.Length == 0)
                        languages.settings.ServerPaths.Remove(entry.Name);
                    else
                        languages.settings.ServerPaths[entry.Name] = at;
                    languages.context.Settings.Save();

                    // The server it was is stopped and the one it is now started for the open files of it
                    languages.RestartServer(entry);
                    languages.ProbeServers([entry]);
                    return;
            }
        }

        public void Reset(string key) => Set(key, key == "server_updates" ? Values.Bool(true) : new StringValue(string.Empty));

        public void Press(string key, int which)
        {
            if (key == "server_updates")
            {
                languages.CheckServerUpdates(asked: true);
                return;
            }

            if (EntryOf(key) is not { } entry || Settings.FirstOrDefault(setting => setting.Key == key)?.Buttons is not { } buttons || which < 0 || which >= buttons.Length)
                return;

            switch (buttons[which])
            {
                case CHOOSE:
                    languages.ChooseServer(entry);
                    break;
                case INSTALL:
                    languages.InstallServer(entry);
                    break;
                case CHANGES:
                    languages.ShowServerChangelog(entry);
                    break;
            }
        }
    }

    /// <summary>Where a server is, picked with Hex's own dialog.</summary>
    private void ChooseServer(HexServerEntry entry)
    {
        string now = settings.ServerPaths.GetValueOrDefault(entry.Name) ?? serversFound.GetValueOrDefault(entry.Name)?.Path ?? string.Empty;
        string from = now.Length > 0 ? Path.GetDirectoryName(now) ?? context.Windows.BrowseFolder : context.Windows.BrowseFolder;
        (string, string)[] filter = OperatingSystem.IsWindows() ? [(context.Strings.Said("language_settings.programs"), "*.exe;*.cmd;*.bat")] : [];
        context.Windows.ChooseFile(context.Strings.Said("language_settings.where_is", ("command", entry.Command)), from, filter, path =>
        {
            new ServerSource(this).Set(SERVER_KEY + entry.Name, new StringValue(Path.GetFullPath(path)));
            context.Settings.Refresh(SERVER_KEY + entry.Name);
        });
    }
}
