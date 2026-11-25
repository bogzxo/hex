using Horizon.UI;
using Horizon.UI.Components;

using Key = Horizon.Input.Key;

namespace Hex.Tests;

/// <summary>
/// What a Mac shows and types that nobody else does, all of it without a Mac. The labels of shortcuts as a Mac's
/// keyboard writes them (⇧⌘P), the other systems' left as they were written, and the Cocoa keys the control key has
/// in a text (control with A, E, F, B, N, P, D, H, K, Y, T and O), only on a Mac, only for the control key itself.
/// </summary>
public sealed class MacKeysTests
{
    [Theory]
    [InlineData("Ctrl+Shift+P", "⇧⌘P")]
    [InlineData("Ctrl+Alt+F", "⌥⌘F")]
    [InlineData("Ctrl+S", "⌘S")]
    [InlineData("Ctrl+Alt+Shift+Z", "⌥⇧⌘Z")]
    [InlineData("Shift+Alt+.", "⌥⇧.")]
    [InlineData("RawCtrl+A", "⌃A")]
    [InlineData("RawCtrl+Ctrl+Alt+Shift+Space", "⌃⌥⇧⌘Space")]
    [InlineData("Ctrl+Enter", "⌘↩")]
    [InlineData("Ctrl+Backspace", "⌘⌫")]
    [InlineData("Del", "⌦")]
    [InlineData("Shift+Delete", "⇧⌦")]
    [InlineData("Esc", "⎋")]
    [InlineData("Ctrl+Tab", "⌘⇥")]
    [InlineData("Ctrl+Shift+Tab", "⇧⌘⇥")]
    [InlineData("Alt+Left", "⌥←")]
    [InlineData("Alt+Right", "⌥→")]
    [InlineData("Alt+Up", "⌥↑")]
    [InlineData("Alt+Down", "⌥↓")]
    [InlineData("Ctrl+PageUp", "⌘⇞")]
    [InlineData("Ctrl+PageDown", "⌘⇟")]
    [InlineData("Home", "↖")]
    [InlineData("Ctrl+End", "⌘↘")]
    [InlineData("Ctrl+Space", "⌘Space")]
    [InlineData("F11", "F11")]
    [InlineData("Ctrl+Shift+F5", "⇧⌘F5")]
    [InlineData("Ctrl+`", "⌘`")]
    [InlineData("Ctrl+Shift+[", "⇧⌘[")]
    [InlineData("Ctrl+,", "⌘,")]
    [InlineData("Ctrl++", "⌘+")]
    [InlineData("Ctrl+-", "⌘-")]
    public void A_shortcut_is_written_the_way_a_mac_writes_it(string written, string shown) =>
        Assert.Equal(shown, UIShortcutText.Shown(written, mac: true));

    [Theory]
    [InlineData("Ctrl+K Z", "⌘K Z")]
    [InlineData("Ctrl+K Ctrl+S", "⌘K ⌘S")]
    [InlineData("Ctrl+\\ Ctrl+E", "⌘\\ ⌘E")]
    public void A_chord_keeps_a_space_between_its_keys(string written, string shown) =>
        Assert.Equal(shown, UIShortcutText.Shown(written, mac: true));

    [Theory]
    [InlineData("Ctrl+Shift+P")]
    [InlineData("Ctrl+Alt+F")]
    [InlineData("Ctrl+K Ctrl+S")]
    [InlineData("Ctrl++")]
    [InlineData("Alt+Left")]
    [InlineData("RawCtrl+A")]
    [InlineData("Del")]
    [InlineData("")]
    public void Anywhere_else_a_shortcut_is_shown_as_it_was_written(string written) =>
        Assert.Equal(written, UIShortcutText.Shown(written, mac: false));

    [Fact]
    public void The_glyphs_are_in_apples_order_whatever_order_they_were_written_in()
    {
        Assert.Equal("⌃⌥⇧⌘K", UIShortcutText.Shown("Alt+Shift+Ctrl+RawCtrl+K", mac: true));
        Assert.Equal("⌥⇧⌘K", UIShortcutText.Shown("Shift+Alt+Cmd+K", mac: true));
    }

    private static (char Code, bool Repeats) Cocoa(Key key, bool control = true, bool primary = false, bool shift = false, bool alt = false, bool mac = true) =>
        UIKeyboard.Meaning(key, primary, shift, alt, mac, visualStudio: false, control);

