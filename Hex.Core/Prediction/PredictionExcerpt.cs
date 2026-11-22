namespace Hex.Core;

/// <summary>A few lines of another file sent with the prompt, round where it was last edited, and the file's path.</summary>
internal sealed record PredictionExcerpt(string Path, string Text);
