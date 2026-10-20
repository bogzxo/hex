using Hex.Shell;

using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The language servers' section of the settings (Editor, Language servers). Whether Hex looks for new versions, Check
// now, and a row a server of language_servers.hor: where it is (empty for its command on the PATH), what it is and how
// it is installed, and Choose, Install (Download for one there is no command line for) and What changed. Under them
// what was found of each, where, which version and whether there is a newer one, written again as that changes.
internal sealed partial class HexLanguages
{
    internal const string SERVER_SECTION = "Editor · Language servers";

    // What a server's row is keyed by, after this its name
    internal const string SERVER_KEY = "server_path_";

    // What the buttons of a server's row say, which is what a press is told apart by
    internal const string CHOOSE = "Choose...", INSTALL = "Install or update", DOWNLOAD = "Download", CHANGES = "What changed";

    // The line a server has under the rows, by its name, while the settings are up
    internal readonly Dictionary<string, Label> serverStatus = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The panel of what was found of each server, for the section under its rows.</summary>
    private StackPanel MakeServerStatus()
    {
        serverStatus.Clear();
        var panel = new StackPanel { Spacing = 6.0f, Padding = new UIEdges(14.0f, 10.0f, 10.0f, 14.0f) };
        panel.Add(new Label { Text = "[b]On this machine[/b]", Markdown = true, Size = new System.Numerics.Vector2(640.0f, 0.0f), Align = Horizon.Rendering.Origin.TopLeft });
        foreach (HexServerEntry entry in ServerEntries)
        {
            var line = new Label { Markdown = true, Wrap = true, Size = new System.Numerics.Vector2(640.0f, 0.0f), Align = Horizon.Rendering.Origin.TopLeft };
            serverStatus[entry.Name] = line;
            panel.Add(line);
        }

        RefreshServerStatus();
        return panel;
    }

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

    private string ServerStatusOf(HexServerEntry entry)
    {
        string name = $"**{UIRichText.Literal(entry.Command)}**  [color:#7f8a9e]{UIRichText.Literal(string.Join(" ", entry.Extensions))}[/color]  ";
        if (Installing(entry) is { } going)
            return name + $"[color:#e5c07b]{UIRichText.Literal(going.Title.ToLowerInvariant())}{(going.Progress is { } progress and < 1.0f ? $" {progress:P0}" : "...")}[/color] {UIRichText.Literal(going.Message)}";
        if (serversProbing.Contains(entry.Name) || !serversFound.TryGetValue(entry.Name, out ServerFound? found))
            return name + "looking for it...";

        string latest = settings.ServerLatest.TryGetValue(entry.Name, out string? newest) ? newest : string.Empty;
        if (found.Path is null)
        {
            string out_ = latest.Length > 0 ? $", {UIRichText.Literal(latest)} is the newest" : string.Empty;
            return name + $"[color:#ff7366]not found[/color]{out_}. {(entry.Install.Length > 0 ? $"`{UIRichText.Literal(entry.Install)}` installs it" : "Download it from its releases")}.";
        }

        string version = found.Version is { } said ? UIRichText.Literal(said) : "a version it doesn't say";
        string news = NewerServerVersion(entry) is { } newer ? $", [color:#e5c07b]{UIRichText.Literal(newer)} is out[/color]"
            : latest.Length > 0 && found.Version is not null ? ", the newest" : string.Empty;
        return name + $"{version}{news}\n[color:#7f8a9e]{UIRichText.Literal(found.Path)}[/color]";
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
                new("server_updates", "Look for new versions", UIFormKind.Flag,
                    "Asks GitHub once a day, as Hex starts, for the newest release of each language server, and offers an update for one on this machine that is behind.",
                    SERVER_SECTION, Buttons: ["Check now"]),
                .. languages.ServerEntries.Select(entry => new UISetting(SERVER_KEY + entry.Name, entry.Command, UIFormKind.Text, HelpOf(entry), SERVER_SECTION,
                    Buttons: entry.Repo.Length > 0 ? [CHOOSE, InstallLabel(entry), CHANGES] : [CHOOSE, InstallLabel(entry)]))
            ];
        }

        private static string InstallLabel(HexServerEntry entry) => entry.Install.Length > 0 ? INSTALL : DOWNLOAD;

        private static string HelpOf(HexServerEntry entry)
        {
            string how = entry.Install.Length > 0 ? $"Installed with {entry.Install}." : entry.Repo.Length > 0 ? $"Downloaded from github.com/{entry.Repo}." : string.Empty;
            return $"{entry.About} {how} Where it is, empty for {entry.Command} on the PATH.".Trim();
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
                case INSTALL or DOWNLOAD:
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
        (string, string)[] filter = OperatingSystem.IsWindows() ? [("Programs (*.exe, *.cmd, *.bat)", "*.exe;*.cmd;*.bat")] : [];
        context.Windows.ChooseFile($"Where {entry.Command} is", from, filter, path =>
        {
            new ServerSource(this).Set(SERVER_KEY + entry.Name, new StringValue(Path.GetFullPath(path)));
            context.Settings.Refresh(SERVER_KEY + entry.Name);
        });
    }
}