    [Theory]
    [InlineData(Key.A, UIKeyboard.HOME)]
    [InlineData(Key.E, UIKeyboard.END)]
    [InlineData(Key.F, UIKeyboard.RIGHT)]
    [InlineData(Key.B, UIKeyboard.LEFT)]
    [InlineData(Key.N, UIKeyboard.DOWN)]
    [InlineData(Key.P, UIKeyboard.UP)]
    [InlineData(Key.D, UIKeyboard.DELETE)]
    [InlineData(Key.H, UIKeyboard.BACKSPACE)]
    [InlineData(Key.K, UIKeyboard.KILL_LINE)]
    [InlineData(Key.Y, UIKeyboard.YANK)]
    [InlineData(Key.T, UIKeyboard.TRANSPOSE)]
    [InlineData(Key.O, UIKeyboard.OPEN_LINE)]
    public void Control_with_a_letter_is_a_cocoa_key_on_a_mac(Key key, char code) =>
        Assert.Equal(code, Cocoa(key).Code);

    [Fact]
    public void Command_keeps_its_keys_with_control_held_too()
    {
        Assert.Equal(UIKeyboard.SELECT_ALL, Cocoa(Key.A, control: false, primary: true).Code);
        Assert.Equal(UIKeyboard.SELECT_ALL, Cocoa(Key.A, control: true, primary: true).Code);
        Assert.Equal(UIKeyboard.NEXT_OCCURRENCE, Cocoa(Key.D, control: true, primary: true).Code);
        Assert.Equal(UIKeyboard.REDO, Cocoa(Key.Y, control: true, primary: true).Code);
    }

    [Fact]
    public void Shift_option_or_nothing_held_are_not_the_cocoa_keys()
    {
        Assert.NotEqual(UIKeyboard.HOME, Cocoa(Key.A, shift: true).Code);
        Assert.NotEqual(UIKeyboard.KILL_LINE, Cocoa(Key.K, alt: true).Code);
    }

    [Fact]
    public void Control_is_the_primary_key_elsewhere_so_the_cocoa_keys_do_not_exist_there()
    {
        Assert.Equal('\0', Cocoa(Key.K, mac: false).Code);
        Assert.Equal(UIKeyboard.SELECT_ALL, UIKeyboard.Meaning(Key.A, primary: true, shift: false, alt: false, mac: false, visualStudio: false, control: true).Code);
        Assert.Equal(UIKeyboard.REDO, UIKeyboard.Meaning(Key.Y, primary: true, shift: false, alt: false, mac: false, visualStudio: false, control: true).Code);
    }

    [Fact]
    public void Moving_and_deleting_keys_repeat_when_held_and_the_others_do_not()
    {
        Assert.True(Cocoa(Key.F).Repeats);
        Assert.True(Cocoa(Key.D).Repeats);
        Assert.False(Cocoa(Key.K).Repeats);
        Assert.False(Cocoa(Key.T).Repeats);
    }

    [Fact]
    public void The_cocoa_codes_are_editing_keys_a_text_box_does_not_type()
    {
        foreach (char code in new[] { UIKeyboard.KILL_LINE, UIKeyboard.YANK, UIKeyboard.TRANSPOSE, UIKeyboard.OPEN_LINE })
            Assert.True(UIKeyboard.IsEditingKey(code));
    }

    private static UITextDocument Document(string text, int line, int column)
    {
        var document = new UITextDocument { Text = text };
        document.MoveTo(new UITextPosition(line, column));
        return document;
    }

    [Fact]
    public void Kill_takes_the_rest_of_the_line_and_then_its_new_line()
    {
        var document = Document("one two\nthree", 0, 3);
        Assert.Equal(" two", document.KillToLineEnd());
        Assert.Equal("one\nthree", document.Text);

        Assert.Equal("\n", document.KillToLineEnd());
        Assert.Equal("onethree", document.Text);
        Assert.Equal(new UITextPosition(0, 3), document.Caret);

        document.MoveTo(new UITextPosition(0, 8));
        Assert.Equal(string.Empty, document.KillToLineEnd());
        Assert.Equal("onethree", document.Text);
    }

    [Fact]
    public void Kill_is_one_step_to_undo()
    {
        var document = Document("one two", 0, 3);
        document.KillToLineEnd();
        document.Undo();
        Assert.Equal("one two", document.Text);
    }

    [Fact]
    public void Transpose_swaps_the_two_characters_round_the_caret()
    {
        var document = Document("abcd", 0, 2);
        document.Transpose();
        Assert.Equal("acbd", document.Text);
        Assert.Equal(new UITextPosition(0, 3), document.Caret);

        document.MoveTo(new UITextPosition(0, 4));
        document.Transpose();
        Assert.Equal("acdb", document.Text);

        document.MoveTo(new UITextPosition(0, 0));
        document.Transpose();
        Assert.Equal("acdb", document.Text);
    }

    [Fact]
    public void Open_line_adds_a_line_after_the_caret_and_leaves_the_caret_there()
    {
        var document = Document("onetwo", 0, 3);
        document.OpenLine();
        Assert.Equal("one\ntwo", document.Text);
        Assert.Equal(new UITextPosition(0, 3), document.Caret);
    }
}
