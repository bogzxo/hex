namespace Hex.Dawn;

// Predictions over a link (docs/hex-link-plan.md, 5.6), what Link offers the edit prediction side both ways without
// knowing its types. As the machine with the model: LinkPredictServing says whether it shares its predictions and
// LinkPredictServe makes one, both bound by the prediction side and called off the editor's thread by the links. As
// the machine without one: LinkPredictSource is the first linked device that would make predictions for this one,
// kept current as links come and go and as what they serve changes, and LinkPredictSourceChanged says when it moved.
internal sealed partial class DawnScene
{
    /// <summary>Whether this machine shares its predictions now (the setting, and a model it can reach). Null shares nothing.</summary>
    internal Func<bool>? LinkPredictServing { get; set; }

    /// <summary>
    /// A prediction made with this machine's model for a linked device, its text as it comes. Throws a HexLinkException
    /// (PREDICTION_FAILED with data.status, HexLinkPrediction.Failed) when the model couldn't make it.
    /// </summary>
    internal Func<HexLinkPrediction, CancellationToken, IAsyncEnumerable<string>>? LinkPredictServe { get; set; }

    /// <summary>The linked device whose model this one's autocomplete may use, the first that serves it, null for none. On the editor's thread.</summary>
    internal HexLinkPeer? LinkPredictSource { get; private set; }

    /// <summary><see cref="LinkPredictSource"/> moved, on the editor's thread, with what it is now.</summary>
    internal event Action<HexLinkPeer?>? LinkPredictSourceChanged;

    // What the links were told of this machine's sharing last, so they're told again only when it changed
    private bool linkPredictServed;

    // Off the editor's thread, from the links: a binding that throws shares nothing rather than breaking the link
    private bool LinkPredictServes()
    {
        try
        {
            return LinkPredictServing?.Invoke() == true && LinkPredictServe is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private IAsyncEnumerable<string> LinkPredictMake(HexLinkPrediction request, CancellationToken cancel) =>
        LinkPredictServe is { } serve && LinkPredictServes() ? serve(request, cancel) : HexLinkPrediction.NotServed();

    /// <summary>
    /// Once a second while Link is up: whether this machine shares its predictions looked at, and the links told when it
    /// changed (the setting, or the model here coming and going), each device that may ask hearing link/serves.
    /// </summary>
    private void LinkPredictLookServing()
    {
        bool serving = LinkPredictServes();
        if (serving == linkPredictServed)
            return;
        linkPredictServed = serving;
        link?.PredictionsMoved();
    }

    /// <summary>
    /// Which linked device serves this one's predictions, looked at when anything of Link's moved: the one already
    /// used while it still serves, otherwise the first linked that does (the oldest link), none when none does.
    /// </summary>
    private void LinkPredictLookSource()
    {
        IReadOnlyList<HexLinkPeer> peers = LinkPeers;
        HexLinkPeer? now = LinkPredictSource is { ServesPredictions: true } kept && peers.Contains(kept) ? kept : peers.FirstOrDefault(peer => peer.ServesPredictions);
        HexPredictionRelay.WhyNoRemote = now is null ? LinkPredictWhyNone(peers) : null;
        if (now == LinkPredictSource)
            return;
        LinkPredictSource = now;
        LinkPredictSourceChanged?.Invoke(now);
    }

    /// <summary>
    /// Why none of the linked devices serves this one's predictions, said so the user knows what to change and where:
    /// nothing connected, a device that gave this one Autocomplete but doesn't share its model, or one that hasn't given
    /// this one Autocomplete at all. "Predictions refused" with nothing more was all a client saw before.
    /// </summary>
    internal string LinkPredictWhyNone(IReadOnlyList<HexLinkPeer> peers)
    {
        if (peers.Count == 0)
            return Said("prediction.why_no_device");
        if (peers.FirstOrDefault(peer => peer.Granted.HasFlag(HexLinkRights.Predict)) is { } granted)
            return Said("prediction.why_not_shared", ("device", granted.Device.Name));
        return Said("prediction.why_not_granted", ("device", peers[0].Device.Name));
    }
}
