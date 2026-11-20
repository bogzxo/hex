namespace Hex.Dawn;

// The language servers asked for an agent about a file it hasn't got open (hex_calls, hex_implementations, hex_rename,
// hex_fix, hex_diagnostics, the check after hex_edit). A file open in a tab goes by its tab's server as F12 does. Any
// other is opened in its language's server with no tab, its text as it is on the disk, told again when the disk changes,
// and let go of after it's gone unasked for a while. What a server says is wrong with a file is counted as it comes, so
// a tool that changed one can wait for what the server says of the change.
internal sealed partial class HexLanguages
{
    // How long a file opened for an agent stays open in its server unasked, in seconds
    private const float AGENT_FILE_IDLE = 180.0f;

    // The most files opened for agents at once, the longest unasked let go of first
    private const int MOST_AGENT_FILES = 48;

    private sealed class AgentFile(LanguageServer server, long length, long written)
    {
        public readonly LanguageServer Server = server;
        public long Length = length, Written = written;
        public float Idle;
    }

    private readonly Dictionary<string, AgentFile> agentFiles = new(PlatformPaths.Comparer);

    // What servers last said is wrong with each file, how many times they've said it and when, open in a tab or not
    private readonly HexHeardLog agentHeard = new();

    /// <summary>A server said what's wrong with a file, kept for the agent tools, whoever it's open for.</summary>
    private void AgentHeard(LanguageServer server, string path, IReadOnlyList<UICodeDiagnostic> found, bool linted)
    {
        if (!linted && !(agentFiles.TryGetValue(path, out AgentFile? mine) && ReferenceEquals(mine.Server, server)))
            return;
        agentHeard.Record(path, found, server.Name, System.Environment.TickCount64);
    }

    /// <summary>How many times a file's been said of by a server, for waiting on the next time, and the last of it.</summary>
    internal (int Count, IReadOnlyList<UICodeDiagnostic>? Found, string? Server) AgentDiagnostics(string path) =>
        agentHeard.Of(Path.GetFullPath(path)) is var heard ? (heard.Count, heard.Found, heard.Server) : default;

    /// <summary>Whether a server's said what's wrong with a file at or after a moment (a tick), the moment an edit was sent.</summary>
    internal bool AgentSaidSince(string path, long since) => agentHeard.SaidSince(Path.GetFullPath(path), since);

    /// <summary>
    /// The server for a file an agent asks about, open in a tab or not, the file's latest text sent to it first. With
    /// <paramref name="start"/> false a server that isn't running yet isn't started (a check after an edit doesn't wait
    /// for csharp-ls to load a solution). Null with why not. The server may still be starting, wait for IsReady.
    /// </summary>
    internal LanguageServer? AgentServer(string path, bool start, out string? why)
    {
        why = null;
        string full = Path.GetFullPath(path);
        if (context.Documents.Find(full) is { } open && linters.TryGetValue(open, out LanguageServer? linting) && linting.Failure is null)
        {
            if (lintIn.Remove(open))
                linting.Change(full, open.Editor.Text);

            // What its server said before an agent asked, a tab that's said nothing new since would be waited on for ever
            if (!agentHeard.Has(full) && lintFound.TryGetValue(open, out IReadOnlyList<UICodeDiagnostic>? said))
                agentHeard.Record(full, said, linting.Name, 0);
            return linting;
        }

        if (!File.Exists(full))
        {
            why = $"There's no file {full}.";
            return null;
        }

        var info = new FileInfo(full);
        if (agentFiles.TryGetValue(full, out AgentFile? known))
        {
            if (known.Server.Failure is null)
            {
                known.Idle = 0.0f;
                if (known.Length != info.Length || known.Written != info.LastWriteTimeUtc.Ticks)
                {
                    (known.Length, known.Written) = (info.Length, info.LastWriteTimeUtc.Ticks);
                    known.Server.Change(full, ReadText(full));
                }
                return known.Server;
            }
            agentFiles.Remove(full);
        }

        if (!serversRead)
            ReadLanguageServers();
        string serverPath = CodeLanguages.WithoutBackupSuffix(full);
        if (LanguageServers.RegistrationFor(serverPath) is not { } registration)
        {
            why = $"No language server is set up for {Path.GetExtension(full)} files (the Language Servers window adds one).";
            return null;
        }

        string root = ServerRootFor(full);
        if (UsesClangd(registration) && CMakeProject.RootOf(full, context.Workspace.Roots) is { } nativeRoot)
            root = nativeRoot;
        if (!start && !LanguageServers.Running.Any(server => server.Name == registration.Name && server.Failure is null && PlatformPaths.Same(server.RootDirectory, root)))
        {
            why = $"{registration.Name}'s server isn't running for {root} yet.";
            return null;
        }

        string? solution = null;
        if (registration.Arguments.Any(argument => argument.Contains("{solution}", StringComparison.Ordinal)))
            solution = context.Workspace.Solution is { } selected && PlatformPaths.Same(Path.GetDirectoryName(selected)!, root) ? selected : LanguageServers.SolutionIn(root);
        if (LanguageServers.For(serverPath, root, solution, ClangdArguments(registration, root), PythonServerSettings(registration, full, root)) is not { } server)
        {
            why = $"{registration.Name}'s server ({registration.Command}) couldn't be started, it may not be installed.";
            return null;
        }

        server.Woken ??= WakeForLinting;
        context.Workspace.Rewatch();
        if (agentFiles.Count >= MOST_AGENT_FILES && agentFiles.MaxBy(pair => pair.Value.Idle) is { Key: { } oldest })
            LetGo(oldest);
        agentFiles[full] = new AgentFile(server, info.Length, info.LastWriteTimeUtc.Ticks);
        server.Open(full, ReadText(full));
        return server;
    }

    /// <summary>
    /// A file opened for an agent sent to its server again as it is, for one that took the file while it was still
    /// starting and hasn't said anything of it since (a change makes a server look at it again).
    /// </summary>
    internal void AgentNudge(string path)
    {
        string full = Path.GetFullPath(path);
        if (agentFiles.TryGetValue(full, out AgentFile? file) && file.Server.Failure is null && file.Server.IsReady)
            file.Server.Change(full, ReadText(full));
    }

    /// <summary>A file an agent changed on the disk told to the server it's open in for an agent, at once rather than at its next question.</summary>
    internal void AgentFileWritten(string path)
    {
        string full = Path.GetFullPath(path);
        if (agentFiles.ContainsKey(full))
            AgentServer(full, start: false, out _);
    }

    // Every tick there are any, the files opened for agents that went to a tab of their own, or unasked too long, let go
    private void UpdateAgentFiles(float dt)
    {
        List<string>? going = null;
        foreach (var (path, file) in agentFiles)
        {
            file.Idle += dt;
            bool tabbed = context.Documents.Find(path) is { } open && linters.ContainsKey(open);
            if (tabbed || file.Idle > AGENT_FILE_IDLE || file.Server.Failure is not null)
                (going ??= []).Add(path);
        }

        foreach (string path in going ?? [])
            LetGo(path);
    }

    // A file opened for agents closed in its server, unless a tab has it open there now (its own didOpen took it over)
    private void LetGo(string path)
    {
        if (!agentFiles.Remove(path, out AgentFile? file))
            return;
        bool tabbed = context.Documents.Find(path) is { } open && linters.TryGetValue(open, out LanguageServer? server) && ReferenceEquals(server, file.Server);
        if (!tabbed && file.Server.Failure is null)
            file.Server.Close(path);
    }

    private static string ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
