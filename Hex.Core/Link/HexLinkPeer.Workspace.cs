using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkPeer
{
    internal Action? WorkspaceChanged { get; set; }
    private HexLinkCoalesce? workspaceRefresh;
    private readonly SemaphoreSlim workspaceRefreshGate = new(1, 1);

    /// <summary>Gets the workspace now, without changing the peer's identity, rights or other hello state.</summary>
    internal async Task RefreshWorkspace(CancellationToken cancel = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel, session.Closing);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await workspaceRefreshGate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            int revision = Volatile.Read(ref viewRevision);
            if (!HexLinkAllowed.Has(Granted, HexLinkRights.View))
                throw HexLinkException.NotAllowed("view");

            JsonObject hello = await AskObject("link/hello", null, deadline.Token).ConfigureAwait(false);
            if (revision != Volatile.Read(ref viewRevision) || !HexLinkAllowed.Has(Granted, HexLinkRights.View)
                || !HexLinkAllowed.Has(HexLinkData.RightsOf(hello, "granted"), HexLinkRights.View))
                throw HexLinkException.NotAllowed("view");

            HexLinkWorkspace fresh = WorkspaceOf(HexLinkData.Object(hello, "workspace"));
            bool choicesChanged = TakeSyncChoices(hello);
            bool workspaceChanged = !SameWorkspace(Workspace, fresh);
            if (workspaceChanged)
            {
                Workspace = fresh;
                WorkspaceChanged?.Invoke();
            }
            if (workspaceChanged || choicesChanged)
                Changed?.Invoke();
        }
        finally
        {
            workspaceRefreshGate.Release();
        }
    }

    private bool TakeSyncChoices(JsonObject hello)
    {
        var chosen = HexLinkData.Strings(hello, "syncChosen", HexLinkHost.MOST_TREES, 200);
        HexLinkSyncPause[] paused = [.. HexLinkData.Objects(hello, "syncPaused", HexLinkHost.MOST_TREES)
            .Select(item => (Folder: HexLinkData.String(item, "folder", 200), By: HexLinkData.String(item, "by", 120)))
            .Where(item => item.Folder is { Length: > 0 })
            .Select(item => new HexLinkSyncPause(item.Folder!, item.By))];
        bool pauseChanged = !PausedSyncFolders.SequenceEqual(paused);
        // The colour the device picked for itself, #rrggbb, said with its choices so a change of it travels the same way
        string? colour = HexLinkData.String(hello, "colour", 7) is { Length: 7 } said && said[0] == '#' && said[1..].All(char.IsAsciiHexDigit) ? said : null;
        bool changed = pauseChanged || !ChosenSyncFolders.SequenceEqual(chosen) || colour != Colour;
        Colour = colour;
        ChosenSyncFolders = chosen;
        PausedSyncFolders = paused;
        if (pauseChanged)
            SyncPausesChanged?.Invoke();
        return changed;
    }

    internal static bool SameWorkspace(HexLinkWorkspace one, HexLinkWorkspace other) =>
        one.Name == other.Name && one.Folders.SequenceEqual(other.Folders) && one.Repositories.SequenceEqual(other.Repositories);

    private void PokeWorkspaceRefresh()
    {
        if (!HexLinkAllowed.Has(Granted, HexLinkRights.View))
            return;
        GitFollowHint?.Invoke();
        LazyInitializer.EnsureInitialized(ref workspaceRefresh, () => new HexLinkCoalesce(TimeSpan.FromSeconds(1),
            () => RefreshWorkspace(session.Closing))).Poke();
    }
}
