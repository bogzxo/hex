namespace Hex.Core;

/// <summary>
/// A prompt as built, with what its answer is read against: the part of the text the model may rewrite (from
/// RegionStart to RegionEnd, its old text in Region, no cursor marker in it; empty at the caret for fill-in-the-middle)
/// and the caret it was built for.
/// </summary>
internal sealed record PredictionPrompt(PredictionWireRequest Request, int RegionStart, int RegionEnd, string Region, int Caret);
