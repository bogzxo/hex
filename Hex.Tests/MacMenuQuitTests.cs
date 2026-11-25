using Hex.Dawn;
using Horizon.Platform;

namespace Hex.Tests;

/// <summary>
/// The pure decisions of the Mac's menu bar and quitting: where File, Exit is, the key Redo says, and how the application
/// being quit (Cmd+Q, the Dock's Quit) is told from its window being closed. No Objective-C in any of them.
/// </summary>
public sealed class MacMenuQuitTests
{
    [Theory]
    [InlineData(true, true, false)]   // a Mac with the menus in the system's bar quits from Hex, Quit Hex
    [InlineData(true, false, true)]   // a Mac whose menus are in the window has File, Exit
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]  // Windows and Linux never lose it
    public void Exit_is_left_out_only_of_the_menus_in_a_mac_bar(bool mac, bool native, bool shown) =>
        Assert.Equal(shown, DawnScene.ExitShown(mac, native));

    [Fact]
    public void Redo_says_shift_cmd_z_on_a_mac_and_ctrl_y_elsewhere()
    {
        Assert.Equal("Ctrl+Shift+Z", DawnScene.RedoShortcut(mac: true));
        Assert.Equal("Ctrl+Y", DawnScene.RedoShortcut(mac: false));

        // As the Mac's menu item has it, Shift and Command on a capital Z
        MacKeyEquivalent? item = MacKeyEquivalents.Map(DawnScene.RedoShortcut(mac: true));
        Assert.Equal(new MacKeyEquivalent("Z", MacKeyEquivalents.Command | MacKeyEquivalents.Shift), item);
        Assert.Equal("⇧⌘Z", MacKeyEquivalents.Glyphs("Ctrl+Shift+Z"));
    }

    [Fact]
    public void Alt_f4_is_only_a_function_key_with_option_which_the_hidden_exit_item_never_shows()
    {
        // F4 is the system's function key character, not a letter, and the item that has it is left out of the Mac bar
        MacKeyEquivalent? item = MacKeyEquivalents.Map("Alt+F4");
        Assert.NotNull(item);
        Assert.Equal(MacKeyEquivalents.Option, item.Value.Modifiers);
        Assert.Single(item.Value.Key);
        Assert.True(item.Value.Key[0] > 0x7F);
    }

    [Fact]
    public void A_quit_with_no_window_close_before_it_is_the_application_quitting()
    {
        var tracker = new AppQuitTracker();
        tracker.QuitSeen();
        Assert.True(tracker.TakeQuit());
    }

    [Fact]
    public void The_quit_that_follows_a_window_close_is_not_the_application_quitting()
    {
        // The red button: SDL says the window closed and then the quit it sends when the last window closes
        var tracker = new AppQuitTracker();
        tracker.WindowCloseSeen();
        tracker.QuitSeen();
        Assert.False(tracker.TakeQuit());
    }

    [Fact]
    public void A_quit_is_taken_once()
    {
        var tracker = new AppQuitTracker();
        tracker.QuitSeen();
        Assert.True(tracker.TakeQuit());
        Assert.False(tracker.TakeQuit());
    }

    [Fact]
    public void A_window_close_the_program_said_no_to_does_not_make_the_next_quit_a_close()
    {
        var tracker = new AppQuitTracker();
        tracker.WindowCloseSeen();
        tracker.QuitSeen();
        Assert.False(tracker.TakeQuit());

        // Later, Cmd+Q
        tracker.QuitSeen();
        Assert.True(tracker.TakeQuit());
    }

    [Fact]
    public void A_window_close_with_no_quit_after_it_is_forgotten_when_asked()
    {
        // A second window closing leaves SDL no quit to send, and the flag mustn't turn a Cmd+Q later into a close
        var tracker = new AppQuitTracker();
        tracker.WindowCloseSeen();
        Assert.False(tracker.TakeQuit());
        tracker.QuitSeen();
        Assert.True(tracker.TakeQuit());
    }
}
