using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

/// <summary>One edit of a batch, a function of the text as it is and what the file came to, the new text or why not.</summary>
internal delegate string? HexEdit(string text, HexSourceTree tree, out string why);

/// <summary>
/// Edits to a file of HIDL by the paths of its values (["moves", "jab", "damage"]), each found in what the file comes to
/// and written where it is in the text and nowhere else (HexSourceEdits), the rest of the text as it was. What the
/// extensions' hex.edit and the rest are made of, with nothing of the editor in it.
/// </summary>
internal static class HexValueEdits
{
    /// <summary>
    /// The edits made one after the other, each on what the one before left. The text at the end, or null and why the
    /// first that couldn't be made couldn't.
    /// </summary>
    /// <param name="path">Where the file is, for its includes, null for one that isn't anywhere.</param>
    /// <param name="include">The text of a file it includes, by its name and the folder of the file including it.</param>
    public static string? Apply(string text, string? path, Func<string, string?, string> include, IReadOnlyList<HexEdit> edits, out string why)
    {
        why = string.Empty;
        foreach (HexEdit edit in edits)
        {
            HexSourceTree tree;
            try
            {
                tree = HexSourceTree.Read(text, path, include);
            }
            catch (Exception e)
            {
                why = $"the file doesn't run as it is, {e.Message}";
                return null;
            }

            if (edit(text, tree, out why) is not { } changed)
                return null;
            text = changed;
        }

        return text;
    }

    /// <summary>The value at a path set.</summary>
    public static HexEdit EditOf(string[] path, IRuntimeValue value) => (string text, HexSourceTree tree, out string why) =>
        tree.Find(path, out why) is { } node && Writable(node, path, ref why)
            ? HexSourceEdits.SetValue(text, node.At.Value, HexValueText.Write(value).Replace("\n", "\n" + IndentAt(text, node.At.Value.Index)))
            : null;

    /// <summary>A key put into the object at a path, at its end or after another key.</summary>
    public static HexEdit InsertOf(string[] path, string key, IRuntimeValue value, string? after) => (string text, HexSourceTree tree, out string why) =>
    {
        if (tree.Find(path, out why) is not { Value: ObjectValue } node || !Writable(node, path, ref why))
            return why.Length > 0 ? null : Fail($"{string.Join(".", path)} isn't an object a key can go into", out why);
        if (node.Entries.Any(entry => entry.Key == key))
            return Fail($"{string.Join(".", path)} has a {key} already", out why);

        return HexSourceEdits.Insert(text, node.At.Value, node.Entries, HexValueText.Key(key), HexValueText.Write(value), after);
    };

    /// <summary>A key taken out, with its comma and the comments over it.</summary>
    public static HexEdit RemoveOf(string[] path) => (string text, HexSourceTree tree, out string why) =>
    {
        if (path.Length < 2)
            return Fail("only a key of an object can be taken out", out why);
        if (tree.Find(path[..^1], out why) is not { } parent || !Writable(parent, path[..^1], ref why))
            return null;
        if (!parent.Entries.Any(entry => entry.Key == path[^1]))
            return Fail($"{string.Join(".", path)} isn't written in the file", out why);

        return HexSourceEdits.Remove(text, parent.At.Value, parent.Entries, path[^1]);
    };

    /// <summary>A key's name changed.</summary>
    public static HexEdit RenameOf(string[] path, string key) => (string text, HexSourceTree tree, out string why) =>
    {
        if (tree.Find(path, out why) is not { } node || !node.At.Span.IsKnown || path.Length < 2)
            return why.Length > 0 ? null : Fail($"{string.Join(".", path)} isn't a key written in the file", out why);
        if (tree.Find([.. path[..^1], key], out _) is not null)
            return Fail($"there is a {key} there already", out why);

        return HexSourceEdits.Rename(text, node.At.Span, HexValueText.Key(key));
    };

    /// <summary>An object's keys put in another order, every one of them once.</summary>
    public static HexEdit ReorderOf(string[] path, string[] order) => (string text, HexSourceTree tree, out string why) =>
    {
        if (tree.Find(path, out why) is not { Value: ObjectValue } node || !Writable(node, path, ref why))
            return why.Length > 0 ? null : Fail($"{string.Join(".", path)} isn't an object", out why);
        if (order.Length != node.Entries.Count || order.Except(node.Entries.Select(entry => entry.Key)).Any())
            return Fail("the new order has to have every key of the object once", out why);

        return HexSourceEdits.Reorder(text, node.Entries, order);
    };

    /// <summary>A batch read, a list of objects that each say what they are by their first key, { edit: path, value } and the like.</summary>
    public static IEnumerable<HexEdit> BatchOf(IRuntimeValue batch)
    {
        if (batch is not ListValue list)
            throw new HidlRuntimeException("hex.edits takes a list of edits.");

        foreach (IRuntimeValue item in list.Items)
        {
            if (item is not ObjectValue edit)
                throw new HidlRuntimeException("an edit of hex.edits is an object, { edit: path, value } and the like.");

            var with = edit.Properties;
            yield return with switch
            {
                _ when with.TryGetValue("edit", out var at) => EditOf(PathOf(at), with.GetValueOrDefault("value") ?? Values.Null),
                _ when with.TryGetValue("insert", out var at) => InsertOf(PathOf(at), Text(edit, "key") ?? "", with.GetValueOrDefault("value") ?? Values.Null, Text(edit, "after")),
                _ when with.TryGetValue("remove", out var at) => RemoveOf(PathOf(at)),
                _ when with.TryGetValue("rename", out var at) => RenameOf(PathOf(at), Text(edit, "key") ?? ""),
                _ when with.TryGetValue("reorder", out var at) => ReorderOf(PathOf(at), Texts(with.GetValueOrDefault("keys") ?? Values.Null)),
                _ => throw new HidlRuntimeException("an edit says edit, insert, remove, rename or reorder.")
            };
        }
    }

    /// <summary>A path as HIDL hands it over, a list of keys or one text with dots.</summary>
    public static string[] PathOf(IRuntimeValue path) => path switch
    {
        ListValue list => [.. list.Items.Select(item => item is StringValue text ? text.Value : item.ToString() ?? string.Empty)],
        StringValue text => text.Value.Split('.'),
        _ => throw new HidlRuntimeException("a path is a list of keys, [\"moves\", \"jab\"], or one text with dots.")
    };

    /// <summary>A list of texts as HIDL hands it over, none for anything else.</summary>
    public static string[] Texts(IRuntimeValue value) =>
        value is ListValue list ? [.. list.Items.Select(item => item is StringValue text ? text.Value : item.ToString() ?? string.Empty)] : [];

    /// <summary>Whether a value is written in the file as itself, saying why not when it isn't.</summary>
    private static bool Writable(HexNode node, string[] path, ref string why)
    {
        if (node.Written)
            return true;

        why = $"{string.Join(".", path)} is worked out or written in another file, the code says what it is";
        return false;
    }

    private static string? Fail(string reason, out string why)
    {
        why = reason;
        return null;
    }

    private static string? Text(ObjectValue of, string key) =>
        of.Properties.TryGetValue(key, out IRuntimeValue? value) && value is StringValue text ? text.Value : null;

    private static string IndentAt(string text, int index)
    {
        int line = index <= 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;
        int end = line;
        while (end < text.Length && text[end] is ' ' or '\t')
            end++;
        return text[line..end];
    }
}
