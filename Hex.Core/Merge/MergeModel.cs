using Hex.Core.Git;

namespace Hex.Core.Merge;

internal enum MergeChoice { Unresolved, Mine, Theirs, BothMineFirst, BothTheirsFirst, Custom }

internal readonly record struct MergeResolution(MergeChoice Choice, IReadOnlyList<string>? Lines = null);

internal abstract record MergeRegion
{
    public sealed record Clean(IReadOnlyList<string> Lines) : MergeRegion;

    public sealed record Conflict(IReadOnlyList<string>? BaseLines, IReadOnlyList<string> MineLines,
        IReadOnlyList<string> TheirsLines, IReadOnlyList<string> MarkedLines) : MergeRegion
    {
        public MergeResolution Resolution { get; set; }

        internal IReadOnlyList<string> OutputLines() => Resolution.Choice switch
        {
            MergeChoice.Unresolved => MarkedLines,
            MergeChoice.Mine => MineLines,
            MergeChoice.Theirs => TheirsLines,
            MergeChoice.BothMineFirst => [.. MineLines, .. TheirsLines],
            MergeChoice.BothTheirsFirst => [.. TheirsLines, .. MineLines],
            MergeChoice.Custom => Resolution.Lines ?? [],
            _ => throw new ArgumentOutOfRangeException(nameof(Resolution)),
        };
    }
}

internal sealed record MergeBinarySides(byte[] Base, byte[] Mine, byte[] Theirs);

/// <summary>The text and choices shared by any source of a three-way merge or conflict markers.</summary>
internal sealed class MergeModel
{
    private readonly string encoding;

    private MergeModel(IReadOnlyList<MergeRegion> regions, string lineEnding, bool finalNewline, string encoding,
        MergeBinarySides? binarySides = null)
    {
        Regions = regions;
        LineEnding = lineEnding;
        HasFinalNewline = finalNewline;
        this.encoding = encoding;
        BinarySides = binarySides;
        Conflicts = regions.OfType<MergeRegion.Conflict>().ToArray();
    }

    public IReadOnlyList<MergeRegion> Regions { get; }

    public IReadOnlyList<MergeRegion.Conflict> Conflicts { get; }

    public int ConflictCount => Conflicts.Count;

    public int UnresolvedCount => Conflicts.Count(conflict => conflict.Resolution.Choice == MergeChoice.Unresolved);

    public string LineEnding { get; }

    public bool HasFinalNewline { get; }

    public MergeBinarySides? BinarySides { get; }

    public bool IsBinary => BinarySides is not null;

    public static async Task<MergeModel> FromThree(byte[] @base, byte[] mine, byte[] theirs, CancellationToken cancel = default)
    {
        if (await HexLinkMerge.TryTextAsync(@base, mine, theirs, cancel) is not { } merged
            || HexLinkFiles.Decode(merged.Bytes) is not { } draft
            || HexLinkFiles.Decode(mine) is not { } original)
            return Binary(@base, mine, theirs);

        IReadOnlyList<MergeRegion> regions = merged.Clean ? [new MergeRegion.Clean(Lines(draft.Text))] : RegionsFromMarkers(draft.Text);
        // If Git reports a conflict, keep a side choice available even if the draft's markers cannot be read.
        if (!merged.Clean && !regions.Any(region => region is MergeRegion.Conflict))
            return Binary(@base, mine, theirs);
        return new(regions, Ending(original.Text), EndsWithNewline(original.Text), original.Encoding);
    }

    public static MergeModel FromMarkers(string text, string encoding = "utf-8") =>
        new(RegionsFromMarkers(text), Ending(text), EndsWithNewline(text), encoding);

    public static MergeModel Binary(byte[] @base, byte[] mine, byte[] theirs) =>
        new([], "\n", false, "utf-8", new(@base, mine, theirs));

    /// <summary>Render the result, keeping the original markers for each still-unresolved conflict.</summary>
    public string ToText()
    {
        if (IsBinary)
            throw new InvalidOperationException("Choose a side for a binary merge.");

        var lines = new List<string>();
        foreach (MergeRegion region in Regions)
            lines.AddRange(region is MergeRegion.Clean clean ? clean.Lines : ((MergeRegion.Conflict)region).OutputLines());

        return lines.Count == 0 ? "" : string.Join(LineEnding, lines) + (HasFinalNewline ? LineEnding : "");
    }

    public byte[] ToBytes() => HexLinkFiles.Encode(ToText(), encoding)
        ?? throw new InvalidOperationException($"The merge encoding '{encoding}' is unsupported.");

    internal static string[] Lines(string text) => text.Length == 0 ? [] : GitLineDiff.Lines(text.ReplaceLineEndings("\n"));

    private static IReadOnlyList<MergeRegion> RegionsFromMarkers(string text)
    {
        string[] lines = Lines(text);
        var regions = new List<MergeRegion>();
        int start = 0;
        foreach (GitConflict conflict in GitConflicts.Find(lines))
        {
            if (start < conflict.Start)
                regions.Add(new MergeRegion.Clean(lines[start..conflict.Start]));
            start = conflict.End + 1;
            regions.Add(new MergeRegion.Conflict(conflict.Base < 0 ? null : lines[(conflict.Base + 1)..conflict.Middle],
                lines[(conflict.Start + 1)..conflict.CurrentEnd], lines[(conflict.Middle + 1)..conflict.End],
                lines[conflict.Start..start]));
        }

        if (start < lines.Length || regions.Count == 0)
            regions.Add(new MergeRegion.Clean(lines[start..]));
        return regions;
    }

    private static string Ending(string text) => HexLinkFiles.Eol(text) switch
    {
        "crlf" => "\r\n",
        "cr" => "\r",
        _ => "\n",
    };

    private static bool EndsWithNewline(string text) => text.EndsWith('\n') || text.EndsWith('\r');
}
