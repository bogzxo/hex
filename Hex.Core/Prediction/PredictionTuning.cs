namespace Hex.Core;

/// <summary>
/// The model's own parameters as the settings tune them. Zero (or <c>model</c> for thinking) is the model's or the
/// server's own value, which is then never sent. Temperature is always sent, 0 as it comes so a prediction is the same
/// each time it's asked.
/// </summary>
/// <param name="Temperature">0 to 2, how freely the next token is picked.</param>
/// <param name="TopP">0 to 1, nucleus sampling; 0 is the model's own.</param>
/// <param name="TopK">0 to 200, the most likely tokens picked from; 0 is the model's own.</param>
/// <param name="Context">The context window in tokens (Ollama's num_ctx); 0 is the server's own.</param>
/// <param name="RepeatPenalty">0 to 2, how much repeating is held back; 0 is the model's own.</param>
/// <param name="Thinking">model, off, on, low, medium or high. Only Link Chat's answers think, a prediction never does.</param>
internal sealed record PredictionTuning(
    float Temperature = 0,
    float TopP = 0,
    int TopK = 0,
    int Context = 0,
    float RepeatPenalty = 0,
    string Thinking = PredictionTuning.THINK_MODEL)
{
    public const string THINK_MODEL = "model", THINK_OFF = "off", THINK_ON = "on";

    /// <summary>Every value thinking may have, as the settings write it.</summary>
    public static readonly string[] Thinkings = [THINK_MODEL, THINK_OFF, THINK_ON, "low", "medium", "high"];

    /// <summary>The values as they come, the ones predictions were sent with before they could be tuned.</summary>
    public static readonly PredictionTuning Usual = new();

    /// <summary>The tuning with every value held to its ends and an unknown thinking taken as the model's own.</summary>
    public PredictionTuning Clamped() => new(
        Math.Clamp(float.IsFinite(Temperature) ? Temperature : 0, 0, 2),
        Math.Clamp(float.IsFinite(TopP) ? TopP : 0, 0, 1),
        Math.Clamp(TopK, 0, 200),
        Math.Clamp(Context, 0, 262_144),
        Math.Clamp(float.IsFinite(RepeatPenalty) ? RepeatPenalty : 0, 0, 2),
        Array.IndexOf(Thinkings, Thinking) >= 0 ? Thinking : THINK_MODEL);

    /// <summary>Writes the sampling values into an Ollama options object already started, the ones left at 0 not at all.</summary>
    public void WriteOllamaOptions(System.Text.Json.Utf8JsonWriter writer)
    {
        writer.WriteNumber("temperature", Temperature);
        if (TopP > 0)
            writer.WriteNumber("top_p", TopP);
        if (TopK > 0)
            writer.WriteNumber("top_k", TopK);
        if (Context > 0)
            writer.WriteNumber("num_ctx", Context);
        if (RepeatPenalty > 0)
            writer.WriteNumber("repeat_penalty", RepeatPenalty);
    }
}
