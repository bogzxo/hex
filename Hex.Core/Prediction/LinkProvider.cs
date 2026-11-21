using System.Runtime.CompilerServices;

namespace Hex.Core;

/// <summary>
/// Predictions from a device attached over Hex Link that has a model: the prompt built here goes to it through
/// <see cref="HexPredictionRelay.Remote"/> and its model's text streams back. Refused when no such device is attached,
/// and then nothing is sent anywhere else.
/// </summary>
internal sealed class LinkProvider : IPredictionProvider
{
    public async IAsyncEnumerable<string> Complete(PredictionWireRequest request, [EnumeratorCancellation] CancellationToken cancel)
    {
        var remote = HexPredictionRelay.Remote
            ?? throw new PredictionException(PredictionStatus.Refused, "No device that shares its predictions is attached over Hex Link.");

        await foreach (string piece in remote(request, cancel).WithCancellation(cancel).ConfigureAwait(false))
            yield return piece;
    }
}
