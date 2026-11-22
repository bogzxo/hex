using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkPeer
{
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
            if (!SameWorkspace(Workspace, fresh))
            {
                Workspace = fresh;
                Changed?.Invoke();
            }
        }
        finally
        {
            workspaceRefreshGate.Release();
        }
    }

    internal static bool SameWorkspace(HexLinkWorkspace one, HexLinkWorkspace other) =>
        one.Name == other.Name && one.Folders.SequenceEqual(other.Folders) && one.Repositories.SequenceEqual(other.Repositories);

    private void PokeWorkspaceRefresh()
    {
        if (HexLinkAllowed.Has(Granted, HexLinkRights.View))
            LazyInitializer.EnsureInitialized(ref workspaceRefresh, () => new HexLinkCoalesce(TimeSpan.FromSeconds(1),
                () => RefreshWorkspace(session.Closing))).Poke();
    }
}
