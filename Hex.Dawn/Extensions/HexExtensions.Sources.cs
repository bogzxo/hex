using Hex.Core;

namespace Hex.Dawn;

// Where extensions come from and taking one out again. The catalog of the settings (extension_catalog) is the first
// source and any added after it (extension_sources) follow it, each a GitHub repository (owner/name), a git repository
// by its URL or a folder on this machine. Adding one syncs straight away, taking one away forgets what it had and its
// synced copy. Uninstalling an extension switches it off the way Switch does, stops watching it and deletes its folder
// out of the extensions folder next to Hex. One that comes with Hex can only be switched off.
internal sealed partial class HexExtensions
{
    /// <summary>Something about the installed extensions, the catalog or the sources changed, for the Extensions window to show again.</summary>
    internal event Action? ListChanged;

    /// <summary>Every source in the order they're synced and listed, the catalog of the settings first.</summary>
    internal IReadOnlyList<string> Sources => [.. Kept.AllExtensionSources()];

    /// <summary>Whether a source is the catalog of the settings, the one the rest come after.</summary>
    internal bool IsMainSource(string source) => source.Equals(Kept.ExtensionCatalog, StringComparison.OrdinalIgnoreCase);

    // Tells whoever shows the extensions that they changed
    private void CatalogChanged() => ListChanged?.Invoke();

    /// <summary>
    /// Adds a place extensions come from and syncs it. False with why when it isn't one (a folder that isn't there, a
    /// name that isn't owner/name or a URL) or it's in the list already.
    /// </summary>
    internal bool AddSource(string source, out string why)
    {
        string tidy = source.Trim().Trim('"');
        why = string.Empty;
        if (tidy.Length == 0 || !HexExtensionCatalog.IsSource(tidy))
        {
            why = context.Strings.Said("extension_sources.not_a_source", ("source", tidy));
            return false;
        }

        if (Sources.Contains(tidy, StringComparer.OrdinalIgnoreCase))
        {
            why = context.Strings.Said("extension_sources.already", ("source", tidy));
            return false;
        }

        Kept.ExtensionSources.Add(tidy);
        context.Settings.Save();
        SyncCatalog(asked: true);
        CatalogChanged();
        return true;
    }

    /// <summary>
    /// Takes a source away, what it had no longer listed and its synced copy deleted. The catalog of the settings goes
    /// too when there's another to take its place (the next one moves up), the last source there is stays.
    /// </summary>
    internal bool RemoveSource(string source)
    {
        if (IsMainSource(source))
        {
            if (Kept.ExtensionSources.Count == 0)
                return false;
            Kept.ExtensionCatalog = Kept.ExtensionSources[0];
            Kept.ExtensionSources.RemoveAt(0);
        }
        else if (Kept.ExtensionSources.RemoveAll(other => other.Equals(source, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            return false;
        }

        context.Settings.Save();
        sourceProblems.Remove(source);

        string synced = SourceFolder(CatalogFolder, source);
        string[] sources = [.. Kept.AllExtensionSources()];
        string folder = CatalogFolder;
        context.Work.InBackground(context.Strings.Said("extension_catalog.reading"), () =>
        {
            try
            {
                if (Directory.Exists(synced))
                    Directory.Delete(synced, recursive: true);
            }
            catch (IOException)
            {
                // Something has a file of it open, it's ignored as it's no longer a source either way
            }

            return ReadAll(sources, folder);
        }, read =>
        {
            catalog = read;
            CatalogChanged();
        }, quiet: true);

        CatalogChanged();
        return true;
    }

    /// <summary>How many extensions a source's last sync brought, those another source higher up has as well included.</summary>
    internal int SyncedCount(string source) => HexExtensionCatalog.Extensions(SourceFolder(CatalogFolder, source)).Count();

    /// <summary>Whether an extension can be uninstalled, one of the extensions folder's rather than one Hex comes with.</summary>
    internal bool CanUninstall(HexExtension extension) => !extension.BuiltIn && IsInside(extension.Folder, OwnFolder);

    /// <summary>
    /// Uninstalls an extension, switched off and taken out first and then its folder deleted. Its settings are kept in
    /// case it's installed again. False with why when it comes with Hex or its folder couldn't be deleted.
    /// </summary>
    internal bool Uninstall(HexExtension extension, out string why)
    {
        why = string.Empty;
        if (!CanUninstall(extension))
        {
            why = context.Strings.Said("extensions_window.comes_with_hex", ("name", extension.Name));
            return false;
        }

        // What it did that Hex can take back, the way switching it off does, and nothing it asked for left running
        screens.ForgetTitleNote(extension);
        ForgetAgents(extension);
        ForgetTimers(extension);
        Hex.Core.Projects.ProjectTemplates.Forget(extension.Id);
        extension.Enabled = false;
        extensionsChanged.TryRemove(extension, out _);
        Remove(extension);

        try
        {
            if (Directory.Exists(extension.Folder))
            {
                foreach (string file in Directory.GetFiles(extension.Folder, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(extension.Folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            why = e.Message;
        }

        Kept.CatalogExtensions.Remove(extension.Id);
        Kept.DisabledExtensions.Remove(extension.Id);
        context.Settings.Save();

        if (why.Length == 0)
            context.Notices.Say(context.Strings.Said("extensions_window.uninstalled", ("name", extension.Name)));
        else
            context.Notices.Notify(context.Strings.Said("extensions_window.uninstall_left", ("name", extension.Name), ("why", why)), error: true);

        CatalogChanged();
        return why.Length == 0;
    }

    /// <summary>Switches an extension on or off and says so to the Extensions window.</summary>
    internal void Toggle(HexExtension extension, bool on)
    {
        if (extension.Enabled == on)
            return;
        Switch(extension, on);
        CatalogChanged();
    }

    // Whether a folder is inside another (or is it), by their full paths
    private static bool IsInside(string folder, string parent)
    {
        string full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
