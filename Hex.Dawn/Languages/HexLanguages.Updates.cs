using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

using Horizon.Core.Threading;

namespace Hex.Dawn;

// What is on this machine of the language servers language_servers.hor names, which version, and whether there is a
// newer one. Where each one is and the version it says it is are found out off the editor's thread (it starts every
// server that can say its version once, which takes a moment for the .NET ones). Once a day as Hex starts, and whenever
// Check now is pressed, the newest release of each one with a repository is asked of GitHub and kept in the settings,
// so the days in between still know. A server that is behind gets a card with Update, What changed and Not this one.
// Installing and updating run the entry's command line in the background, the server stopped first (a program that
// is running can't be written over) and started again after. All of it is work of the scheduler's (IWork),
// each known by a key so it runs once at a time: an update pressed twice, or pressed again from the settings while it
// goes, is the one update, with a card saying how far it got, what the command is printing, and Cancel.
internal sealed partial class HexLanguages
{
    // The keys of the servers' work
    private const string FIND_WORK = "servers:find:", CHECK_WORK = "servers:check", CHANGES_WORK = "servers:changes:";
    internal const string INSTALL_WORK = "servers:install:";

    // A percentage a command prints as it goes (npm's, dotnet's, winget's), which the bar is set to
    private static readonly Regex Percent = new(@"(\d{1,3}(?:\.\d+)?)\s?%", RegexOptions.Compiled);

    // How long after a look at GitHub the next one is, as Hex starts
    private static readonly TimeSpan SERVER_CHECK_EVERY = TimeSpan.FromHours(20);

    // How long an install or an update is given before it is taken to have hung
    private const int SERVER_COMMAND_TIMEOUT = 10 * 60 * 1000;

    /// <summary>What was found of a server on this machine.</summary>
    /// <param name="Path">Where it is, null for not on this machine.</param>
    /// <param name="Version">What it says it is, null for nothing (it can't say, or isn't there).</param>
    private sealed record ServerFound(string? Path, string? Version);

    private readonly Dictionary<string, ServerFound> serversFound = new(StringComparer.OrdinalIgnoreCase);

    // The servers being looked for, and the ones a card was put up for this run
    private readonly HashSet<string> serversProbing = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> serversOffered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The install or update of a server that's going, null when none is.</summary>
    internal WorkItem? Installing(HexServerEntry entry)
    {
        // Asked every tick while the settings are up, the key made once a server
        if (!installKeys.TryGetValue(entry.Name, out string? key))
            installKeys[entry.Name] = key = INSTALL_WORK + entry.Name;
        return context.Work.Find(key);
    }

