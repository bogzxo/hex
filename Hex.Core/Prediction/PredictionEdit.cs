namespace Hex.Core;

/// <summary>
/// One change a prediction would make: the characters from Start to End of the text that was sent (lines ended with
/// '\n') replaced with Text. Start equal to End is an insertion.
/// </summary>
internal readonly record struct PredictionEdit(int Start, int End, string Text);
