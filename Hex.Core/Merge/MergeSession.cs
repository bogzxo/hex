namespace Hex.Core.Merge;

/// <summary>The data and callbacks needed to open a merge editor, independent of its caller.</summary>
internal sealed record MergeSession(string Title, string MineLabel, string TheirsLabel, string BaseLabel,
    MergeModel Model, Func<byte[], Task> Complete, Action Cancel, string Path)
{
    public bool IsBinary => Model.IsBinary;
    public MergeSideChoices? SideChoices { get; init; }
    public DateTimeOffset? MineModified { get; init; }
    public DateTimeOffset? TheirsModified { get; init; }
}

internal sealed class MergeCheckFailed(string output, Func<Task> overrideCheck) : IOException(output)
{
    public Func<Task> OverrideCheck { get; } = overrideCheck;
}

internal enum MergeSide { Mine, Theirs }

internal sealed record MergeSideChoices(bool MineDeleted, bool TheirsDeleted, Func<MergeSide, Task> Complete);
