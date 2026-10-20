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
internal sealed partial class HexExtensions
{
    private (string, IRuntimeValue)[] Edits(HexExtension extension) =>
    [
        ("edit", Natives.Function("edit", args => Apply(extension, args[0], [HexValueEdits.EditOf(HexValueEdits.PathOf(args[1]), args[2])]))),
        ("insert", Natives.Function("insert", args => Apply(extension, args[0], [HexValueEdits.InsertOf(HexValueEdits.PathOf(args[1]), args.Text(2), args[3], args.Has(4) && args[4] is StringValue after ? after.Value : null)]))),
        ("remove", Natives.Function("remove", args => Apply(extension, args[0], [HexValueEdits.RemoveOf(HexValueEdits.PathOf(args[1]))]))),
        ("rename", Natives.Function("rename", args => Apply(extension, args[0], [HexValueEdits.RenameOf(HexValueEdits.PathOf(args[1]), args.Text(2))]))),
        ("reorder", Natives.Function("reorder", args => Apply(extension, args[0], [HexValueEdits.ReorderOf(HexValueEdits.PathOf(args[1]), HexValueEdits.Texts(args[2]))]))),
        ("edits", Natives.Function("edits", args => Apply(extension, args[0], [.. HexValueEdits.BatchOf(args[1])]))),
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

        if (HexValueEdits.Apply(target.Editor.Text, target.Path is { } path ? Path.GetFullPath(path) : null, ReadInclude, edits, out why) is not { } text)
            return new StringValue(why);

        if (text != target.Editor.Text)
            context.Documents.Rewrite(target, text);
        return Values.Null;
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
            if (context.Documents.Current.IsText)
                return context.Documents.Current;

            why = "the file on screen is a layout, not code";
            return null;
        }

        string full = PathFor(extension, named);
        if (context.Documents.Find(full) is { } open)
            return open.IsText ? open : null;

        if (!File.Exists(full))
        {
            why = $"there is no {named}";
            return null;
        }

        return context.Documents.OpenBehind(full);
    }
}
