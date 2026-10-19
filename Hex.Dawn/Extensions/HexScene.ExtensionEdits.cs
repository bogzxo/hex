using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

// Edits an extension makes to a file by the paths of its values, written where they are in the text and nowhere else
// (HexSourceEdits), one step to undo a call. The file is null for the one on screen, or a path, open in Hex or opened for
// it in a tab that isn't brought to the front. Each hands back null when it went, or why it didn't as a text, an extension
// has no try and catch to find out with.
//
//   hex.edit(file, ["moves", "jab", "damage"], 8)          the value at a path
//   hex.insert(file, ["moves", "jab"], "level", "low")     a key into an object, at the end, or after another key
//   hex.remove(file, ["moves", "jab", "level"])            a key out, with its comma and the comments over it
//   hex.rename(file, ["moves", "jab"], "light_punch")      a key's name
//   hex.reorder(file, ["moves", "jab", "phases"], [...])   an object's keys in another order
//   hex.edits(file, [{ edit: path, value }, { insert: path, key, value, after }, { remove: path }, ...])  several, one step
//   hex.undo_file(file) and hex.redo_file(file)            the file's own undo
internal sealed partial class HexScene
{
    /// <summary>One edit of a batch, a function of the text as it is and what the file came to, the new text or why not.</summary>
    private delegate string? HexEdit(string text, HexSourceTree tree, out string why);

    private (string, IRuntimeValue)[] Edits(HexExtension extension) =>
    [
        ("edit", Natives.Function("edit", args => Apply(extension, args[0], [EditOf(PathOf(args[1]), args[2])]))),
        ("insert", Natives.Function("insert", args => Apply(extension, args[0], [InsertOf(PathOf(args[1]), args.Text(2), args[3], args.Has(4) && args[4] is StringValue after ? after.Value : null)]))),
        ("remove", Natives.Function("remove", args => Apply(extension, args[0], [RemoveOf(PathOf(args[1]))]))),
        ("rename", Natives.Function("rename", args => Apply(extension, args[0], [RenameOf(PathOf(args[1]), args.Text(2))]))),
        ("reorder", Natives.Function("reorder", args => Apply(extension, args[0], [ReorderOf(PathOf(args[1]), Texts(args[2]))]))),
        ("edits", Natives.Function("edits", args => Apply(extension, args[0], [.. BatchOf(args[1])]))),
        ("undo_file", Natives.Action("undo_file", args => FileDocument(extension, args.Has(0) ? args[0] : Values.Null, out _)?.Editor.Document.Undo())),
        ("redo_file", Natives.Action("redo_file", args => FileDocument(extension, args.Has(0) ? args[0] : Values.Null, out _)?.Editor.Document.Redo()))
    ];

    /// <summary>
    /// Helper method to make edits to a file one after the other, each on what the one before left, and put the end of it
    /// into the file as one step. Null when it went, why it didn't otherwise, and the file is left as it was.
    /// </summary>
    private IRuntimeValue Apply(HexExtension extension, IRuntimeValue file, IReadOnlyList<HexEdit> edits)
    {
        if (FileDocument(extension, file, out string why) is not { } target)
            return new StringValue(why);

        string text = target.Editor.Text;
        foreach (HexEdit edit in edits)
        {
            HexSourceTree tree;
            try
            {
                tree = HexSourceTree.Read(text, target.Path is { } path ? Path.GetFullPath(path) : null, ReadInclude);
            }
            catch (Exception e)
            {
                return new StringValue($"the file doesn't run as it is, {e.Message}");
            }

            if (edit(text, tree, out why) is not { } changed)
                return new StringValue(why);
            text = changed;
        }

        if (text != target.Editor.Text)
            Rewrite(target, text);
        return Values.Null;
    }

    /// <summary>A file's text replaced as one step to undo, the tab saying it has changes when it isn't the one on screen.</summary>
    private void Rewrite(HexDocument target, string text)
    {
        target.Editor.Document.Replace(text);
        if (target != document)
        {
            target.Modified = target.Editor.Text != target.SavedCode;
            tabsDirty = true;
        }
    }

    /* The edits, each finds what it changes and checks it is written in the file as itself */

    private static HexEdit EditOf(string[] path, IRuntimeValue value) => (string text, HexSourceTree tree, out string why) =>
        tree.Find(path, out why) is { } node && Writable(node, path, ref why)
            ? HexSourceEdits.SetValue(text, node.At.Value, HexValueText.Write(value).Replace("\n", "\n" + IndentAt(text, node.At.Value.Index)))
            : null;

