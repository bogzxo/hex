namespace Hex.Core;

/// <summary>A model family's way of being asked: how its prompt is written and how its answer is read.</summary>
internal interface IPredictionFormat
{
    /// <summary>The name the settings use for it, zeta2 or qwen.</summary>
    string Name { get; }

    /// <summary>
    /// The prompt for the input, its answer allowed at most <paramref name="maxTokens"/> tokens, or null when nothing
    /// should be asked here (the file holds the format's own markers, the caret is in the middle of a word).
    /// </summary>
    PredictionPrompt? Build(PredictionInput input, int maxTokens);

    /// <summary>
    /// What the answer so far comes to. <paramref name="complete"/> is whether the stream has ended; before it has, the
    /// answer is read only far enough to see whether the rest is needed.
    /// </summary>
    PredictionParsed Parse(PredictionPrompt prompt, string output, bool complete);
}
