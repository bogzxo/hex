using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

// Linting the languages that aren't HIDL, through their language servers (the protocol VS Code gets its squiggles
// through). Assets/hex/language_servers.hor says which server lints which files and how it is started, a file of one
// of them is opened in its server as it is opened here, sent again a moment after it is typed into, and what the
// server says about it goes under the code and into the Problems panel the way HIDL's own diagnostics do. A server
// that isn't on the machine is no trouble, the file is coloured and nothing is said. HIDL will go the same way once it
// has a server of its own, for now its diagnostics come out of the editor's parser and the run of the file.
internal sealed partial class HexScene
{
    private const string LANGUAGE_SERVERS = "Assets/hex/language_servers.hor";

    // How long after the last keystroke the text goes to the server
    private const float LINT_DELAY = 0.3f;

    // The server every open file of another language is in, how long until its last change is sent, and what the server
    // last said about it
    private readonly Dictionary<HexDocument, LanguageServer> linters = [];
    private readonly Dictionary<HexDocument, float> lintIn = [];
    private readonly Dictionary<HexDocument, IReadOnlyList<UICodeDiagnostic>> lintFound = [];
    private bool serversRead;

    // The servers to be started again, a project of theirs having changed, and how long until (a build or a checkout
    // writes many at once, the server goes once they've settled)
    private const float RELOAD_DELAY = 1.5f;
    private readonly Dictionary<HexServerEntry, float> reloadIn = [];
    private readonly HashSet<string> failed = [];

    /// <summary>
    /// What the languages take from the rest of the editor and give it, through the context: the documents opened, typed
    /// into and closed, what's wrong with them and what completes in them, the folders the servers run in watched, and
    /// a page of settings.
    /// </summary>
    private void AttachLanguages()
    {
        DawnContext with = Context;
        with.Documents.Opened += StartLinting;
        with.Documents.Edited += LintChanged;
        with.Documents.Closed += StopLinting;
        with.Documents.AddDiagnostics(of => lintFound.GetValueOrDefault(of));
        with.Documents.AddCompletions(ServerCompletions);

        // What changes on the disk in the folders the servers were started in, for what isn't open in them
        with.Workspace.WatchToo(() => LanguageServers.Running.Where(server => server.Failure is null).Select(server => server.RootDirectory));
        with.Workspace.FilesChanged += ServerFilesChanged;
        with.Workspace.Deleting += StopServersUnder;

        // What was found of the servers looked for again as the settings open, in case something was installed since
        with.Settings.AddSource(() => new ServerSource(this), SERVER_SECTION, MakeServerStatus, () => ProbeServers(ServerEntries), serverStatus.Clear);
    }

    /// <summary>A folder going in the trash, the servers started in it stopped first, their files let go of.</summary>
    private void StopServersUnder(string folder)
    {
        foreach (HexDocument of in linters.Where(pair => PlatformPaths.IsUnder(pair.Value.RootDirectory, folder)).Select(pair => pair.Key).ToList())
            StopLinting(of);
        LanguageServers.StopUnder(folder).Wait(TimeSpan.FromSeconds(6));
    }

    /// <summary>
    /// The folder a file's server runs in, which is where it looks for projects: the workspace's folder the file is in,
    /// otherwise the nearest folder over it with a project of its server's (a .csproj, a .sln, what its reload names),
    /// otherwise the file's own folder. A file outside the workspace got the workspace's server, which knew nothing of it.
    /// </summary>
    private string ServerRootFor(string path)
    {
        string full = Path.GetFullPath(path);
        if (workspace.Folders.Where(folder => PlatformPaths.IsUnder(full, folder)).OrderByDescending(folder => folder.Length).FirstOrDefault() is { } inWorkspace)
            return inWorkspace;

        string folder = Path.GetDirectoryName(full) ?? System.Environment.CurrentDirectory;
        if (serverEntries.Find(entry => entry.Extensions.Any(extension => extension.Equals(Path.GetExtension(full), StringComparison.OrdinalIgnoreCase))) is { Reload.Length: > 0 } server)
        {
            for (string? above = folder; above is not null; above = Path.GetDirectoryName(above))
            {
                try
                {
                    if (Directory.EnumerateFiles(above).Any(server.ReloadsFor))
                        return above;
                }
                catch (Exception)
                {
                    // A folder that can't be read has no project as far as this goes
                }
            }
        }

        return folder;
    }

    /// <summary>The name of the server linting a document, null for none.</summary>
    private string? LinterOf(HexDocument of) => linters.TryGetValue(of, out LanguageServer? server) ? server.Name : null;

    // What language_servers.hor says, read the first time a server is wanted, nothing when it can't be read
    private List<HexServerEntry> serverEntries = [];

    /// <summary>The servers language_servers.hor names, in its order.</summary>
    private IReadOnlyList<HexServerEntry> ServerEntries
    {
        get
        {
            if (!serversRead)
                ReadLanguageServers();
            return serverEntries;
        }
    }

    /// <summary>Helper method to read which servers there are, once, from the file next to the editor.</summary>
    private void ReadLanguageServers()
    {
        serversRead = true;
        if (!File.Exists(LANGUAGE_SERVERS))
            return;

        try
        {
            serverEntries = HexServerCatalog.Read(File.ReadAllText(LANGUAGE_SERVERS));
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Hex] {LANGUAGE_SERVERS} wasn't read, {e.Message}");
            return;
        }

