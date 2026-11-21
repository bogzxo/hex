using System.Runtime.CompilerServices;
using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// Predictions through Hex Link in one process: the device without a model sends its prompt through the relay, the one
/// with it serves it from its own provider. The relay is static, so these run one at a time in their own collection.
/// </summary>
[Collection(NAME)]
public sealed class PredictionRelayTests : IDisposable
{
    public const string NAME = "prediction relay";

    private static readonly PredictionWireRequest request = new("PROMPT", null, 5000, [], "qwen");

    public void Dispose()
    {
        HexPredictionRelay.Remote = null;
        HexPredictionRelay.RemoteName = null;
        HexPredictionRelay.ProviderOverride = null;
        HexPredictionRelay.Configure(() => null, () => false);
    }

    private static async Task<List<string>> Read(IPredictionProvider provider, CancellationToken cancel = default)
    {
        var pieces = new List<string>();
        await foreach (string piece in provider.Complete(request, cancel))
            pieces.Add(piece);
        return pieces;
    }

    [Fact]
    public async Task A_request_is_served_and_streamed_back_through_the_link()
    {
        var served = new ScriptedProvider("a", "b");
        HexPredictionRelay.ProviderOverride = served;
        HexPredictionRelay.Configure(() => PredictionTestKit.Options(), () => true);
        HexPredictionRelay.Remote = HexPredictionRelay.Serve;

        Assert.True(HexPredictionRelay.Serving);
        Assert.Equal(["a", "b"], await Read(new LinkProvider()));
        Assert.Equal(2048, served.Last!.MaxTokens);
    }

    [Fact]
    public async Task Cancelling_on_the_device_without_the_model_reaches_the_provider()
    {
        var served = new ScriptedProvider((_, cancel) => ScriptedProvider.Pieces(["a"], true, cancel));
        HexPredictionRelay.ProviderOverride = served;
        HexPredictionRelay.Configure(() => PredictionTestKit.Options(), () => true);
        HexPredictionRelay.Remote = HexPredictionRelay.Serve;
        using var cancel = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (string _ in new LinkProvider().Complete(request, cancel.Token))
                cancel.Cancel();
        });

        Assert.Equal(1, served.Cancelled);
    }

    [Fact]
    public async Task Serving_is_refused_when_predictions_arent_shared()
    {
        HexPredictionRelay.ProviderOverride = new ScriptedProvider("a");
        HexPredictionRelay.Configure(() => PredictionTestKit.Options(), () => false);

        Assert.False(HexPredictionRelay.Serving);
        var failure = await Assert.ThrowsAsync<PredictionException>(async () =>
        {
            await foreach (string _ in HexPredictionRelay.Serve(request, default))
            {
            }
        });
        Assert.Equal(PredictionStatus.Refused, failure.Status);
    }

    [Fact]
    public void A_device_whose_own_provider_is_the_link_doesnt_serve()
    {
        HexPredictionRelay.Configure(() => PredictionTestKit.Options() with { Provider = "link" }, () => true);

        Assert.False(HexPredictionRelay.Serving);
    }

    [Fact]
    public async Task With_no_device_attached_the_link_is_refused_and_nothing_is_sent()
    {
        var failure = await Assert.ThrowsAsync<PredictionException>(() => Read(new LinkProvider()));

        Assert.Equal(PredictionStatus.Refused, failure.Status);
    }
}

[CollectionDefinition(PredictionRelayTests.NAME, DisableParallelization = true)]
public sealed class PredictionRelayCollection
{
}
