namespace Hex.Core;

internal sealed partial class HexLink
{
    private HexLinkCoalesce? workspaceNotices;

    /// <summary>Ask viewers to refresh after folders or repository heads change. The hint carries no workspace data.</summary>
    internal void WorkspaceMoved() =>
        LazyInitializer.EnsureInitialized(ref workspaceNotices, () => new HexLinkCoalesce(TimeSpan.FromMilliseconds(500), SendWorkspaceNotice)).Poke();

    private Task SendWorkspaceNotice()
    {
        foreach (Joined joined in All())
            if (HexLinkAllowed.Has(joined.Host.Rights, HexLinkRights.View))
                joined.Session.Tell("workspace/changed", new());
        return Task.CompletedTask;
    }
}
