using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The caret of the file on screen and the text round it, for an extension that writes where somebody is typing (Emmet)
// rather than by the paths of values. Lines and columns from 1, the way hex.problems has them.
//
//   hex.caret()                                            { line, column, text, selected }, null when it isn't code
//   hex.replace_text({ line, column }, { line, column }, text, caret)   one step to undo, the caret that far into the text
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
        }))
    ];

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
