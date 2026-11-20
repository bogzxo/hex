using System.Globalization;
using System.Numerics;

using Hex.Core;
using Hex.Shell;

using Horizon.Core.Threading;
using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The extensions that can be installed, out of a catalog on GitHub (bogzxo/hex-extensions, or another repository or a
// folder the settings say) and any more sources added after it (a GitHub repository, a git URL or a folder, see
// HexExtensions.Sources). Once a day as Hex starts, and whenever Check now is pressed, every source is synced into a
// folder of its own under Hex's (HexExtensionCatalog), and what's in them is listed together on Extensions, Available
// and in the Extensions window, with Install on the ones that aren't here and Update on the ones installed out of it
// that are behind. One that's behind gets a card as well. Two sources with an extension by the same name, the one higher
// up the list is the one that's offered.
// An install is a copy into the extensions folder next to Hex, the extension started straight away, an update the same
// copy over it and the extension read again. All of it is work of the scheduler's, each known by a key so a press twice
// is the one install.
internal sealed partial class HexExtensions
{
    internal const string AVAILABLE = "Extensions · Available";

    // Where extensions come from, the catalogue of the settings and the rest, see HexExtensions.Sources
    internal const string SOURCES = "Extensions · Sources";

    // The keys of the catalog's work
    private const string CATALOG_WORK = "extensions:catalog", INSTALL_WORK = "extensions:install:";

    // How long after a sync the next one is, as Hex starts
    private static readonly TimeSpan CATALOG_CHECK_EVERY = TimeSpan.FromHours(20);

    /// <summary>An extension of the catalog, what its extension object says it is and where the synced copy of it is.</summary>
    internal sealed record CatalogEntry(string Id, string Name, string Description, string Version, string Author, string Folder, string Source = "");

    private List<CatalogEntry> catalog = [];

    // Why the last sync didn't go, null when it did or there hasn't been one, and why each source didn't by itself
    private string? catalogProblem;
    private readonly Dictionary<string, string> sourceProblems = new(StringComparer.OrdinalIgnoreCase);

    // The updates a card was put up for this run, by the extension and the version
    private readonly HashSet<string> catalogOffered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every extension of the catalog as it was last synced, by name.</summary>
    internal IReadOnlyList<CatalogEntry> Catalog => catalog;

    /// <summary>Where the catalog is synced to, a folder of Hex's (the self test's own while it runs).</summary>
    internal string CatalogFolder { get; set; } = Path.Combine(Hex.Core.HexHost.ProgramFolder, "extension_catalog");

    /// <summary>The extensions folder next to Hex, the one installs go into (the self test's own while it runs).</summary>
    internal string OwnFolder { get; set; } = Path.Combine(Hex.Core.HexHost.ProgramFolder, OWN_EXTENSIONS);

    private HexSettings Kept => context.Settings.Values;

    /// <summary>
    /// As Hex starts, the catalog as it was last synced read, then synced again if it's been a day (and the settings let
    /// it), and whatever installed out of it is behind offered.
    /// </summary>
    internal void StartCatalog()
    {
        bool due = !DateTimeOffset.TryParse(Kept.ExtensionsChecked, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last)
            || DateTimeOffset.UtcNow - last > CATALOG_CHECK_EVERY;
        bool look = Kept.ExtensionUpdates && !context.Options.SelfTest && context.Options.Check.Length == 0;

        string folder = CatalogFolder;
        string[] sources = [.. Kept.AllExtensionSources()];

        // A source that was never synced (just added, or from before each had a folder of its own) is synced now rather
        // than tomorrow
        due = due || sources.Any(source => !Directory.Exists(SourceFolder(folder, source)));
        context.Work.InBackground(context.Strings.Said("extension_catalog.reading"), () => ReadAll(sources, folder), read =>
        {
            catalog = read;
            CatalogChanged();
            if (look && due)
                SyncCatalog(asked: false);
            else if (look)
                OfferUpdates();
        }, quiet: true);
    }

    /// <summary>
    /// The catalog synced off the editor's thread and listed again. Asked (Check now) says what it found either way, a
    /// card if it couldn't, otherwise only an extension that is behind is said anything about.
    /// </summary>
    internal WorkItem SyncCatalog(bool asked)
    {
        string[] sources = [.. Kept.AllExtensionSources()];
        string source = string.Join(", ", sources), into = CatalogFolder;
        bool hadSome = catalog.Count > 0;
        List<CatalogEntry>? read = null;
        var problems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return context.Work.Start(CATALOG_WORK, context.Strings.Said("extension_catalog.syncing"), async work =>
        {
            for (int i = 0; i < sources.Length; i++)
            {
                work.Report(i / (float)sources.Length, sources[i]);
                try
                {
                    await HexExtensionCatalog.Sync(sources[i], SourceFolder(into, sources[i]), work.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    problems[sources[i]] = e.Message;
                }
            }

            // Every source failing keeps what was listed (GitHub down, the network gone), one of several failing keeps
            // its last sync and the rest are what they are now
            if (problems.Count < sources.Length || !hadSome)
                read = ReadAll(sources, into);
            if (problems.Count > 0)
                throw new IOException(string.Join("; ", problems.Select(problem => $"{problem.Key}, {problem.Value}")));
        }, done =>
        {
            sourceProblems.Clear();
            foreach (var (from, why) in problems)
                sourceProblems[from] = why;

            if (read is not null)
                catalog = read;

            if (done.State == WorkState.Succeeded && read is not null)
            {
                catalogProblem = null;
                Kept.ExtensionsChecked = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                context.Settings.Save();

                int behind = Behind().Count();
                if (asked)
                    context.Notices.Say(behind == 0 ? context.Strings.Said("extension_catalog.up_to_date", ("count", catalog.Count.ToString(CultureInfo.InvariantCulture)))
                        : context.Strings.Said("extension_catalog.updates", ("count", behind.ToString(CultureInfo.InvariantCulture))));
                else
                    OfferUpdates();
            }
            else if (done.State == WorkState.Failed)
            {
                catalogProblem = done.Error ?? "?";
                if (asked)
                    context.Notices.Notify(context.Strings.Said("extension_catalog.failed", ("source", source), ("why", catalogProblem)), error: true);
            }

            CatalogChanged();
        }, seen: asked);
    }

    /// <summary>Where a source is synced to, a folder of its own under the catalog's.</summary>
    private static string SourceFolder(string catalogFolder, string source) =>
        Path.Combine(catalogFolder, "sources", HexExtensionCatalog.FolderNameOf(source));

    /// <summary>Why a source didn't sync the last time, null when it did or hasn't been tried.</summary>
    internal string? ProblemOf(string source) => sourceProblems.GetValueOrDefault(source);

    /// <summary>Whether the catalog is being synced right now.</summary>
    internal bool Syncing => context.Work.Find(CATALOG_WORK) is not null;

    /// <summary>
    /// Every source's synced extensions read and put together in the order of the sources, the first one with an
    /// extension's name being where it comes from unless a later one has a newer version of it (a folder of the
    /// extensions being worked on next to the published ones), then sorted by name. Off the editor's thread.
    /// </summary>
    private static List<CatalogEntry> ReadAll(IReadOnlyList<string> sources, string catalogFolder)
    {
        var read = new List<CatalogEntry>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in sources)
        {
            foreach (CatalogEntry entry in ReadCatalog(SourceFolder(catalogFolder, source), source))
            {
                if (!seen.TryGetValue(entry.Id, out int at))
                {
                    seen[entry.Id] = read.Count;
                    read.Add(entry);
                }
                else if (HexExtensionCatalog.IsNewer(entry.Version, read[at].Version))
                    read[at] = entry;
            }
        }

        read.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return read;
    }

    /// <summary>
    /// What every extension of a synced catalog says it is, read off the editor's thread. Its file is run the way Hex runs
    /// an extension's to find out (not its on_start), with nothing to touch as hex, and one that doesn't run is listed by
    /// its folder.
    /// </summary>
    private static List<CatalogEntry> ReadCatalog(string folder, string source)
    {
        var read = new List<CatalogEntry>();
        foreach (string inside in HexExtensionCatalog.Extensions(folder))
        {
            var probe = new HexExtension(inside, builtIn: false);
            probe.Load(Natives.Object(), _ => { });
            read.Add(new CatalogEntry(probe.Id, probe.Name, probe.Description, probe.Version, probe.Author, inside, source));
        }

        return read;
    }

    /// <summary>The extension here by the same folder name as one of the catalog, null when it isn't installed.</summary>
    internal HexExtension? InstalledOf(CatalogEntry entry) =>
        all.FirstOrDefault(extension => extension.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The extensions installed out of the catalog that it has a newer version of.</summary>
    internal IEnumerable<(CatalogEntry Entry, HexExtension Installed)> Behind()
    {
        foreach (CatalogEntry entry in catalog)
        {
            if (Kept.CatalogExtensions.Contains(entry.Id) && InstalledOf(entry) is { BuiltIn: false } installed
                && HexExtensionCatalog.IsNewer(entry.Version, installed.Version))
                yield return (entry, installed);
        }
    }

    /// <summary>A card for every extension that is behind, once a version a run.</summary>
    private void OfferUpdates()
    {
        foreach (var (entry, installed) in Behind())
        {
            if (!catalogOffered.Add($"{entry.Id}@{entry.Version}"))
                continue;

            CatalogEntry of = entry;
            context.Notices.Notify(context.Strings.Said("extension_catalog.newer_out", ("name", installed.Name), ("version", entry.Version), ("installed", installed.Version)), false,
                (context.Strings.Said("extension_catalog.update"), () => Install(of)),
                (context.Strings.Said("extension_catalog.see_all"), () => context.Windows.OpenSettings(AVAILABLE)));
        }
    }

    /// <summary>The install or update of an extension that's going, null when none is.</summary>
    internal WorkItem? Installing(CatalogEntry entry) => context.Work.Find(INSTALL_WORK + entry.Id);

    /// <summary>
    /// An extension of the catalog installed, or brought up to date, in the extensions folder next to Hex. A new one is
    /// started straight away (unless it was switched off before), one that was running is read again.
    /// </summary>
    internal WorkItem Install(CatalogEntry entry)
    {
        string to = Path.Combine(OwnFolder, entry.Id);
        HexExtension? there = InstalledOf(entry);
        bool update = there is not null;

        WorkItem going = context.Work.Start(INSTALL_WORK + entry.Id, context.Strings.Said(update ? "extension_catalog.updating" : "extension_catalog.installing", ("name", entry.Name)), work =>
        {
            work.Report(0.0f, entry.Folder);
            HexExtensionCatalog.Install(entry.Folder, to);
        }, done =>
        {
            if (done.State != WorkState.Succeeded)
            {
                if (done.State == WorkState.Failed)
                    context.Notices.Notify(context.Strings.Said("extension_catalog.install_failed", ("name", entry.Name), ("why", done.Error ?? "?")), error: true);
                CatalogChanged();
                return;
            }

            Kept.CatalogExtensions.Add(entry.Id);
            context.Settings.Save();

            if (there is null)
            {
                var added = new HexExtension(to, builtIn: false) { Enabled = !Kept.DisabledExtensions.Contains(entry.Id) };
                all.Add(added);
                if (added.Enabled)
                    Start(added);
            }
            else if (there.Enabled)
            {
                // Read again now rather than when its folder settles, the copy's changes are this
                extensionsChanged.TryRemove(there, out _);
                ReloadExtension(there);
            }

            WatchFolders();
            context.Notices.Say(context.Strings.Said(update ? "extension_catalog.updated" : "extension_catalog.installed", ("name", entry.Name), ("version", entry.Version)));
            CatalogChanged();
        }, cancellable: false);

        CatalogChanged();
        return going;
    }

    /* Extensions, Available */

    /// <summary>The catalog's section of the settings, whether it's looked at, Check now, and where it is.</summary>
    private sealed class CatalogSource(HexExtensions extensions) : ISettingsSource
    {
        public string Section => SettingsSections.EDITOR;

        public IReadOnlyList<UISetting> Settings { get; } =
        [
            new("extension_updates", HexText.Get("extension_catalog.look"), UIFormKind.Flag, HexText.Get("extension_catalog.look_help"), AVAILABLE,
                Buttons: [HexText.Get("extension_catalog.check_now")]),
            new("extension_catalog", HexText.Get("extension_catalog.source"), UIFormKind.Text, HexText.Get("extension_catalog.source_help"), SOURCES,
                Placeholder: "bogzxo/hex-extensions")
        ];

        public IRuntimeValue? Get(string key) => key switch
        {
            "extension_updates" => Values.Bool(extensions.Kept.ExtensionUpdates),
            "extension_catalog" => new StringValue(extensions.Kept.ExtensionCatalog),
            _ => null
        };

        public bool IsModified(string key) => key switch
        {
            "extension_updates" => !extensions.Kept.ExtensionUpdates,
            "extension_catalog" => extensions.Kept.ExtensionCatalog != HexSettings.DEFAULT_CATALOG,
            _ => false
        };

        public void Set(string key, IRuntimeValue value)
        {
            switch (key, value)
            {
                case ("extension_updates", BooleanValue on):
                    extensions.Kept.ExtensionUpdates = on.Value;
                    break;
                case ("extension_catalog", StringValue source):
                    string tidy = source.Value.Trim().Trim('"');
                    if (tidy == extensions.Kept.ExtensionCatalog)
                        return;
                    extensions.Kept.ExtensionCatalog = tidy.Length > 0 ? tidy : HexSettings.DEFAULT_CATALOG;
                    break;
                default:
                    return;
            }

            extensions.context.Settings.Save();
        }

        public void Reset(string key) => Set(key, key == "extension_updates" ? Values.Bool(true) : new StringValue(HexSettings.DEFAULT_CATALOG));

        public void Press(string key, int which)
        {
            if (key == "extension_updates")
                extensions.SyncCatalog(asked: true);
        }
    }

    /// <summary>What an extension of the catalog's row has on its button, Install or Update, null for none, for the self test.</summary>
    internal string? CatalogButton(CatalogEntry entry) => StateOf(entry).Button;

    // What an extension of the catalog's row says, and its button (null for none)
    private (string Said, string? Button) StateOf(CatalogEntry entry)
    {
        string by = entry.Author.Length > 0 ? "  " + context.Strings.Said("extensions.by", ("author", UIRichText.MarkdownLiteral(entry.Author))) : string.Empty;
        string head = $"**{UIRichText.MarkdownLiteral(entry.Name)}** {UIRichText.MarkdownLiteral(entry.Version)}{by}\n{UIRichText.MarkdownLiteral(entry.Description)}";
        string Line(string said) => $"{head}\n[color:dim]{said}[/color]";

        if (InstalledOf(entry) is not { } installed)
            return (Line(context.Strings.Said("extension_catalog.not_installed")), context.Strings.Said("extension_catalog.install"));
        if (installed.BuiltIn)
            return (Line(context.Strings.Said("extension_catalog.comes_with_hex")), null);
        if (!Kept.CatalogExtensions.Contains(entry.Id))
            return (Line(context.Strings.Said("extension_catalog.own_by_that_name")), null);
        if (HexExtensionCatalog.IsNewer(entry.Version, installed.Version))
            return (Line(context.Strings.Said("extension_catalog.behind", ("installed", UIRichText.MarkdownLiteral(installed.Version)))), context.Strings.Said("extension_catalog.update"));
        return (Line(context.Strings.Said("extension_catalog.installed_line")), null);
    }
}
