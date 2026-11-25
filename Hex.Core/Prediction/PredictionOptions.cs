namespace Hex.Core;

/// <summary>
/// What the settings say about edit prediction: whether it's on, which kind of server (ollama, openai or link), its URL,
/// the key, the model, the prompt format (infer works it out from the model's name), the most tokens an answer may have
/// and how long the typing has to pause before one is asked for. The model's tuning and the system prompt come with
/// them, the usual tuning and none unless the settings say.
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
    /// <summary>The model's parameters, temperature, sampling, context and thinking.</summary>
    public PredictionTuning Tuning { get; init; } = PredictionTuning.Usual;

    /// <summary>
    /// The system prompt, empty for none. Sent where the server wraps the prompt in the model's template (Ollama with a
    /// suffix), never with a raw prompt, which has no place for one.
    /// </summary>
    public string SystemPrompt { get; init; } = string.Empty;

    /// <summary>The kind of model: auto works it out from its name and what the server says, or edit_model, fim, instruct, fim_instruct.</summary>
    public string Kind { get; init; } = PredictionModels.AUTO;

    /// <summary>
    /// The llama-server slot to send prompts to (id_slot), or -1 for the server's choice. Set only when one slot is
    /// configured: with several, which is free is the server's to say.
    /// </summary>
    public int Slot { get; init; } = -1;

    /// <summary>Whether the model is asked for edits away from the caret (edit_predictions_far).</summary>
    public bool Far { get; init; } = true;

    /// <summary>The key is left out, so that writing the options somewhere never writes it.</summary>
    public override string ToString() =>
        $"PredictionOptions {{ On = {On}, Provider = {Provider}, ApiUrl = {ApiUrl}, Model = {Model}, Format = {Format}, Kind = {Kind}, MaxTokens = {MaxTokens}, DebounceMs = {DebounceMs}, Tuning = {Tuning}, SystemPrompt = {SystemPrompt.Length} characters }}";
}