    private static HexEdit InsertOf(string[] path, string key, IRuntimeValue value, string? after) => (string text, HexSourceTree tree, out string why) =>
    {
        if (tree.Find(path, out why) is not { Value: ObjectValue } node || !Writable(node, path, ref why))
            return why.Length > 0 ? null : Fail($"{string.Join(".", path)} isn't an object a key can go into", out why);
        if (node.Entries.Any(entry => entry.Key == key))
            return Fail($"{string.Join(".", path)} has a {key} already", out why);

        return HexSourceEdits.Insert(text, node.At.Value, node.Entries, HexValueText.Key(key), HexValueText.Write(value), after);
    };

    private static HexEdit RemoveOf(string[] path) => (string text, HexSourceTree tree, out string why) =>
    {
        if (path.Length < 2)
            return Fail("only a key of an object can be taken out", out why);
        if (tree.Find(path[..^1], out why) is not { } parent || !Writable(parent, path[..^1], ref why))
            return null;
        if (!parent.Entries.Any(entry => entry.Key == path[^1]))
            return Fail($"{string.Join(".", path)} isn't written in the file", out why);

        return HexSourceEdits.Remove(text, parent.At.Value, parent.Entries, path[^1]);
    };

    private static HexEdit RenameOf(string[] path, string key) => (string text, HexSourceTree tree, out string why) =>
    {
        if (tree.Find(path, out why) is not { } node || !node.At.Span.IsKnown || path.Length < 2)
            return why.Length > 0 ? null : Fail($"{string.Join(".", path)} isn't a key written in the file", out why);
        if (tree.Find([.. path[..^1], key], out _) is not null)
            return Fail($"there is a {key} there already", out why);

        return HexSourceEdits.Rename(text, node.At.Span, HexValueText.Key(key));
    };

    private static HexEdit ReorderOf(string[] path, string[] order) => (string text, HexSourceTree tree, out string why) =>
    {
        if (tree.Find(path, out why) is not { Value: ObjectValue } node || !Writable(node, path, ref why))
            return why.Length > 0 ? null : Fail($"{string.Join(".", path)} isn't an object", out why);
        if (order.Length != node.Entries.Count || order.Except(node.Entries.Select(entry => entry.Key)).Any())
            return Fail("the new order has to have every key of the object once", out why);

        return HexSourceEdits.Reorder(text, node.Entries, order);
    };

    /// <summary>Helper method to read a batch, a list of objects that each say what they are by their first key.</summary>
    private static IEnumerable<HexEdit> BatchOf(IRuntimeValue batch)
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

    /* Helpers */

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

    private static string[] PathOf(IRuntimeValue path) => path switch
    {
        ListValue list => [.. list.Items.Select(item => item is StringValue text ? text.Value : item.ToString() ?? string.Empty)],
        StringValue text => text.Value.Split('.'),
        _ => throw new HidlRuntimeException("a path is a list of keys, [\"moves\", \"jab\"], or one text with dots.")
    };

    private static string[] Texts(IRuntimeValue value) =>
        value is ListValue list ? [.. list.Items.Select(item => item is StringValue text ? text.Value : item.ToString() ?? string.Empty)] : [];

    private static string IndentAt(string text, int index)
    {
        int line = index <= 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;
        int end = line;
        while (end < text.Length && text[end] is ' ' or '\t')
            end++;
        return text[line..end];
    }

    /// <summary>An include of a file being edited, the open files as they are in Hex.</summary>
    private string ReadInclude(string file, string? folder) =>
        ReadForExtension(Path.GetFullPath(Path.IsPathRooted(file) || folder is null ? file : Path.Combine(folder, file)));

    /// <summary>
    /// The document of a file an extension names, null for the one on screen. One that isn't open is opened in a tab that
    /// stays behind the one on screen, so what is done to it can be saved and undone. Null with why if it can't be.
    /// </summary>
    private HexDocument? FileDocument(HexExtension extension, IRuntimeValue file, out string why)
    {
        why = string.Empty;
        if (file is not StringValue { Value: { Length: > 0 } named })
        {
            if (document.IsText)
                return document;

            why = "the file on screen is a layout, not code";
            return null;
        }

        string full = PathFor(extension, named);
        if (OpenDocument(full) is { } open)
            return open.IsText ? open : null;

        if (!File.Exists(full))
        {
            why = $"there is no {named}";
            return null;
        }

        // As it is on the disk, line endings and all, the editor keeps them and its save writes them back the same
        OpenText(full, File.ReadAllText(full), show: false);
        return OpenDocument(full);
    }
}
