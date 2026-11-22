using System.Runtime.CompilerServices;

namespace Hex.Dawn;

// Edit predictions and Hex Link joined (docs/hex-link-plan.md 5.6, docs/edit-prediction-plan.md 3.6). Link knows
// nothing of the prediction side's types and the prediction side nothing of Link's; this is the one place that knows
// both. On the machine with the model, Link's LinkPredictServing and LinkPredictServe are the relay's Serving and
// Serve, its PredictionException said over the wire as Link's prediction failed error with the status in a word. On
// the machine without one, the relay's Remote is the linked device Link says serves this one (LinkPredictSource),
// Link's errors read back into PredictionExceptions the status bar can say, and RemoteName is that device's name.
internal sealed partial class DawnScene
{
    private bool linkPredictionsBound;

    private void ApplyLinkPredictions()
    {
        bool wanted = LinkOn && !HexAi.Off && (settings.EditPredictions || settings.EditPredictionsShare);
        if (wanted == linkPredictionsBound)
            return;
        if (wanted)
            BindLinkPredictions();
        else
            UnbindLinkPredictions();
        linkPredictionsBound = wanted;
    }

    private void UnbindLinkPredictions()
    {
        LinkPredictSourceChanged -= LinkPredictTakeSource;
        LinkPredictServe = null;
        LinkPredictServing = null;
        LinkPredictTakeSource(null);
        HexPredictionRelay.Configure(null, null);
    }

    /// <summary>Connect an enabled predictor or explicitly shared model to this machine's Link service.</summary>
    private void BindLinkPredictions()
    {
        HexPredictionRelay.Configure(() => MakeShareOptions(settings), () => !HexAi.Off && PredictionSettings.Share(settings));
        LinkPredictServing = () => HexPredictionRelay.Serving;
        LinkPredictServe = LinkPredictionGlue.Serve;
        LinkPredictSourceChanged -= LinkPredictTakeSource;
        LinkPredictSourceChanged += LinkPredictTakeSource;
        LinkPredictTakeSource(LinkPredictSource);
    }

    // The options this machine's model is shared with: its provider and model as set up, whether or not predictions are
    // switched on for its own typing. A machine that only lends its model to paired devices refused them all before.
    // Never Hex Link itself, a device asked over a link is never passed on to another
    internal static PredictionOptions? MakeShareOptions(HexSettings from) =>
        !HexAi.Off && PredictionSettings.Configured(from) && from.EditPredictionsProvider != PredictionProviders.LINK
            ? new PredictionOptions(true, from.EditPredictionsProvider, from.EditPredictionsApiUrl, PredictionSettings.ApiKey(from),
                from.EditPredictionsModel, from.EditPredictionsPromptFormat, from.EditPredictionsMaxTokens, from.EditPredictionsDebounce)
            : null;

    // The device that serves this one now, or none, handed to the relay; on the editor's thread
    private void LinkPredictTakeSource(HexLinkPeer? source)
    {
        HexPredictionRelay.Remote = source is null ? null : LinkPredictionGlue.Remote(source);
        HexPredictionRelay.RemoteName = source?.Device.Name;
        if (settings.EditPredictionsProvider != PredictionProviders.LINK)
            return;

        predictionSession?.Engine.LinkSourceChanged();
        predictionItemWritten = false;
        if (predictionShownIn is { } shown)
        {
            predictionShownIn = null;
            shown.Editor.ClearPrediction();
        }
    }
}

/// <summary>
/// What a prediction is on each side of a link: the relay's request and Link's, and the relay's failures and Link's
/// errors, each read into the other. Static and free of the scene, so the tests run it over a link in memory.
/// </summary>
internal static class LinkPredictionGlue
{
    /// <summary>The relay's request as Link carries it, no suffix as an empty one.</summary>
    public static HexLinkPrediction ToLink(PredictionWireRequest request) =>
        new(request.Prompt, request.Suffix ?? string.Empty, request.MaxTokens, request.Stop, request.Format);

    /// <summary>Link's request as the relay takes it, an empty suffix as none.</summary>
    public static PredictionWireRequest ToRelay(HexLinkPrediction request) =>
        new(request.Prompt, request.Suffix.Length > 0 ? request.Suffix : null, request.MaxTokens, [.. request.Stop], request.Format);

    /// <summary>A status in the word the wire says it by, lower case, Refused as refused.</summary>
    public static string Word(PredictionStatus status) => status.ToString().ToLowerInvariant();

    /// <summary>A status read back from its word, BadAnswer for one this side doesn't know.</summary>
    public static PredictionStatus StatusOf(string word) =>
        Enum.TryParse(word, ignoreCase: true, out PredictionStatus status) && Enum.IsDefined(status) && !int.TryParse(word, out _) ? status : PredictionStatus.BadAnswer;

    /// <summary>
    /// On the machine with the model, what Link's LinkPredictServe is: the relay's Serve, whose PredictionException
    /// becomes Link's prediction failed error with its status, from the call or from the stream.
    /// </summary>
    public static async IAsyncEnumerable<string> Serve(HexLinkPrediction request, [EnumeratorCancellation] CancellationToken cancel)
    {
        IAsyncEnumerator<string> text;
        try
        {
            text = HexPredictionRelay.Serve(ToRelay(request), cancel).GetAsyncEnumerator(cancel);
        }
        catch (PredictionException e)
        {
            throw HexLinkPrediction.Failed(e.Message, Word(e.Status));
        }

        await using (text.ConfigureAwait(false))
        {
            while (true)
            {
                bool more;
                try
                {
                    more = await text.MoveNextAsync().ConfigureAwait(false);
                }
                catch (PredictionException e)
                {
                    throw HexLinkPrediction.Failed(e.Message, Word(e.Status));
                }

                if (!more)
                    yield break;
                yield return text.Current;
            }
        }
    }

    /// <summary>On the machine without a model, the relay's Remote over a linked device that serves this one.</summary>
    public static Func<PredictionWireRequest, CancellationToken, IAsyncEnumerable<string>> Remote(HexLinkPeer source) =>
        (request, cancel) => Asked(source, request, cancel);

    // The device asked, Link's errors read back into what the prediction side knows: a right not granted or a machine
    // that doesn't share is refused, a link that went is unreachable, one that didn't answer timed out, the model's own
    // failure the status it said. A cancel stays a cancel.
    private static async IAsyncEnumerable<string> Asked(HexLinkPeer source, PredictionWireRequest request, [EnumeratorCancellation] CancellationToken cancel)
    {
        await using IAsyncEnumerator<string> text = source.Predict(ToLink(request), cancel).GetAsyncEnumerator(cancel);
        while (true)
        {
            bool more;
            try
            {
                more = await text.MoveNextAsync().ConfigureAwait(false);
            }
            catch (HexLinkException e)
            {
                throw new PredictionException(e.Code switch
                {
                    HexLinkErrors.NOT_ALLOWED => PredictionStatus.Refused,
                    HexLinkErrors.PREDICTION_FAILED => StatusOf(HexLinkPrediction.StatusOf(e)),
                    HexLinkErrors.CLOSED => PredictionStatus.Unreachable,
                    HexLinkErrors.TIMED_OUT => PredictionStatus.TimedOut,
                    HexLinkErrors.BUSY => PredictionStatus.Unreachable,
                    _ => PredictionStatus.BadAnswer
                }, $"{source.Device.Name}: {e.Message}");
            }

            if (!more)
                yield break;
            yield return text.Current;
        }
    }
}
