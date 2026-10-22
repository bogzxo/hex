namespace Hex.Dawn;

// The language servers added in the settings (HexSettings.AddedServers) beside the ones language_servers.hor names. One
// added for a language the file has a server for is used instead of it, and takes the files it's for from whichever
// other server had them. Taken away again, the file's server is back as it was.
internal sealed partial class HexLanguages
{
    // The servers of language_servers.hor as the file says them, before any added in the settings took their place
    private List<HexServerEntry> fileEntries = [];

    // The names of the servers that were added in the settings, which are where they are and never a path set for the file's
    private readonly HashSet<string> addedNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a server was added in the settings, rather than named by language_servers.hor.</summary>
    internal bool IsAdded(HexServerEntry entry) => addedNames.Contains(entry.Name);

    /// <summary>
    /// Helper method to put the file's servers and the added ones together, an added one in place of the file's for its
    /// language, and the files it's for taken off any other.
    /// </summary>
    private List<HexServerEntry> Merged()
    {
        var added = settings.AddedServers.Select(server => new HexServerEntry(
            server.Language, server.Command, server.Arguments, server.Language, server.Extensions,
            context.Strings.Said("language_settings.added_about"), string.Empty, string.Empty, string.Empty, null)).ToList();

        addedNames.Clear();
        addedNames.UnionWith(added.Select(entry => entry.Name));

        var taken = new HashSet<string>(added.SelectMany(entry => entry.Extensions), StringComparer.OrdinalIgnoreCase);
        return
        [
            .. fileEntries
                .Where(entry => !addedNames.Contains(entry.Name) && !added.Any(other => other.LanguageId.Equals(entry.LanguageId, StringComparison.OrdinalIgnoreCase)))
                .Select(entry => entry with { Extensions = [.. entry.Extensions.Where(extension => !taken.Contains(extension))] }),
            .. added
        ];
    }

    /// <summary>
    /// Adds a language server, or puts one in place of the one there was for its language, saves the settings and starts
    /// it for the open files of it.
    /// </summary>
    internal void AddServer(HexAddedServer server)
    {
        int at = settings.AddedServers.FindIndex(other => other.Language.Equals(server.Language, StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
            settings.AddedServers[at] = server;
        else
            settings.AddedServers.Add(server);

        context.Settings.Save();
        ApplyServers();
    }

    /// <summary>Takes away a server added in the settings, the file's one for its language back if there is one.</summary>
    internal void RemoveServer(string language)
    {
        if (settings.AddedServers.RemoveAll(server => server.Language.Equals(language, StringComparison.OrdinalIgnoreCase)) == 0)
            return;

        context.Settings.Save();
        ApplyServers();
    }

    /// <summary>
    /// Helper method to put the servers as they are now in force. One that's gone is given no files and its open files let
    /// go of, one that changed (or is new) started again for its open files, the rest left running.
    /// </summary>
    private void ApplyServers()
    {
        if (!serversRead)
            ReadLanguageServers();

        List<HexServerEntry> before = serverEntries;
        serverEntries = Merged();

        foreach (HexServerEntry gone in before.Where(old => !serverEntries.Any(now => now.Name == old.Name)))
        {
            foreach (HexDocument of in linters.Where(pair => pair.Value.Name == gone.Name).Select(pair => pair.Key).ToList())
                StopLinting(of);
            LanguageServers.Register(gone.Name, gone.Command, gone.Arguments, gone.LanguageId, []);
        }

        var changed = serverEntries.Where(now => before.Find(old => old.Name == now.Name) is not { } old || !SameServer(old, now)).ToList();
        foreach (HexServerEntry entry in changed)
            RestartServer(entry);

        ProbeServers(changed);
        RebuildServerEditor();
    }

    private static bool SameServer(HexServerEntry a, HexServerEntry b) =>
        a.Command == b.Command && a.LanguageId == b.LanguageId && a.Arguments.SequenceEqual(b.Arguments)
        && a.Extensions.SequenceEqual(b.Extensions, StringComparer.OrdinalIgnoreCase);
}
