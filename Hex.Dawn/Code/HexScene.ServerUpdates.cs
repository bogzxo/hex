using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Hex.Dawn;

// What is on this machine of the language servers language_servers.hor names, which version, and whether there is a
// newer one. Where each one is and the version it says it is are found out off the editor's thread (it starts every
// server that can say its version once, which takes a moment for the .NET ones). Once a day as Hex starts, and whenever
// Check now is pressed, the newest release of each one with a repository is asked of GitHub and kept in the settings,
// so the days in between still know. A server that is behind gets a card with Update, What changed and Not this one.
// Installing and updating run the entry's command line in the background, the server stopped first (a program that
// is running can't be written over) and started again after, and what the command printed goes in the output.
internal sealed partial class HexScene
{
    // How long after a look at GitHub the next one is, as Hex starts
    private static readonly TimeSpan SERVER_CHECK_EVERY = TimeSpan.FromHours(20);

    // How long an install or an update is given before it is taken to have hung
    private const int SERVER_COMMAND_TIMEOUT = 10 * 60 * 1000;

    /// <summary>What was found of a server on this machine.</summary>
    /// <param name="Path">Where it is, null for not on this machine.</param>
    /// <param name="Version">What it says it is, null for nothing (it can't say, or isn't there).</param>
    private sealed record ServerFound(string? Path, string? Version);

    private readonly Dictionary<string, ServerFound> serversFound = new(StringComparer.OrdinalIgnoreCase);

    // The servers being looked for, installed or updated, and the ones a card was put up for this run
    private readonly HashSet<string> serversProbing = new(StringComparer.OrdinalIgnoreCase), serversInstalling = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> serversOffered = new(StringComparer.OrdinalIgnoreCase);
    private bool serversChecking;