        foreach (HexServerEntry entry in serverEntries)
            RegisterServer(entry);
    }

    /// <summary>What a server is started with, where the settings say it is, or the file's command.</summary>
    private string CommandOf(HexServerEntry entry) =>
        settings.ServerPaths.TryGetValue(entry.Name, out string? at) && at.Length > 0 ? at : entry.Command;

    // Helper method to say which files a server is for and how it is started, which stops the one that was running (done
    // when the task is, off the editor's thread)
    private Task RegisterServer(HexServerEntry entry) =>
        LanguageServers.Register(entry.Name, CommandOf(entry), entry.Arguments, entry.LanguageId, entry.Extensions);

    /// <summary>
    /// Stops a server, its files let go of. It starts again with the next file of its that is opened, or with
    /// <see cref="RestartServer"/>. Before it is updated, a program that is running can't be written over.
    /// </summary>
    private Task StopServer(HexServerEntry entry)
    {
        foreach (HexDocument of in linters.Where(pair => pair.Value.Name == entry.Name).Select(pair => pair.Key).ToList())
            StopLinting(of);

        failed.Remove(entry.Name);
        return RegisterServer(entry);
    }

    /// <summary>
    /// A server started again with where it is now, after that changed or it was installed or updated, and every open
    /// file of its opened in it, the ones there was no server for before as well.
    /// </summary>
    private void RestartServer(HexServerEntry entry)
    {
        StopServer(entry);
        foreach (HexDocument of in documents.ToList())
        {
            if (of.Path is { } path && !linters.ContainsKey(of)
                && entry.Extensions.Any(extension => extension.Equals(Path.GetExtension(path), StringComparison.OrdinalIgnoreCase)))
                StartLinting(of);
        }
    }

    /// <summary>Opens a file of another language in its language server, when there is one for it on this machine.</summary>
    private void StartLinting(HexDocument of)
    {
        if (of.Path is not { } path || of.Editor.Highlighter is null || ReferenceEquals(of.Editor.Highlighter, CodeLanguages.Hidl))
            return;

        if (!serversRead)
            ReadLanguageServers();

        string root = ServerRootFor(path);
        if (LanguageServers.For(path, root) is not { } server)
            return;

        server.Woken ??= WakeForLinting;

        // What changes in its folder is said to it, and a project changing there starts it again, workspace or not
        Context.Workspace.Rewatch();
        linters[of] = server;
        server.Open(path, of.Editor.Text);
        Console.WriteLine($"[Hex] {Path.GetFileName(path)} is linted by {server.Name}, started in {root}.");
    }

    // The server's reading thread has something, the window's thread wakes for it
    private void WakeForLinting() => Engine.WindowManager.Wake();

    /// <summary>A file of another language was typed into, its server hears a moment after the typing stops.</summary>
    private void LintChanged(HexDocument changed)
    {
        if (linters.ContainsKey(changed))
            lintIn[changed] = LINT_DELAY;
    }

    /// <summary>A file of another language was closed, its server is told and forgets it.</summary>
    private void StopLinting(HexDocument closed)
    {
        if (linters.Remove(closed, out LanguageServer? server) && closed.Path is { } path)
            server.Close(path);

        lintIn.Remove(closed);
        lintFound.Remove(closed);
        ForgetServerCompletions(closed);
    }

    /// <summary>Every tick, the changes that are due go to their servers and what the servers said is taken in.</summary>
    private void UpdateLinting(float dt)
    {
        if (lintIn.Count > 0)
        {
            foreach (HexDocument due in lintIn.Keys.ToArray())
            {
                if ((lintIn[due] -= dt) > 0.0f)
                    continue;

                lintIn.Remove(due);
                if (linters.TryGetValue(due, out LanguageServer? server) && due.Path is { } path)
                    server.Change(path, due.Editor.Text);
            }
        }

        if (reloadIn.Count > 0)
        {
            foreach (HexServerEntry due in reloadIn.Keys.ToArray())
            {
                if ((reloadIn[due] -= dt) > 0.0f)
                    continue;

                reloadIn.Remove(due);
                Note($"[Hex] {due.Command} started again, a project of its changed");
                RestartServer(due);
            }
        }

        IReadOnlyList<LanguageServer> servers = LanguageServers.Running;
        for (int i = 0; i < servers.Count; i++)
        {
            LanguageServer server = servers[i];
            if (server.Failure is { } failure && failed.Add(server.Name))
                Console.WriteLine($"[Hex] {server.Name} stopped, {failure}");

            while (server.TryTake(out string path, out IReadOnlyList<UICodeDiagnostic> found))
            {
                string full = Path.GetFullPath(path);
                HexDocument? of = documents.Find(open => open.Path is { } at && string.Equals(Path.GetFullPath(at), full, PlatformPaths.Comparison));
                if (of is null)
                    continue;

                lintFound[of] = found;
                ShowDiagnostics(of);
                Console.WriteLine($"[Hex] {server.Name} has {found.Count} things to say about {Path.GetFileName(path)}.");
            }
        }

        UpdateServerCompletions();
    }

    /// <summary>What changed on the disk where the servers run, said to every server, which goes by it for what isn't open in it.</summary>
    private void ServerFilesChanged(IReadOnlyList<(string Path, LanguageServerFileChange Change)> changes)
    {
        IReadOnlyList<LanguageServer> servers = LanguageServers.Running;
        foreach (LanguageServer server in servers)
        {
            if (server.Failure is null)
                server.FilesChanged(changes);
        }

        // A project of a server's that reads them only as it starts, the server started again a moment after
        foreach (HexServerEntry entry in serverEntries)
        {
            if (changes.Any(change => entry.ReloadsFor(change.Path)) && servers.Any(server => server.Name == entry.Name))
                reloadIn[entry] = RELOAD_DELAY;
        }
    }
}
