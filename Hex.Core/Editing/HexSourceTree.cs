using Horizon.HIDL;
using Horizon.HIDL.Parsing;
using Horizon.HIDL.Runtime;

namespace Hex.Core;

/// <summary>
/// A value of a file found by its path (<c>["moves", "kick_left", "damage"]</c>), where it is written and, for an object,
/// where each of its keys is. <see cref="Written"/> says whether it is written in the file as a value that can be changed
/// in place, and not worked out or brought in from another file.
/// </summary>
internal sealed record HexNode(IRuntimeValue Value, SourceLocation At, IReadOnlyList<HexEntry> Entries, bool Written);

/// <summary>
/// The text of a file run once with a source map, for finding where its values are written. Made for every edit an
/// extension asks for, on the text as it is at that moment, a file of moves runs in a few milliseconds and the values
/// of the last run in the background are of a text that's gone the moment anything is typed.
/// </summary>
internal sealed class HexSourceTree
{
    private readonly HIDLSourceMap map;
    private readonly Dictionary<string, IRuntimeValue> declared;
    private readonly string? file;

    // Where every line of the text starts. What runs is the text after the preprocessor, which leaves a directive's line
    // empty (#define fight_move went, and every index after it was that much short), the lines and columns are the same
    // in both, so a place is found in the text by those
    private readonly List<int> lineStarts = [0];

    private HexSourceTree(HIDLSourceMap map, Dictionary<string, IRuntimeValue> declared, string? file, string text)
    {
        this.map = map;
        this.declared = declared;
        this.file = file;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                lineStarts.Add(i + 1);
        }
    }

    /// <summary>A place moved onto the text as it is written, by its line and column.</summary>
    private SourceSpan InText(SourceSpan span) =>
        span.IsKnown && span.Line - 1 < lineStarts.Count ? span with { Index = lineStarts[span.Line - 1] + span.Column - 1 } : span;

    private SourceLocation InText(SourceLocation at) => at with { Span = InText(at.Span), Value = InText(at.Value) };

    /// <summary>Runs a text, a second at the most. Throws with what went wrong if it doesn't run.</summary>
    public static HexSourceTree Read(string text, string? file, Func<string, string?, string> include)
    {
        var sources = new HIDLSourceMap();
        var runtime = new HIDLRuntime
        {
            Output = null,
            Sources = sources,
            BaseDirectory = file is null ? null : Path.GetDirectoryName(file),
            IncludeReader = include
        };

        using var cancel = new CancellationTokenSource(1000);
        runtime.Interpreter.Cancellation = cancel.Token;
        runtime.SourceFile = file;
        runtime.Run(text);
        return new HexSourceTree(sources, new Dictionary<string, IRuntimeValue>(runtime.UserScope.Variables), file, text);
    }

    /// <summary>What the file declared, by name.</summary>
    public IReadOnlyDictionary<string, IRuntimeValue> Declared => declared;

    /// <summary>The value at a path, null with why not if there's none.</summary>
    public HexNode? Find(IReadOnlyList<string> path, out string why)
    {
        why = string.Empty;
        if (path.Count == 0 || !declared.TryGetValue(path[0], out IRuntimeValue? value) || !map.TryGetDeclaration(path[0], out SourceLocation at))
        {
            why = path.Count == 0 ? "a path needs at least the name of what the file declares" : $"the file declares no '{path[0]}'";
            return null;
        }

        for (int i = 1; i < path.Count; i++)
        {
            if (value is not ObjectValue parent || !parent.Properties.TryGetValue(path[i], out IRuntimeValue? inside))
            {
                why = $"there is no '{string.Join(".", path.Take(i + 1))}'";
                return null;
            }

            if (!map.TryGetKey(parent, path[i], out at))
                at = default;

            value = inside;
        }

        return new HexNode(value, InText(at), EntriesOf(value), IsHere(at) && at.Value.IsKnown);
    }

    /// <summary>The keys of an object written in this file, in the order they're written, none for anything else.</summary>
    private List<HexEntry> EntriesOf(IRuntimeValue value)
    {
        var entries = new List<HexEntry>();
        if (value is not ObjectValue of)
            return entries;

        foreach (string key in of.Properties.Keys)
        {
            if (map.TryGetKey(of, key, out SourceLocation at) && IsHere(at) && at.Span.IsKnown && at.Value.IsKnown)
                entries.Add(new HexEntry(key, InText(at.Span), InText(at.Value)));
        }

        entries.Sort((a, b) => a.Name.Index.CompareTo(b.Name.Index));
        return entries;
    }

    /// <summary>Whether a place is in this file and not an include of it.</summary>
    private bool IsHere(SourceLocation at) =>
        at.File is null || file is null || string.Equals(Path.GetFullPath(at.File), Path.GetFullPath(file), PlatformPaths.Comparison);
}