    /// <summary>The entry of a server by its name, null for none.</summary>
    private HexServerEntry? ServerEntry(string name) => ServerEntries.FirstOrDefault(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// As Hex starts, every server looked for, then GitHub asked about the newest versions if it's been a day (and the
    /// settings let it), and anything that is behind offered.
    /// </summary>
    private void StartServerChecks()
    {
        if (options.SelfTest || options.Check.Length > 0)
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
        InBackground("looking for the language servers...", () =>
        {
            var found = new List<(string Name, ServerFound Found)>();
            foreach (var (entry, command) in looking)
            {
                string? path = HexServerCatalog.Locate(command);
                string? version = path is not null && entry.VersionArguments is { } arguments ? HexServerCatalog.VersionOf(path, arguments) : null;
                found.Add((entry.Name, new ServerFound(path, version)));
            }

            return found;
        }, found =>
        {
            foreach (var (name, what) in found)
            {
                serversProbing.Remove(name);
                serversFound[name] = what;
            }

            RefreshServerStatus();
            then?.Invoke();
        }, quiet: true);
    }

    /// <summary>
    /// Asks GitHub for the newest release of every server that has a repository, keeps what it said, and offers what is
    /// behind. Asked (Check now) says what it found either way, otherwise only a server that is behind is said anything about.
    /// </summary>
    private void CheckServerUpdates(bool asked)
    {
        if (serversChecking)
            return;

        serversChecking = true;
        var repos = ServerEntries.Where(entry => entry.Repo.Length > 0).Select(entry => (entry.Name, entry.Repo)).ToList();
        RefreshServerStatus();
        InBackground("looking for new versions of the language servers...", () =>
        {
            var latest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var problems = new List<string>();
            Parallel.ForEach(repos, new ParallelOptions { MaxDegreeOfParallelism = 4 }, pair =>
            {
                try
                {
                    if (HexReleases.Latest(pair.Repo).GetAwaiter().GetResult() is { } release)
                    {
                        lock (latest)
                            latest[pair.Name] = release.Tag;
                    }
                }
                catch (Exception e)
                {
                    lock (problems)
                        problems.Add($"{pair.Name}: {e.Message}");
                }
            });

            return (latest, problems);
        }, result =>
        {
            serversChecking = false;
            foreach (var (name, tag) in result.latest)
                settings.ServerLatest[name] = tag;

            // Only a look that got somewhere counts as having looked, no network tries again next time
            if (result.latest.Count > 0)
                settings.ServersChecked = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            SaveSettings();
            RefreshServerStatus();

            int behind = OfferServerUpdates(again: asked);
            if (result.problems.Count > 0)
                Note($"GitHub couldn't be asked about some of the language servers, {string.Join("; ", result.problems)}", error: result.latest.Count == 0);
            if (asked)
            {
                Say(result.latest.Count == 0 && result.problems.Count > 0 ? "GitHub couldn't be asked, see the output"
                    : behind == 0 ? "every language server on this machine is up to date" : $"{behind} language server{(behind == 1 ? " has" : "s have")} a new version");
            }
        }, quiet: !asked);
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
            if (NewerServerVersion(entry) is not { } latest || serversInstalling.Contains(entry.Name))
                continue;

            behind++;
            bool skipped = settings.ServerSkipped.TryGetValue(entry.Name, out string? skip) && HexServerCatalog.Same(skip, latest);
            if ((skipped && !again) || (!serversOffered.Add(entry.Name) && !again))
                continue;

            string have = serversFound[entry.Name].Version ?? "?";
            var actions = new List<(string, Action)>
            {
                (entry.Update.Length > 0 ? "Update" : "Download", () => InstallServer(entry)),
            };
            if (entry.Repo.Length > 0)
                actions.Add(("What changed", () => ShowServerChangelog(entry)));
            actions.Add(("Not this one", () =>
            {
                settings.ServerSkipped[entry.Name] = latest;
                SaveSettings();
            }));

            Notify($"{entry.Command} {Plain(latest)} is out, you have {have}.", actions: [.. actions]);
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
        InBackground($"asking GitHub what changed in {entry.Command}...", () =>
        {
            try
            {
                return (Releases: HexReleases.Recent(entry.Repo, have is null ? 5 : 30).GetAwaiter().GetResult(), Problem: (string?)null);
            }
            catch (Exception e)
            {
                return (Releases: new List<HexRelease>(), Problem: e.Message);
            }
        }, result =>
        {
            if (result.Problem is { } problem)
            {
                Say($"GitHub couldn't be asked about {entry.Command}, {problem}", error: true);
                return;
            }

            string header = have is null
                ? $"The newest releases of **{entry.Command}**, from [{entry.Repo}]({entry.ReleasesPage}).\n\n"
                : $"What changed in **{entry.Command}** since {have}, the one on this machine, from [{entry.Repo}]({entry.ReleasesPage}).\n\n";
            ShowText($"What changed in {entry.Command}", header + HexReleases.Changelog(result.Releases, have, Literal), 560.0f);
        });
    }

    /// <summary>
    /// Installs or updates a server by its entry's command line, in the background, the server stopped first and
    /// started again after with every open file of its. One without a command line has its releases opened instead.
    /// </summary>
    private void InstallServer(HexServerEntry entry)
    {
        bool there = serversFound.TryGetValue(entry.Name, out ServerFound? found) && found.Path is not null;
        string line = there ? entry.Update : entry.Install;
        if (line.Length == 0)
        {
            if (entry.ReleasesPage.Length > 0)
                OpenInBrowser(entry.ReleasesPage);
            else
                Say($"{entry.Command} has no way of being installed from here, see language_servers.hor", error: true);
            return;
        }

        if (!serversInstalling.Add(entry.Name))
            return;

        StopServer(entry);
        RefreshServerStatus();
        string doing = there ? "updating" : "installing";
        Note($"> {line}");
        InBackground($"{doing} {entry.Command}...", () => RunCommandLine(line), result =>
        {
            serversInstalling.Remove(entry.Name);
            if (result.Output.Length > 0)
                Note(result.Output.TrimEnd(), error: result.Code != 0);

            string? before = found?.Version;
            ProbeServers([entry], () =>
            {
                RestartServer(entry);
                ServerFound now = serversFound.GetValueOrDefault(entry.Name) ?? new ServerFound(null, null);
                if (result.Code != 0 || now.Path is null)
                {
                    Notify(result.Code != 0 ? $"{doing} {entry.Command} didn't work, it ended with {result.Code}, what it said is in the output"
                        : $"{entry.Command} was {(there ? "updated" : "installed")} but isn't on the PATH Hex has, set where it is in the settings or restart Hex",
                        error: true, ("Settings", () => OpenSettings(SERVER_SECTION)));
                    return;
                }

                string version = now.Version is { } said ? $" {said}" : string.Empty;
                Say(there && before is not null && now.Version is not null && HexServerCatalog.Same(before, now.Version)
                    ? $"{entry.Command} was already the newest it could be, {before}"
                    : $"{entry.Command}{version} is {(there ? "updated" : "installed")}");
            });
        });
    }

    /// <summary>
    /// Runs a command line by the usual shell (cmd on Windows, sh elsewhere) and waits for it, what it printed and
    /// its exit code. Nothing is typed into it, a command that asks something is told no. Blocks, for the background.
    /// </summary>
    private static (int Code, string Output) RunCommandLine(string line)
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/s", "/c", line } }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", line } };
        info.UseShellExecute = false;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.RedirectStandardInput = true;
        info.CreateNoWindow = true;

        var output = new StringBuilder();
        try
        {
            using Process process = Process.Start(info) ?? throw new InvalidOperationException("it didn't start");
            process.StandardInput.Close();
            process.OutputDataReceived += (_, said) => { if (said.Data is { } text) lock (output) output.AppendLine(text); };
            process.ErrorDataReceived += (_, said) => { if (said.Data is { } text) lock (output) output.AppendLine(text); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(SERVER_COMMAND_TIMEOUT))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                lock (output)
                    return (-1, output + "\n(it took more than ten minutes and was stopped)");
            }

            process.WaitForExit();
            lock (output)
                return (process.ExitCode, output.ToString());
        }
        catch (Exception e)
        {
            return (-1, $"{line} couldn't be run, {e.Message}");
        }
    }

    /// <summary>A page in the browser the system has.</summary>
    private void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Say($"{url} couldn't be opened, {e.Message}", error: true);
        }
    }
}
