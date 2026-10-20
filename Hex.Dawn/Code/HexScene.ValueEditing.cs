using System.Numerics;

using Horizon.HIDL.Parsing;
using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Editing a value where it is shown, in the values on the right. Only a value written as itself (a number, a text, true,
// a vec of numbers) is, and it is edited as the text it is written as, which goes back into the code in its place and
// nowhere else, one step to undo, the comments and the spacing of the file left alone. A boolean flips on a double click,
// the rest get a box over their row. Anything worked out (2 * 4, a spread, what a loop made) goes to the code instead.
internal sealed partial class HexScene
{
    private const string VALUE_EDITOR = "value_editor.hor";

    private TextBox? valueBox;

    // Where the box goes (over a row of the values, a cell of the table), null while nothing is edited, and where the
    // value being edited is written
    private Func<UIRect?>? editingAt;
    private SourceSpan editingSpan;

    /// <summary>A double click (or enter) on a row of the values.</summary>
    private void ActivateValue(UITreeItem item)
    {
        if (item.Tag is not ValueTag { Path.Length: > 0 } tag || tag.At is not { Value.IsKnown: true } at)
            return;

        // Written in another file, or worked out, it is the code that says what it is
        bool here = at.File is null || (document.Path is { } open && string.Equals(Path.GetFullPath(at.File), Path.GetFullPath(open), PlatformPaths.Comparison));
        if (!here || !at.Literal)
        {
            GoToValue(tag.Path, at);
            Say(here ? Said("values.worked_out") : Said("values.elsewhere"));
            return;
        }

        // The values are of the code as it was a moment ago, and a place in it is only good for that
        if (valuesAsked != (document, code.Document.Version))
        {
            Say(Said("values.behind"), error: true);
            return;
        }

        if (tag.Value is BooleanValue flag)
        {
            ReplaceValue(at.Value, flag.Value ? "false" : "true");
            return;
        }

        StartEditing(() => valuesTree.RowBounds(item) is { } row ? AfterName(row) : null, at.Value);
    }

    /// <summary>The part of a row of the values after its name, where the box goes.</summary>
    private static UIRect AfterName(UIRect row)
    {
        float left = row.Min.X + MathF.Min(row.Width * 0.45f, 140.0f);
        return new UIRect(new Vector2(left, row.Min.Y), new Vector2(MathF.Max(left + 80.0f, row.Max.X - 8.0f), row.Max.Y));
    }

    /// <summary>Puts the box over wherever the value is shown, with the value as it is written in it.</summary>
    private void StartEditing(Func<UIRect?> where, SourceSpan span)
    {
        if (valueBox is null)
        {
            valueBox = layout.Instantiate(VALUE_EDITOR, chrome.Root).Get<TextBox>("value_box");
            valueBox.OnSubmitted = text =>
            {
                if (editingAt is not null)
                    ReplaceValue(editingSpan, text);
                StopEditingValue();
            };
        }

        editingAt = where;
        editingSpan = span;
        valueBox.Text = code.Text.Substring(span.Index, Math.Min(span.Length, code.Text.Length - span.Index));
        valueBox.Visible = true;
        PlaceValueBox();
        valueBox.Focus();
        valueBox.SelectAll();
    }

    private void StopEditingValue()
    {
        editingAt = null;
        if (valueBox is not null)
        {
            valueBox.Visible = false;
            valueBox.Unfocus();
        }
    }

    /// <summary>Every tick while a value is edited, the box kept over its row, and put away when the row or the keyboard went.</summary>
    private void UpdateValueBox()
    {
        if (editingAt is null || valueBox is null)
            return;

        if (!valueBox.IsFocused || !PlaceValueBox())
            StopEditingValue();
    }

    /// <summary>Helper method to put the box where the value being edited is shown. False if that is out of sight.</summary>
    private bool PlaceValueBox()
    {
        if (editingAt?.Invoke() is not { } over || valueBox is null)
            return false;

        UIRect root = chrome.Root.Bounds;
        valueBox.Size = over.Size;
        valueBox.Position = over.Min - root.Min;
        return true;
    }

    /// <summary>
    /// Puts text in the code where a value was written, in place of it, as one step that can be undone. What was typed
    /// has to be a value of HIDL on its own, or nothing changes.
    /// </summary>
    private void ReplaceValue(SourceSpan span, string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0 || !IsValue(trimmed))
        {
            Say(Said("values.not_a_value", ("text", trimmed)), error: true);
            return;
        }

        string all = code.Text;
        if (span.Index < 0 || span.Index + span.Length > all.Length)
            return;

        code.Document.Select(PositionAt(all, span.Index), PositionAt(all, span.Index + span.Length));
        code.Document.Insert(trimmed);
        Say(Said("values.changed", ("text", trimmed)));
    }

    /// <summary>Whether a piece of text is a value of HIDL and nothing more, parsed as the value of a let.</summary>
    private static bool IsValue(string text)
    {
        try
        {
            var program = Horizon.HIDL.HIDLRuntime.Parse($"let value = {text};");
            return program.Body.Count == 1;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The line and column of a place in a text, by its index.</summary>
    private static UITextPosition PositionAt(string text, int index)
    {
        int line = 0, start = 0;
        for (int i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                start = i + 1;
            }
        }

        return new UITextPosition(line, index - start);
    }
}
