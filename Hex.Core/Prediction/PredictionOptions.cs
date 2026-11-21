namespace Hex.Core;

/// <summary>
/// What the settings say about edit prediction: whether it's on, which kind of server (ollama, openai or link), its URL,
/// the key, the model, the prompt format (infer works it out from the model's name), the most tokens an answer may have
/// and how long the typing has to pause before one is asked for.
/// </summary>
internal sealed record PredictionOptions(
    bool On,
    string Provider,
    string ApiUrl,
    string? ApiKey,
    string Model,
    string Format,
    int MaxTokens,
    int DebounceMs)
{
    /// <summary>The key is left out, so that writing the options somewhere never writes it.</summary>
    public override string ToString() =>
        $"PredictionOptions {{ On = {On}, Provider = {Provider}, ApiUrl = {ApiUrl}, Model = {Model}, Format = {Format}, MaxTokens = {MaxTokens}, DebounceMs = {DebounceMs} }}";
}
