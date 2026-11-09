namespace Hex.Dawn;

internal sealed partial class HexLanguages
{
    // Retained only by the pending request/menu, never by a global document cache.
    private int editWorkspaceGeneration;
    internal void EditWorkspaceChanged() => editWorkspaceGeneration++;

    private sealed record EditSnapshot(int Generation, string[] Roots, DateTime Requested,
        Dictionary<string, (HexDocument Document, int Version)> Open);

    private EditSnapshot CaptureEdits() => new(editWorkspaceGeneration, [.. context.Workspace.Roots], DateTime.UtcNow,
        context.Documents.All.Where(doc => doc.IsText && doc.Path is not null)
            .ToDictionary(doc => Path.GetFullPath(doc.Path!), doc => (doc, doc.Editor.Document.Version), PlatformPaths.Comparer));

    private bool EditsCurrent(EditSnapshot snapshot, IEnumerable<string> paths)
    {
        bool valid = snapshot.Generation == editWorkspaceGeneration && snapshot.Roots.SequenceEqual(context.Workspace.Roots, PlatformPaths.Comparer);
        foreach (string path in paths.Distinct(PlatformPaths.Comparer))
        {
            if (!valid)
                break;
            string full = Path.GetFullPath(path);
            HexDocument? now = context.Documents.Find(full);
            if (snapshot.Open.TryGetValue(full, out var before))
                valid = ReferenceEquals(now, before.Document) && now.Path is { } at && PlatformPaths.Same(at, full)
                    && now.Editor.Document.Version == before.Version;
            else
            {
                // A closed file may be opened by the user while a response is pending.
                // Its modified buffer must not be overwritten with ranges based on disk.
                valid = File.Exists(full) && File.GetLastWriteTimeUtc(full) <= snapshot.Requested
                    && (now is null || (!now.Modified && !now.CodeEdited));
            }
        }
        if (!valid)
            context.Notices.Say(context.Strings.Said("code_server.edit_conflict"), error: true);
        return valid;
    }

    private void FlushEditsToServer(LanguageServer server)
    {
        foreach (var (doc, owner) in linters)
            if (ReferenceEquals(owner, server) && doc.Path is { } path && lintIn.Remove(doc))
                server.Change(path, doc.Editor.Text);
    }
    private sealed class PendingEditCommand(EditSnapshot snapshot)
    {
        public EditSnapshot Snapshot = snapshot;
        public bool Failed;
    }
    private readonly Dictionary<LanguageServer, PendingEditCommand> commandsPending = [];

    private void ApplyRequested(LanguageServer server, LanguageServerEditRequest request)
    {
        if (request.IsCompleted)
            return;
        bool applied = false;
        string? reason = null;
        try
        {
            bool pendingTyping = request.Edits.Keys.Any(path => context.Documents.Find(path) is { } doc && ChangePending(doc));
            bool changedOutsideServer = request.Edits.Keys.Any(path =>
                server.DocumentVersion(path) is null && (context.Documents.Find(path) is { } doc
                    ? doc.Modified || doc.CodeEdited
                    : !File.Exists(path) || File.GetLastWriteTimeUtc(path) > request.ReceivedAt));
            bool valid = !pendingTyping && !changedOutsideServer && request.VersionsMatch(server.DocumentVersion);
            if (commandsPending.TryGetValue(server, out var snapshot))
                valid &= EditsCurrent(snapshot.Snapshot, request.Edits.Keys);
            if (!valid)
            {
                reason = context.Strings.Said("code_server.edit_conflict");
                context.Notices.Say(reason, error: true);
                return;
            }
            int expectedFiles = request.Edits.Count(pair => pair.Value.Count > 0);
            applied = ApplyEdits(request.Edits).Files == expectedFiles;
            if (!applied)
                reason = context.Strings.Said("code_server.edit_conflict");
            else
            {
                FlushEditsToServer(server);
                if (snapshot is not null)
                    snapshot.Snapshot = CaptureEdits();
            }
        }
        catch (Exception e)
        {
            reason = e.Message;
            context.Notices.Say(reason, error: true);
        }
        finally
        {
            if (!applied && commandsPending.TryGetValue(server, out var command))
                command.Failed = true;
            request.Complete(applied, reason);
        }
    }
}
