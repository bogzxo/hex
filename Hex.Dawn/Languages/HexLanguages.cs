using Horizon.UI.Components;
using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// Linting the languages that aren't HIDL, through their language servers (the protocol VS Code gets its squiggles
// through). Assets/hex/language_servers.hor says which server lints which files and how it is started, a file of one
// of them is opened in its server as it is opened here, sent again a moment after it is typed into, and what the
// server says about it goes under the code and into the Problems panel the way HIDL's own diagnostics do. A server
// that isn't on the machine is no trouble, the file is coloured and a card says once a session how the server is
// installed (HexLanguages.Missing). HIDL will go the same way once it
// has a server of its own, for now its diagnostics come out of the editor's parser and the run of the file.
internal sealed partial class HexLanguages
{
    private readonly DawnContext context;

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
    /// The languages, taking from the rest of the editor and giving it through the context: the documents opened, typed
    /// into and closed, what's wrong with them and what completes in them, the folders the servers run in watched, and
    /// a page of settings.
    /// </summary>
    public HexLanguages(DawnContext with)
    {
        context = with;

        // C# in ReSharper's colours whatever the skin (or Visual Studio's, or the skin's, as the settings say), the way it
        // looks where most of it is written
        ApplyCSharpColors();

        with.Documents.Opened += StartLinting;
        with.Documents.Edited += LintChanged;
        with.Documents.Closed += StopLinting;
        with.Documents.Saved += LintSaved;
        with.Documents.AddDiagnostics(of => lintFound.GetValueOrDefault(of));
        with.Documents.AddDiagnostics(CheckDiagnosticsOf);
        with.Documents.AddCompletions(ServerCompletions);
        with.Documents.AddHovers(ServerHover);
        with.Documents.AddSignatures(ServerSignature);
        AddServerCommands();

        // What changes on the disk in the folders the servers were started in, for what isn't open in them
        with.Workspace.WatchToo(() => LanguageServers.Running.Where(server => server.Failure is null).Select(server => server.RootDirectory));
        with.Workspace.FilesChanged += ServerFilesChanged;
        with.Workspace.Deleting += StopServersUnder;

        // What was found of the servers looked for again as the settings open, in case something was installed since
        with.Settings.AddSource(() => new ServerSource(this), SERVER_SECTION, MakeServerEditor, () => ProbeServers(ServerEntries), ForgetServerEditor);
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
        if (context.Workspace.Folders.Where(folder => PlatformPaths.IsUnder(full, folder)).OrderByDescending(folder => folder.Length).FirstOrDefault() is { } inWorkspace)
            return inWorkspace;

        string folder = Path.GetDirectoryName(full) ?? System.Environment.CurrentDirectory;
        if (serverEntries.Find(entry => entry.Extensions.Any(extension => extension.Equals(Path.GetExtension(CodeLanguages.WithoutBackupSuffix(full)), StringComparison.OrdinalIgnoreCase))) is { Reload.Length: > 0 } server)
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
    internal string? LinterOf(HexDocument of) => linters.TryGetValue(of, out LanguageServer? server) ? server.Name : null;

    /// <summary>
    /// The program linting a document as somebody knows it, serve-d, csharp-ls, null for none. Its name in
    /// language_servers.hor was what the status bar said, "D, d".
    /// </summary>
    internal string? ServerShownOf(HexDocument of) =>
        LinterOf(of) is { } name ? Path.GetFileNameWithoutExtension(ServerEntry(name) is { } entry ? CommandOf(entry) : name)
            + (ServerStoppedFor(of) ? " " + context.Strings.Said("languages.stopped") : string.Empty) : null;

    // What language_servers.hor says, read the first time a server is wanted, nothing when it can't be read
    private List<HexServerEntry> serverEntries = [];

    // The settings, where the servers are among them
    private HexSettings settings => context.Settings.Values;

    /// <summary>The servers language_servers.hor names, in its order.</summary>
    internal IReadOnlyList<HexServerEntry> ServerEntries
    {
        get
        {
            if (!serversRead)
                ReadLanguageServers();
            return serverEntries;
        }
    }

    /// <summary>
    /// Helper method to read which servers there are, once, from the file next to the editor, with the ones added in the
    /// settings among them (HexLanguages.Added).
    /// </summary>
    private void ReadLanguageServers()
    {
        serversRead = true;
        if (File.Exists(LANGUAGE_SERVERS))
        {
            try
            {
                fileEntries = HexServerCatalog.Read(File.ReadAllText(LANGUAGE_SERVERS));
            }
            catch (Exception e)
            {
                Console.WriteLine($"[Hex] {LANGUAGE_SERVERS} wasn't read, {e.Message}");
            }
        }

        serverEntries = Merged();
        foreach (HexServerEntry entry in serverEntries)
            RegisterServer(entry);
    }

    /// <summary>What a server is started with, where the settings say it is, or the file's command. One added in the settings is where it was added.</summary>
    private string CommandOf(HexServerEntry entry) =>
        !IsAdded(entry) && settings.ServerPaths.TryGetValue(entry.Name, out string? at) && at.Length > 0 ? at : entry.Command;

    // Helper method to say which files a server is for and how it is started, which stops the one that was running (done
    // when the task is, off the editor's thread)
    private Task RegisterServer(HexServerEntry entry) =>
        LanguageServers.Register(entry.Name, CommandOf(entry), entry.Arguments, entry.LanguageId, entry.Extensions, entry.Settings);

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
        foreach (HexDocument of in context.Documents.All.ToList())
        {
            if (of.Path is { } path && !linters.ContainsKey(of)
                && entry.Extensions.Any(extension => extension.Equals(Path.GetExtension(ServerPathFor(of, path)), StringComparison.OrdinalIgnoreCase)))
                StartLinting(of);
        }
    }

