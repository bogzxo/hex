using Hex.Core.Git;

namespace Hex.Core.Merge;

internal enum MergeChoice { Unresolved, Mine, Theirs, BothMineFirst, BothTheirsFirst, Custom }

internal readonly record struct MergeResolution(MergeChoice Choice, IReadOnlyList<string>? Lines = null);

internal abstract record MergeRegion
{
    public sealed record Clean(IReadOnlyList<string> Lines, IReadOnlyList<string>? BaseLines = null) : MergeRegion;

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

internal sealed record MergeOriginals(string[]? Base, string[] Mine, string[] Theirs);

/// <summary>The text and choices shared by any source of a three-way merge or conflict markers.</summary>
internal sealed class MergeModel
{
    private readonly string encoding;

    private MergeModel(IReadOnlyList<MergeRegion> regions, string lineEnding, bool finalNewline, string encoding,
        MergeBinarySides? binarySides = null, MergeOriginals? originals = null)
    {
        Regions = regions;
        LineEnding = lineEnding;
        HasFinalNewline = finalNewline;
        this.encoding = encoding;
        BinarySides = binarySides;
        Originals = originals;
        Conflicts = regions.OfType<MergeRegion.Conflict>().ToArray();
    }

    public IReadOnlyList<MergeRegion> Regions { get; }

    public IReadOnlyList<MergeRegion.Conflict> Conflicts { get; }

    public int ConflictCount => Conflicts.Count;

    public int UnresolvedCount => Conflicts.Count(conflict => conflict.Resolution.Choice == MergeChoice.Unresolved);

    public string LineEnding { get; }

    public bool HasFinalNewline { get; }

    public MergeBinarySides? BinarySides { get; }

    public MergeOriginals? Originals { get; }

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
        string[]? baseLines = HexLinkFiles.Decode(@base) is { } baseline ? Lines(baseline.Text) : null;
        if (baseLines is not null)
            regions = ShowCleanChanges(regions, baseLines);
        MergeOriginals? sources = HexLinkFiles.Decode(theirs) is { } incoming
            ? new(baseLines, Lines(original.Text), Lines(incoming.Text)) : null;
        return new(regions, Ending(original.Text), EndsWithNewline(original.Text), original.Encoding, originals: sources);
    }

    public static MergeModel FromTwo(byte[] mine, byte[] theirs)
    {
        if (HexLinkFiles.Decode(mine) is not { } local || HexLinkFiles.Decode(theirs) is not { } remote)
            return Binary([], mine, theirs);
        string[] ours = Lines(local.Text), other = Lines(remote.Text);
        return new([new MergeRegion.Conflict(null, ours, other,
            ["<<<<<<< Mine", .. ours, "=======", .. other, ">>>>>>> Theirs"])],
            Ending(local.Text), EndsWithNewline(local.Text), local.Encoding, originals: new(null, ours, other));
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

    public byte[] ToBytes() => EncodeResult(ToText());

    public byte[] EncodeResult(string edited)
    {
        string text = edited.ReplaceLineEndings(LineEnding);
        if (text.Length > 0 && HasFinalNewline != EndsWithNewline(text))
            text = HasFinalNewline ? text + LineEnding : text[..^LineEnding.Length];
        return HexLinkFiles.Encode(text, encoding)
            ?? throw new InvalidOperationException($"The merge encoding '{encoding}' is unsupported.");
    }

    // Replacing conflicts with their base aligns already-applied changes against the original file.
    private static IReadOnlyList<MergeRegion> ShowCleanChanges(IReadOnlyList<MergeRegion> regions, string[] basis)
    {
        string[] cleanProjection = [.. regions.SelectMany(region => region is MergeRegion.Clean clean
            ? clean.Lines : ((MergeRegion.Conflict)region).BaseLines ?? [])];
        List<GitHunk> hunks = GitLineDiff.Diff(basis, cleanProjection);
        if (hunks.Count == 0)
            return regions;

        var result = new List<MergeRegion>();
        int offset = 0, hunkAt = 0;
        foreach (MergeRegion region in regions)
        {
            if (region is MergeRegion.Conflict conflict)
            {
                result.Add(conflict);
                offset += conflict.BaseLines?.Count ?? 0;
                continue;
            }
            string[] lines = [.. ((MergeRegion.Clean)region).Lines];
            int end = offset + lines.Length, at = offset;
            while (hunkAt < hunks.Count && hunks[hunkAt].NewStart < offset)
                hunkAt++;
            while (hunkAt < hunks.Count && hunks[hunkAt].NewStart <= end)
            {
                GitHunk hunk = hunks[hunkAt];
                int after = hunk.NewStart + hunk.NewCount;
                if (after > end)
                    break;
                if (at < hunk.NewStart)
                    result.Add(new MergeRegion.Clean(lines[(at - offset)..(hunk.NewStart - offset)]));
                result.Add(new MergeRegion.Clean(lines[(hunk.NewStart - offset)..(after - offset)],
                    basis[hunk.OldStart..(hunk.OldStart + hunk.OldCount)]));
                at = after;
                hunkAt++;
            }
            if (at < end || lines.Length == 0 && result.Count == 0)
                result.Add(new MergeRegion.Clean(lines[(at - offset)..]));
            offset = end;
        }
        return result;
    }

    internal static void AlignStarts(IReadOnlyList<string> before, IReadOnlyList<string> after, Span<int> starts)
    {
        List<GitHunk> hunks = GitLineDiff.Diff(before, after);
        int at = 0, delta = 0;
        for (int i = 0; i < starts.Length; i++)
        {
            int line = starts[i];
            while (at < hunks.Count && hunks[at].OldStart + hunks[at].OldCount <= line)
            {
                delta += hunks[at].NewCount - hunks[at].OldCount;
                at++;
            }

            starts[i] = at < hunks.Count && line >= hunks[at].OldStart ? hunks[at].NewStart : line + delta;
        }
    }

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
