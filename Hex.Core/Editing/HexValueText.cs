using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Hex.Core;

/// <summary>
/// A value written as code for putting into a file, the way the files of a game are written by hand. A small object of
/// plain values on one line (a phase, <c>{ animation: "kick", frames: 6, hit: 2 }</c>), anything bigger over lines.
/// What HIDLWriter writes otherwise, which puts every object over lines.
/// </summary>
internal static class HexValueText
{
    // How long an object can be on one line, and how many keys, before it goes over lines
    private const int ONE_LINE = 88, ONE_LINE_KEYS = 6;

    public static string Write(IRuntimeValue value)
    {
        if (value is ObjectValue of && OneLine(of) is { } line)
            return line;

        if (value is ObjectValue big)
            return WriteOverLines(big);

        return HIDLWriter.Write(value);
    }

    /// <summary>A key as it is written, as itself when it can be.</summary>
    public static string Key(string key) => HIDLWriter.IsIdentifier(key) ? key : HIDLWriter.Write(new StringValue(key));

    private static string? OneLine(ObjectValue of)
    {
        if (of.Properties.Count == 0)
            return "{}";
        if (of.Properties.Count > ONE_LINE_KEYS)
            return null;

        var parts = new List<string>(of.Properties.Count);
        foreach (var (key, value) in of.Properties)
        {
            if (value is ObjectValue or ListValue { Count: > 3 } || !HIDLWriter.CanWrite(value))
                return null;
            parts.Add(Key(key) + ": " + HIDLWriter.Write(value));
        }

        string line = "{ " + string.Join(", ", parts) + " }";
        return line.Length <= ONE_LINE ? line : null;
    }

    // Over lines, every value written the same way, so a move's phases come out one a line
    private static string WriteOverLines(ObjectValue of)
    {
        var lines = new List<string>();
        foreach (var (key, value) in of.Properties)
        {
            if (HIDLWriter.CanWrite(value))
                lines.Add("    " + Key(key) + ": " + Write(value).Replace("\n", "\n    "));
        }

        return "{\n" + string.Join(",\n", lines) + "\n}";
    }
}
