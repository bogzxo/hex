namespace Hex.Core;

/// <summary>
/// An edit made to a file a little while ago, for the model to see where the typing is going: the file's path inside
/// its folder, the change as unified-diff hunks (from the @@ line, no file header), and whether it was a prediction the
/// user took.
/// </summary>
internal sealed record PredictionEvent(string Path, string Diff, bool Predicted);
