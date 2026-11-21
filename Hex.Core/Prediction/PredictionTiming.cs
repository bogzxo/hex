namespace Hex.Core;

/// <summary>
/// How long a prediction took: to the first piece of the answer and to the end, in milliseconds, and the tokens of
/// prompt (estimated at 3 bytes each) and of answer (the pieces the server sent).
/// </summary>
internal readonly record struct PredictionTiming(double FirstMs, double TotalMs, int PromptTokens, int OutputTokens);
