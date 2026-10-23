namespace Hex.Dawn;

// A server that stopped while it was working. What it said about its files goes (it isn't there to take any of it back,
// and the Problems kept warnings it no longer stood by), it's started again by itself once a run, and the second time
// the card in the corner says so with a button to start it. serve-d died over one message and nobody was told, its
// file went on saying D, serve-d with nothing behind it.
internal sealed partial class HexLanguages
{
    // The servers started again by themselves after stopping, once each a run, a server that stops as it starts isn't
    // started for ever
    private readonly HashSet<string> restartedOnce = new(StringComparer.OrdinalIgnoreCase);

    // The servers that stopped this tick, dealt with after going through them, which starting one again changes
    private readonly List<(LanguageServer Server, string Why)> stoppedNow = [];

    /// <summary>How many times a server started or stopped, for what says which is linting a file to say it again.</summary>
    internal int ServersChanged { get; private set; }

    private void ServersStopped()
    {
        if (stoppedNow.Count == 0)
            return;

        var stopped = stoppedNow.ToList();
        stoppedNow.Clear();
        foreach (var (server, why) in stopped)
            ServerStopped(server, why);
    }

    private void ServerStopped(LanguageServer server, string why)
    {
        Console.WriteLine($"[Hex] {server.Name} stopped, {why}");
        ServersChanged++;

        foreach (HexDocument of in linters.Where(pair => pair.Value == server).Select(pair => pair.Key).ToList())
        {
            if (lintFound.Remove(of))
                context.Documents.DiagnosticsChanged(of);
        }

        // One that never got going isn't working to have stopped, what's missing about it is said where it's looked for
        if (!server.IsReady || ServerEntry(server.Name) is not { } entry)
            return;

        string shown = Path.GetFileNameWithoutExtension(CommandOf(entry));
        if (restartedOnce.Add(server.Name))
        {
            context.Notices.Say(context.Strings.Said("languages.server_restarted_after", ("server", shown)));
            RestartServer(entry);
            return;
        }

        context.Notices.Notify(context.Strings.Said("languages.server_stopped", ("server", shown), ("why", why)), error: true,
            (context.Strings.Said("languages.start_again"), () => RestartServer(entry)));
    }

    /// <summary>Whether the server linting a document stopped, for the status bar to say so.</summary>
    internal bool ServerStoppedFor(HexDocument of) => linters.TryGetValue(of, out LanguageServer? server) && server.Failure is not null;
}