    private readonly Dictionary<string, string> installKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The entry of a server by its name, null for none.</summary>
    internal HexServerEntry? ServerEntry(string name) => ServerEntries.FirstOrDefault(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// As Hex starts, every server looked for, then GitHub asked about the newest versions if it's been a day (and the
    /// settings let it), and anything that is behind offered.
    /// </summary>
    internal void StartChecks()
    {
        if (context.Options.SelfTest || context.Options.Check.Length > 0)
            return;

        ProbeServers(ServerEntries, () =>
        {
            bool due = !DateTimeOffset.TryParse(settings.ServersChecked, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last)
                || DateTimeOffset.UtcNow - last > SERVER_CHECK_EVERY;
            if (settings.ServerUpdates && due)
                CheckServerUpdates(asked: false);
            else if (settings.ServerUpdates)
                OfferServerUpdates();
        });
    }

    /// <summary>Finds out where some servers are and which version, then does something on the editor's thread.</summary>
    private void ProbeServers(IEnumerable<HexServerEntry> entries, Action? then = null)
    {
        var looking = entries.Where(entry => serversProbing.Add(entry.Name)).Select(entry => (Entry: entry, Command: CommandOf(entry))).ToList();
        if (looking.Count == 0)
        {
            then?.Invoke();
            return;
        }

        RefreshServerStatus();
        var found = new List<(string Name, ServerFound Found)>();
        context.Work.Start(FIND_WORK + string.Join(',', looking.Select(pair => pair.Entry.Name)), context.Strings.Said("language_updates.looking_for_servers"), context =>
        {
            Horizon.Core.Processes.ProcessEnvironment.WaitForLoginShell();
            for (int i = 0; i < looking.Count; i++)
            {
                var (entry, command) = looking[i];
                context.Report((float)i / looking.Count, entry.Command);
                string? path = HexServerCatalog.Locate(command);
                string? version = path is not null && entry.VersionArguments is { } arguments ? HexServerCatalog.VersionOf(path, arguments) : null;
                found.Add((entry.Name, new ServerFound(path, version)));
            }
        }, _ =>
        {
            foreach (var (entry, _) in looking)
                serversProbing.Remove(entry.Name);
            foreach (var (name, what) in found)
                serversFound[name] = what;

            RefreshServerStatus();
            then?.Invoke();
        }, seen: false, cancellable: false);
    }

    /// <summary>
    /// Asks GitHub for the newest release of every server that has a repository, keeps what it said, and offers what is
    /// behind. Asked (Check now) says what it found either way, otherwise only a server that is behind is said anything about.
    /// </summary>
    private void CheckServerUpdates(bool asked)
    {
        var repos = ServerEntries.Where(entry => entry.Repo.Length > 0).Select(entry => (entry.Name, entry.Repo)).ToList();
        var latest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        int asking = 0;

        // Asked again while it goes, it's the one look, and says what it found when it's done the same
        context.Work.Start(CHECK_WORK, context.Strings.Said("language_updates.looking_for_versions"), async context =>
        {
            context.Report(0.0f, this.context.Strings.Said("language_updates.asking_github"));
            await Parallel.ForEachAsync(repos, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = context.Token }, async (pair, cancel) =>
            {
                try
                {
                    if (await HexReleases.Latest(pair.Repo, cancel).ConfigureAwait(false) is { } release)
                    {
                        lock (latest)
                            latest[pair.Name] = release.Tag;
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    lock (problems)
                        problems.Add($"{pair.Name}: {e.Message}");
                }

                int done = Interlocked.Increment(ref asking);
                context.Report((float)done / repos.Count, $"{pair.Repo}");
            }).ConfigureAwait(false);
        }, item =>
        {
            if (item.State != WorkState.Succeeded)
                return;

            foreach (var (name, tag) in latest)
                settings.ServerLatest[name] = tag;

            // Only a look that got somewhere counts as having looked, no network tries again next time
            if (latest.Count > 0)
                settings.ServersChecked = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            context.Settings.Save();
            RefreshServerStatus();

            int behind = OfferServerUpdates(again: asked);
            // Quiet, not red, GitHub not answering (no network, its limit on asking) is nothing anybody did wrong. Only said
            // in the output when somebody asked, the look Hex takes by itself as it starts put it there every start
            if (problems.Count > 0 && asked)
                context.Notices.Note(context.Strings.Said("language_updates.some_not_asked", ("problems", string.Join("; ", problems))));
            else if (problems.Count > 0)
                Console.WriteLine($"[Hex] The newest versions of some language servers weren't asked about, {string.Join("; ", problems)}.");
            if (asked)
            {
                string count = behind.ToString(CultureInfo.CurrentCulture);
                context.Notices.Say(latest.Count == 0 && problems.Count > 0 ? context.Strings.Said("language_updates.not_asked")
                    : behind == 0 ? context.Strings.Said("language_updates.all_up_to_date")
                    : behind == 1 ? context.Strings.Said("language_updates.one_behind", ("count", count)) : context.Strings.Said("language_updates.many_behind", ("count", count)));
            }
        }, seen: asked);
        RefreshServerStatus();
    }

    /// <summary>Whether a server on this machine is older than the newest release, which version that is if so.</summary>
    private string? NewerServerVersion(HexServerEntry entry) =>
        serversFound.TryGetValue(entry.Name, out ServerFound? found) && found is { Path: not null, Version: { } version }
        && settings.ServerLatest.TryGetValue(entry.Name, out string? latest) && HexServerCatalog.IsNewer(latest, version) ? latest : null;

    /// <summary>
    /// A card for every server on this machine that is behind, unless that version was said not to be offered, once a
    /// run (asked again, those that had one get another). How many are behind, offered or not.
    /// </summary>
    private int OfferServerUpdates(bool again = false)
    {
        int behind = 0;
        foreach (HexServerEntry entry in ServerEntries)
        {
            if (NewerServerVersion(entry) is not { } latest || Installing(entry) is not null)
                continue;

            behind++;
            bool skipped = settings.ServerSkipped.TryGetValue(entry.Name, out string? skip) && HexServerCatalog.Same(skip, latest);
            if ((skipped && !again) || (!serversOffered.Add(entry.Name) && !again))
                continue;

            string have = serversFound[entry.Name].Version ?? "?";
            var actions = new List<(string, Action)>
            {
                (entry.Update.Length > 0 ? context.Strings.Said("language_updates.update") : context.Strings.Said("language_updates.download"), () => InstallServer(entry)),
            };
            if (entry.Repo.Length > 0)
                actions.Add((context.Strings.Said("language_updates.what_changed"), () => ShowServerChangelog(entry)));
            actions.Add((context.Strings.Said("language_updates.not_this_one"), () =>
            {
                settings.ServerSkipped[entry.Name] = latest;
                context.Settings.Save();
            }));

            context.Notices.Notify(context.Strings.Said("language_updates.is_out", ("command", entry.Command), ("latest", UIRichText.Plain(latest)), ("have", have)), actions: [.. actions]);
        }

        return behind;
    }

    /// <summary>
    /// What changed in a server, from GitHub, in a window. The releases newer than the one on this machine, the last few
    /// when it isn't there or can't say its version.
    /// </summary>
    private void ShowServerChangelog(HexServerEntry entry)
    {
        if (entry.Repo.Length == 0)
            return;

        string? have = serversFound.TryGetValue(entry.Name, out ServerFound? found) ? found.Version : null;
        List<HexRelease> releases = [];
        context.Notices.Say(context.Strings.Said("language_updates.asking_what_changed", ("command", entry.Command)));
        context.Work.Start(CHANGES_WORK + entry.Name, context.Strings.Said("language_updates.what_changed_in", ("command", entry.Command)), async context =>
        {
            context.Say(this.context.Strings.Said("language_updates.asking_repo", ("repo", entry.Repo)));
            releases = await HexReleases.Recent(entry.Repo, have is null ? 5 : 30, context.Token).ConfigureAwait(false);
        }, item =>
        {
            if (item.State == WorkState.Failed)
            {
                context.Notices.Say(context.Strings.Said("language_updates.not_asked_about", ("command", entry.Command), ("error", $"{item.Error}")), error: true);
                return;
            }

            if (item.State != WorkState.Succeeded)
                return;

            string header = have is null
                ? context.Strings.Said("language_updates.newest_releases_header", ("command", entry.Command), ("repo", entry.Repo), ("page", entry.ReleasesPage)) + "\n\n"
                : context.Strings.Said("language_updates.changed_since_header", ("command", entry.Command), ("have", have), ("repo", entry.Repo), ("page", entry.ReleasesPage)) + "\n\n";
            context.Windows.ShowText(context.Strings.Said("language_updates.what_changed_in", ("command", entry.Command)), header + HexReleases.Changelog(releases, have, UIRichText.Literal), 560.0f);
        }, seen: false);
    }

    /// <summary>
    /// Installs or updates a server by its entry's command line, in the background, the server stopped first and
    /// started again after with every open file of its. One without a command line has its releases opened instead.
    /// </summary>
    internal void InstallServer(HexServerEntry entry)
    {
        bool there = serversFound.TryGetValue(entry.Name, out ServerFound? found) && found.Path is not null;
        string line = there ? entry.Update : entry.Install;
        if (line.Length == 0)
        {
            if (entry.ReleasesPage.Length > 0)
                context.Windows.OpenInBrowser(entry.ReleasesPage);
            else
                context.Notices.Say(context.Strings.Said("language_updates.no_way_to_install", ("command", entry.Command)), error: true);
            return;
        }

        // Going already (pressed again, or from the settings and from a card both), it's that one, its card brought back
        if (Installing(entry) is { } going)
        {
            context.Work.Renew(going);
            return;
        }

        // A program that's running can't be written over, the server goes first (its process gone before the command starts)
        Task stopped = StopServer(entry);
        string? before = found?.Version;
        context.Notices.Note($"> {line}");
        context.Work.Start(INSTALL_WORK + entry.Name, there ? context.Strings.Said("language_updates.updating", ("command", entry.Command)) : context.Strings.Said("language_updates.installing", ("command", entry.Command)), async context =>
        {
            context.Say(this.context.Strings.Said("language_updates.stopping", ("command", entry.Command)));
            await stopped.WaitAsync(context.Token).ConfigureAwait(false);
            context.Say(line);
            int code = await RunCommandLine(line, context).ConfigureAwait(false);
            if (code != 0)
                throw new InvalidOperationException($"{line} ended with {code}, what it said is under Output");
        }, item =>
        {
            foreach (string said in item.Output)
                context.Notices.Note(said, error: item.State == WorkState.Failed);

            // Whatever happened, what's there now is looked for, and the server started again with it
            ProbeServers([entry], () =>
            {
                RestartServer(entry);
                if (item.State != WorkState.Succeeded)
                    return;

                ServerFound now = serversFound.GetValueOrDefault(entry.Name) ?? new ServerFound(null, null);
                if (now.Path is null)
                {
                    context.Notices.Notify(there ? context.Strings.Said("language_updates.updated_not_on_path", ("command", entry.Command)) : context.Strings.Said("language_updates.installed_not_on_path", ("command", entry.Command)),
                        error: true, (context.Strings.Said("settings.title"), () => context.Windows.OpenSettings(SERVER_SECTION)));
                    return;
                }

                string version = now.Version is { } said ? $" {said}" : string.Empty;
                context.Notices.Say(there && before is not null && now.Version is not null && HexServerCatalog.Same(before, now.Version)
                    ? context.Strings.Said("language_updates.already_newest", ("command", entry.Command), ("before", before))
                    : there ? context.Strings.Said("language_updates.is_updated", ("command", entry.Command), ("version", version))
                    : context.Strings.Said("language_updates.is_installed", ("command", entry.Command), ("version", version)));
            });
        });
        RefreshServerStatus();
    }

    /// <summary>
    /// Runs a command line by the usual shell (cmd on Windows, sh elsewhere) as a piece of work, every line it prints
    /// written to it (the last shown on its card, a percentage in one setting its bar), and its exit code once it ends.
    /// Nothing is typed into it, a command that asks something is told no. Cancelled, the command and everything it
    /// started are ended; taking more than ten minutes, likewise, and it's taken to have hung.
    /// </summary>
    private static async Task<int> RunCommandLine(string line, WorkContext context)
    {
        var info = InstallStartInfo(line);

        void Heard(string? said)
        {
            if (said is null)
                return;

            context.Write(said);
            if (Percent.Match(said) is { Success: true } percent && float.TryParse(percent.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float done) && done <= 100.0f)
                context.Report(done / 100.0f);
        }

        using Process process = Process.Start(info) ?? throw new InvalidOperationException($"{line} couldn't be started");
        process.StandardInput.Close();
        process.OutputDataReceived += (_, said) => Heard(said.Data);
        process.ErrorDataReceived += (_, said) => Heard(said.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(context.Token);
        limit.CancelAfter(SERVER_COMMAND_TIMEOUT);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            context.ThrowIfCancelled();
            throw new TimeoutException($"{line} took more than ten minutes, and was stopped");
        }

        return process.ExitCode;
    }

    internal static ProcessStartInfo InstallStartInfo(string line)
    {
        Horizon.Core.Processes.ProcessEnvironment.WaitForLoginShell();
        var info = OperatingSystem.IsWindows()
            // cmd parses shell syntax, not CRT argument escaping. /s removes this one outer quote pair.
            ? new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")) { Arguments = "/d /s /c \"" + line + "\"" }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", line } };
        info.UseShellExecute = false;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.RedirectStandardInput = true;
        info.CreateNoWindow = true;
        foreach (var (key, value) in Horizon.Core.Processes.ProcessEnvironment.Current())
            info.Environment[key] = value;

        return info;
    }
}
