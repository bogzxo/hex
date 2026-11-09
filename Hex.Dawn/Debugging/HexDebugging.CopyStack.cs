namespace Hex.Dawn;

internal sealed partial class HexDebugging
{
    internal static string StackText(IReadOnlyList<DebugFrame> frames) => string.Join(Environment.NewLine,
        frames.Select(frame => frame.File is { Length: > 0 } file
            ? $"{frame.Name} in {file}:line {frame.Line + 1}, column {frame.Column + 1}"
            : frame.Name));

    internal string CurrentStackText() => DebugStopped ? StackText(stoppedFrames) : string.Empty;

    private void CopyCallStack()
    {
        if (CurrentStackText() is { Length: > 0 } text) UIClipboard.Copy(text);
    }
}
