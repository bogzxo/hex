namespace Hex.Core.Merge;

/// <summary>The data and callbacks needed to open a merge editor, independent of its caller.</summary>
internal sealed record MergeSession(string Title, string MineLabel, string TheirsLabel, string BaseLabel,
    MergeModel Model, Func<byte[], Task> Complete, Action Cancel, string Path)
{
    public bool IsBinary => Model.IsBinary;
    /// <summary>The name of the other device when this is a Link conflict, whose labels and buttons say "your copy" and "its copy"; null for a git merge.</summary>
    public string? LinkDevice { get; init; }
    public MergeSideChoices? SideChoices { get; init; }
    public DateTimeOffset? MineModified { get; init; }
    public DateTimeOffset? TheirsModified { get; init; }
}

internal sealed class MergeCheckFailed(string output, Func<Task> overrideCheck) : IOException(output)
{
    public Func<Task> OverrideCheck { get; } = overrideCheck;
}

internal enum MergeSide { Mine, Theirs }

/// <summary>
/// Choosing a side of a file one side deleted. The buttons and the lines naming each side are said by the caller when it
/// knows better than "Delete file" and "Keep file" (a link says "Deleted here", "Changed on Beta", "Keep deleted").
/// </summary>
internal sealed record MergeSideChoices(bool MineDeleted, bool TheirsDeleted, Func<MergeSide, Task> Complete)
{
    public string? MineButton { get; init; }
    public string? TheirsButton { get; init; }
    public string? MineDescription { get; init; }
    public string? TheirsDescription { get; init; }
}
