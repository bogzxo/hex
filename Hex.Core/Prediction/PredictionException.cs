namespace Hex.Core;

/// <summary>
/// A prediction that couldn't be had: the status it leaves and a sentence saying why. Neither the key nor the prompt is
/// ever in the sentence.
/// </summary>
public sealed class PredictionException(PredictionStatus status, string message) : Exception(message)
{
    public PredictionStatus Status { get; } = status;
}
