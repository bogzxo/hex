using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The caret of the file on screen and the text round it, for an extension that writes where somebody is typing (Emmet)
// rather than by the paths of values. Lines and columns from 1, the way hex.problems has them.
//
//   hex.caret()                                            { line, column, text, selected }, null when it isn't code
//   hex.replace_text({ line, column }, { line, column }, text, caret)   one step to undo, the caret that far into the text
//   hex.selection()                                        { from, to, caret, text }, from before to, null when it isn't code
//   hex.select({ line, column }, { line, column })         a selection with the caret at the second, or only a caret, shown
//   hex.lines(first, last)                                 the lines as they are written, all of them without the two
//   on_tab(hex, file)                                      Tab pressed with nothing selected, true when it took the key
internal sealed partial class HexExtensions
{
    private (string, IRuntimeValue)[] Caret() =>
    [
        ("caret", Natives.Function("caret", _ =>
        {
            if (!context.Documents.Current.IsText)
                return Values.Null;

            UITextDocument text = context.Documents.Current.Editor.Document;
            return Natives.Object(
                ("line", new NumberValue(text.Caret.Line + 1)),
                ("column", new NumberValue(text.Caret.Column + 1)),
                ("text", new StringValue(text.LineAt(text.Caret.Line))),
                ("selected", new StringValue(text.SelectedText)));
        })),
        ("replace_text", Natives.Function("replace_text", args =>
        {
            if (!context.Documents.Current.IsText)
                return new StringValue("the file on screen is a layout, not code");

            UITextDocument text = context.Documents.Current.Editor.Document;
            UITextPosition from = PositionOf(text, args.Object(0)), to = PositionOf(text, args.Object(1));
            if (to < from)
                (from, to) = (to, from);

            string written = args.Text(2).ReplaceLineEndings("\n");
            int caret = Math.Clamp(args.Has(3) && args[3] is NumberValue at ? (int)at.Value : written.Length, 0, written.Length);

            text.Select(from, to);
            text.Insert(written);

            // The caret where it was asked for, counted through the lines of what was written
            string before = written[..caret];
            int lastBreak = before.LastIndexOf('\n');
            text.MoveTo(lastBreak < 0
                ? new UITextPosition(from.Line, from.Column + caret)
                : new UITextPosition(from.Line + before.Count(character => character == '\n'), caret - lastBreak - 1));
            return Values.Null;
        })),
        ("selection", Natives.Function("selection", _ =>
        {
            if (!context.Documents.Current.IsText)
                return Values.Null;

            UITextDocument text = context.Documents.Current.Editor.Document;
            return Natives.Object(
                ("from", PlaceOf(text.SelectionStart)),
                ("to", PlaceOf(text.SelectionEnd)),
                ("caret", PlaceOf(text.Caret)),
                ("text", new StringValue(text.SelectedText)));
        })),
        ("select", Natives.Function("select", args =>
        {
            if (!context.Documents.Current.IsText)
                return new StringValue("the file on screen is a layout, not code");

            // The caret put there the way going to a place does, scrolled to and in the middle if it was far away
            CodeEditor editor = context.Documents.Current.Editor;
            UITextPosition from = PositionOf(editor.Document, args.Object(0));
            UITextPosition to = args.Has(1) && args[1] is ObjectValue end ? PositionOf(editor.Document, end) : from;
            editor.GoTo(to.Line, to.Column);
            if (from != to)
                editor.Document.Select(from, to);
            return Values.Null;
        })),
        ("lines", Natives.Function("lines", args =>
        {
            if (!context.Documents.Current.IsText)
                return Values.Null;

            // HIDL's split trims what it splits and drops what's empty, which is no use for lines of code
            UITextDocument text = context.Documents.Current.Editor.Document;
            int first = Math.Clamp(args.Has(0) ? args.Integer(0) - 1 : 0, 0, text.LineCount - 1);
            int last = Math.Clamp(args.Has(1) ? args.Integer(1) - 1 : text.LineCount - 1, first, text.LineCount - 1);
            var lines = new ListValue();
            for (int line = first; line <= last; line++)
                lines.Items.Add(new StringValue(text.LineAt(line)));
            return lines;
        }))
    ];

    // A place in the text as an extension has it, a { line, column } from 1
    private static ObjectValue PlaceOf(UITextPosition at) =>
        Natives.Object(("line", new NumberValue(at.Line + 1)), ("column", new NumberValue(at.Column + 1)));

    // A { line, column } from 1 as a place in the text, kept inside it
    private static UITextPosition PositionOf(UITextDocument text, ObjectValue at) =>
        text.Clamp(new UITextPosition((int)(Number(at, "line") ?? 1) - 1, (int)(Number(at, "column") ?? 1) - 1));

    /// <summary>
    /// Tab pressed in the code of the file on screen with one caret and nothing selected, handed to every extension that is
    /// on and has an <c>on_tab(hex, file)</c> until one says it took it (true), which is then all the key does.
    /// </summary>
    internal bool Tab(HexDocument of)
    {
        if (of != context.Documents.Current || !of.IsText)
            return false;

        IRuntimeValue file = of.Path is { } path ? new StringValue(Path.GetFullPath(path)) : Values.Null;
        foreach (HexExtension extension in all)
        {
            if (!extension.Enabled || extension.Runtime is null || extension.Failed is not null)
                continue;

            if (!extension.Raise("on_tab", [HexOf(extension), file], out IRuntimeValue took))
            {
                Failed(extension);
                continue;
            }

            if (took is BooleanValue { Value: true })
                return true;
        }

        return false;
    }
}
