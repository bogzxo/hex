namespace Hex.Core;

/// <summary>A server that runs a model, or the device over Hex Link that reaches one.</summary>
public interface IPredictionProvider
{
    /// <summary>
    /// The text the model writes, in pieces as it arrives. Throws <see cref="PredictionException"/> for what went wrong
    /// and <see cref="OperationCanceledException"/> when cancelled; cancelling, or leaving the loop early, closes the
    /// connection, which is what stops the server generating.
    /// </summary>
    IAsyncEnumerable<string> Complete(PredictionWireRequest request, CancellationToken cancel);
}