    /// <summary>Opens a file of another language in its language server, when there is one for it on this machine.</summary>
    private void StartLinting(HexDocument of)
    {
        // A diff is no file of the language, its server never hears of it
        if (of.DiffOf is not null)
            return;

        CheckSoon(of, 0.0f);
        if (of.Path is not { } path || of.Editor.Highlighter is null || ReferenceEquals(of.Editor.Highlighter, CodeLanguages.Hidl))
            return;

        // The framework's source decompiled to be read is no project's, a server started for its folder knew nothing
        if (PlatformPaths.IsUnder(path, DecompiledFolder))
            return;

        if (!serversRead)
            ReadLanguageServers();

        string root = ServerRootFor(path);
        string serverPath = ServerPathFor(of, path);
        if (LanguageServers.For(serverPath, root) is not { } server)
        {
            SayIfMissing(serverPath);
            return;
        }

        server.Woken ??= WakeForLinting;

        // What changes in its folder is said to it, and a project changing there starts it again, workspace or not
        context.Workspace.Rewatch();
        linters[of] = server;
        ServersChanged++;
        server.Open(path, of.Editor.Text);
        Console.WriteLine($"[Hex] {Path.GetFileName(path)} is linted by {server.Name}, started in {root}.");
    }

    // The server's reading thread has something, the window's thread wakes for it
    private void WakeForLinting() => context.Wake();

    /// <summary>
    /// The path a file's server is picked by, a backup's suffix off (Foo.cs.orig goes to C#'s), and for a file set to a
    /// language by hand or by an association, one with an extension that language's server is registered for. The file
    /// itself is still opened in the server by its own path.
    /// </summary>
    private string ServerPathFor(HexDocument of, string path)
    {
        string bare = CodeLanguages.WithoutBackupSuffix(path);
        if (of.Editor.Highlighter is null)
            return bare;

        if (!serversRead)
            ReadLanguageServers();

        string id = CodeLanguages.LanguageIdOf(of.Editor.Highlighter);
        string extension = Path.GetExtension(bare);
        if (serverEntries.Find(entry => entry.LanguageId.Equals(id, StringComparison.OrdinalIgnoreCase)) is { Extensions: [var first, ..] } wanted
            && !wanted.Extensions.Any(known => known.Equals(extension, StringComparison.OrdinalIgnoreCase)))
            return Path.ChangeExtension(bare, first);
        return bare;
    }

    /// <summary>
    /// A file's language was changed (Change Language Mode, an association), its old server forgets it and the new
    /// language's hears of it with its own language id, when there is one.
    /// </summary>
    internal void LanguageChanged(HexDocument of)
    {
        StopLinting(of);
        StartLinting(of);
        ServersChanged++;
    }

    /// <summary>A file of another language was typed into, its server hears a moment after the typing stops.</summary>
    private void LintChanged(HexDocument changed)
    {
        if (changed.DiffOf is not null)
            return;

        CheckSoon(changed);
        CheckOthersSoon(changed);
        if (linters.ContainsKey(changed))
            lintIn[changed] = LINT_DELAY;
    }

    /// <summary>
    /// A file of another language was saved, its server told so, with what was typed since it last heard first. Some look
    /// the whole of a file over only then, serve-d builds it with dub, and never heard it was.
    /// </summary>
    private void LintSaved(HexDocument saved)
    {
        CheckSoon(saved, 0.0f);
        if (!linters.TryGetValue(saved, out LanguageServer? server) || saved.Path is not { } path)
            return;

        if (lintIn.Remove(saved))
            server.Change(path, saved.Editor.Text);
        server.Saved(path);
    }

    /// <summary>A file of another language was closed, its server is told and forgets it.</summary>
    private void StopLinting(HexDocument closed)
    {
        if (linters.Remove(closed, out LanguageServer? server) && closed.Path is { } path)
            server.Close(path);

        lintIn.Remove(closed);
        lintFound.Remove(closed);
        ForgetCheck(closed);
        ForgetServerCompletions(closed);
    }

    // The documents waiting on a lint or a check, gathered into the same list every tick they're counted down, the
    // waits change as they're gone through
    private readonly List<HexDocument> dueNow = [];

    /// <summary>Every tick, the changes that are due go to their servers and what the servers said is taken in.</summary>
    internal void Update(float dt)
    {
        if (lintIn.Count > 0)
        {
            dueNow.Clear();
            dueNow.AddRange(lintIn.Keys);
            foreach (HexDocument due in dueNow)
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
                context.Notices.Note(context.Strings.Said("languages.server_restarted", ("command", due.Command)));
                RestartServer(due);
            }
        }

        IReadOnlyList<LanguageServer> servers = LanguageServers.Running;
        for (int i = 0; i < servers.Count; i++)
        {
            LanguageServer server = servers[i];
            if (server.Failure is { } failure && failed.Add(server.Name))
                stoppedNow.Add((server, failure));

            TakeTalk(server);

            while (server.TryTake(out string path, out IReadOnlyList<UICodeDiagnostic> found))
            {
                string full = Path.GetFullPath(path);
                HexDocument? of = context.Documents.Find(full);
                if (of is null)
                    continue;

                lintFound[of] = found;
                context.Documents.DiagnosticsChanged(of);
                Console.WriteLine($"[Hex] {server.Name} has {found.Count} things to say about {Path.GetFileName(path)}.");
            }
        }

        UpdateChecks(dt);
        UpdateAnswers();
        ServersStopped();
        AskNext();
        UpdateServerCompletions();
        UpdateInstallStatus();
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
