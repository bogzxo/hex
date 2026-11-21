namespace Hex.Core;

/// <summary>
/// What is sent to a model, built and ready: the raw prompt (the format's tokens already in it), a suffix for servers
/// that put fill-in-the-middle together themselves (null when the prompt has it), the most tokens it may write, where it
/// stops, and the name of the format that built it. This is also what goes over Hex Link to a device that has the model.
/// </summary>
public sealed record PredictionWireRequest(string Prompt, string? Suffix, int MaxTokens, string[] Stop, string Format);
