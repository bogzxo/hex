namespace Hex.Core.Merge;

/// <summary>The data and callbacks needed to open a merge editor, independent of its caller.</summary>
internal sealed record MergeSession(string Title, string MineLabel, string TheirsLabel, string BaseLabel,
    MergeModel Model, Func<byte[], Task> Complete, Action Cancel, string Path)
{
    public bool IsBinary => Model.IsBinary;
}
