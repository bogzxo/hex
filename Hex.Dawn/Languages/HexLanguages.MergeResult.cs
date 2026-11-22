using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

internal sealed partial class HexLanguages
{
    private readonly Dictionary<HexDocument, MergeResultLanguageSession> mergeResults = [];

    internal void AttachMergeResult(HexDocument result, string originalPath)
    {
        if (result.Path is not null || result.Editor.ReadOnly || result.Editor.Highlighter is null
            || ReferenceEquals(result.Editor.Highlighter, CodeLanguages.Hidl) || mergeResults.ContainsKey(result))
            return;

        if (!serversRead)
            ReadLanguageServers();

        string path = Path.GetFullPath(originalPath);
        if (LanguageServers.RegistrationFor(ServerPathFor(result, path)) is not { } registration)
            return;

        string root = ServerRootFor(path);
        if (UsesClangd(registration) && CMakeProject.RootOf(path, context.Workspace.Roots) is { } nativeRoot)
            root = nativeRoot;

        var server = LanguageServers.StartIsolated(registration, root, SolutionFor(root),
            ClangdArguments(registration, root), PythonServerSettings(registration, path, root));
        if (server is null)
            return;

        server.Woken = WakeForLinting;
        mergeResults.Add(result, new MergeResultLanguageSession(path, server));
        ServersChanged++;
        context.Wake();
    }

    internal void DetachMergeResult(HexDocument result)
    {
        if (mergeResults.ContainsKey(result))
            StopLinting(result);
    }

    private void StopMergeResult(HexDocument result)
    {
        if (!mergeResults.Remove(result, out var session))
            return;

        ServersChanged++;
        session.Server.Woken = null;
        _ = Task.Run(session.Dispose);
    }

    // This path is for LSP requests only. The tab stays pathless, including when it closes.
    private string? LanguagePathOf(HexDocument of) => mergeResults.GetValueOrDefault(of)?.Path ?? of.Path;

    private LanguageServer? RequestServerOf(HexDocument of) =>
        mergeResults.GetValueOrDefault(of) is { Opened: true } session ? session.Server : linters.GetValueOrDefault(of);

    private void SynchronizeRequest(HexDocument of, LanguageServer server, string path)
    {
        if (mergeResults.TryGetValue(of, out var session))
            SynchronizeMergeResult(of, session);
        else if (lintIn.Remove(of))
            server.Change(path, of.Editor.Text);
    }

    private void SynchronizeMergeResult(HexDocument result, MergeResultLanguageSession session)
    {
        int version = result.Editor.Document.Version;
        if (session.IsCurrent(version))
            return;
        if (lintFound.Remove(result))
            context.Documents.DiagnosticsChanged(result);
        session.Send(version, result.Editor.Text);
    }

    private string? MergeSourceLine(HexDocument? result, LanguageServerPlace place)
    {
        if (result is null || !place.OnDisk || !PlatformPaths.Comparer.Equals(LanguagePathOf(result), place.Path))
            return null;
        var document = result.Editor.Document;
        return place.Line >= 0 && place.Line < document.LineCount ? document.LineAt(place.Line) : string.Empty;
    }

    private bool GoToMergeLocation(HexDocument result, LanguageServerPlace place)
    {
        if (!place.OnDisk || !mergeResults.TryGetValue(result, out var session)
            || !PlatformPaths.Comparer.Equals(session.Path, place.Path))
            return false;

        result.Editor.GoTo(place.Line, place.Character);
        return true;
    }

    private void UpdateMergeResults(float dt)
    {
        foreach (var (result, session) in mergeResults)
        {
            if (session.Server.Failure is not null)
            {
                DetachMergeResult(result);
                context.Documents.DiagnosticsChanged(result);
                continue;
            }

            while (session.Server.TryTakeQuestion(out var question))
                question.Answer(null);
            while (session.Server.TryTakeSaid(out _, out _))
            {
            }

            if (!session.Server.IsReady)
                continue;

            int version = result.Editor.Document.Version;
            if (session.Observe(version, dt, LINT_DELAY))
                SynchronizeMergeResult(result, session);

            if (!session.IsCurrent(version) && lintFound.Remove(result))
                context.Documents.DiagnosticsChanged(result);

            if (session.TakeDiagnostics(version) is { } found)
            {
                lintFound[result] = found;
                context.Documents.DiagnosticsChanged(result);
            }
        }
    }
}

/// <summary>Owns the protocol document for a single scratch Result buffer.</summary>
internal sealed class MergeResultLanguageSession(string path, LanguageServer server) : IDisposable
{
    private int? sentVersion;
    private int? observedVersion;
    private float remaining;
    private bool disposed;

    internal string Path { get; } = path;
    internal LanguageServer Server { get; } = server;
    internal bool Opened => sentVersion is not null;
    internal bool IsCurrent(int version) => sentVersion == version;

    internal bool Observe(int version, float elapsed, float delay)
    {
        if (!Opened)
            return true;
        if (IsCurrent(version))
            return false;
        if (observedVersion != version)
        {
            observedVersion = version;
            remaining = delay;
        }

        remaining -= elapsed;
        return remaining <= 0;
    }

    internal void Send(int version, string text)
    {
        if (!Opened)
            Server.Open(Path, text);
        else if (!IsCurrent(version))
            Server.Change(Path, text);
        sentVersion = version;
    }

    internal IReadOnlyList<UICodeDiagnostic>? TakeDiagnostics(int version)
    {
        IReadOnlyList<UICodeDiagnostic>? latest = null;
        while (Server.TryTake(out string path, out var found))
            if (IsCurrent(version) && PlatformPaths.Comparer.Equals(path, Path))
                latest = found;
        return latest;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        if (Opened)
            Server.Close(Path);
        Server.Dispose();
    }
}
