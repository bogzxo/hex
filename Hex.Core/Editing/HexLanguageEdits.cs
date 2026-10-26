using Horizon.Content;
using Horizon.HIDL.Runtime;

namespace Hex.Core;

/// <summary>
/// A text of a game's language file found and written by its key ("main_menu.quit"), in place, the rest of the file
/// left exactly as it was, its comments and its spacing (HexValueEdits underneath). A key the file hasn't got goes into
/// the deepest object of its path that it has, with whatever objects it wants on the way. What double clicking a text
/// on the designer's canvas writes, and what F12 on a key goes to.
/// </summary>
internal static class HexLanguageEdits
{
    /// <summary>Where a key is in the file, the strings object first and then its parts.</summary>
    public static string[] PathOf(string key) => [Localisation.STRINGS, .. key.Split('.')];

    /// <summary>
    /// The file with a key's text changed, or put in if it isn't there. A text written as a list of lines stays a list
    /// of lines. Null and why if it can't be (the file doesn't run, the key is in an include, a part of it is a text).
    /// </summary>
    /// <param name="path">Where the file is, for its includes, null for one that isn't anywhere.</param>
    public static string? SetText(string source, string? path, string key, string text, out string why)
    {
        string[] at = PathOf(key);
        if (key.Length == 0 || at.Skip(1).Any(part => part.Length == 0))
            return Fail($"{key} isn't a key", out why);

        if (!Read(source, path, out HexSourceTree? tree, out why))
            return null;

        if (tree!.Find(at, out _) is { } node)
        {
            // A long text a line to an item of the list, the way the pages of help are written
            IRuntimeValue value = node.Value is ListValue
                ? new ListValue([.. text.Split('\n').Select(line => (IRuntimeValue)new StringValue(line))])
                : new StringValue(text);
            return HexValueEdits.Apply(source, path, ReadInclude, [HexValueEdits.EditOf(at, value)], out why);
        }

        // The deepest object of the path the file has, the rest goes into it as objects round the text
        int have = at.Length - 1;
        while (have > 0 && tree.Find(at[..have], out _) is not { Value: ObjectValue })
            have--;

        if (have == 0)
            return Fail($"the file declares no {Localisation.STRINGS}", out why);

        IRuntimeValue made = new StringValue(text);
        for (int i = at.Length - 1; i > have; i--)
            made = new ObjectValue(new Dictionary<string, IRuntimeValue> { [at[i]] = made });

        return HexValueEdits.Apply(source, path, ReadInclude, [HexValueEdits.InsertOf(at[..have], at[have], made, after: null)], out why);
    }

    /// <summary>The file with a key taken out, its comma and the comments over it with it. Null and why if it can't be.</summary>
    public static string? RemoveText(string source, string? path, string key, out string why) =>
        HexValueEdits.Apply(source, path, ReadInclude, [HexValueEdits.RemoveOf(PathOf(key))], out why);

    /// <summary>Where a key's name is written in the file, its line and column from 0, null when the file hasn't got it written.</summary>
    public static (int Line, int Column)? Find(string source, string? path, string key)
    {
        if (!Read(source, path, out HexSourceTree? tree, out _) || tree!.Find(PathOf(key), out _) is not { } node || !node.At.Span.IsKnown)
            return null;

        return (node.At.Span.Line - 1, node.At.Span.Column - 1);
    }

    private static bool Read(string source, string? path, out HexSourceTree? tree, out string why)
    {
        why = string.Empty;
        try
        {
            tree = HexSourceTree.Read(source, path, ReadInclude);
            return true;
        }
        catch (Exception e)
        {
            tree = null;
            why = $"the file doesn't run as it is, {e.Message}";
            return false;
        }
    }

    // An include of a language file, read off the disk
    private static string ReadInclude(string file, string? folder) =>
        File.ReadAllText(Path.IsPathRooted(file) || folder is null ? file : Path.Combine(folder, file));

    private static string? Fail(string reason, out string why)
    {
        why = reason;
        return null;
    }
}
