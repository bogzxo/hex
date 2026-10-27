using System.Numerics;

using Hex.Core;
using Hex.Shell;

using Horizon.UI;
using Horizon.UI.Components;

using Key = Silk.NET.Input.Key;

namespace Hex.Dusk;

// The self test of laying the picture behind the terminals out by hand. Its button in the settings puts the overlay up
// over the panes with the picture shown for it though it's off, a drag moves it and a corner sizes it while the terminal
// behind never gets a click, Cancel leaves the file as it was, Reset puts it back where it comes and Done writes it and
// switches it on. From the palette Escape puts it back. The file is tidied up after.
internal sealed partial class DuskScene
{
    private void TestBackdropArrange(DuskScript test)
    {
        UIRect before = default;
        Vector2 from = default;
        UIComponent? focusBefore = null;

        ArrangeOverlay Overlay() => BackdropArrange!;
        HexSettings InFile() => new(host.Settings.Path);
        Button? ArrangeButton() => settingsView?.Window is { Parent: not null } window
            ? Descendants(window).OfType<Button>().FirstOrDefault(button => button.Label == HexText.Get("settings_catalog.backdrop_arrange_button") && button.IsShownInHierarchy)
            : null;
        Func<Vector2> Local(Func<Vector2> point) => () => chrome.ToWorld(point());
        Func<Vector2> Of(Func<UIComponent?> find) => () => find() is { } found ? chrome.ToWorld(found.Bounds.Center) : default;
        static bool Near(float a, float b, float by) => MathF.Abs(a - b) <= by;

        test.Run(ShowSettings);
        test.Wait();
        test.Run(() => settingsView?.Reveal(EditorSettingsCatalog.ARRANGE_BACKDROP));
        test.WaitFor(() => SettingsPage?.IsRowInView(EditorSettingsCatalog.ARRANGE_BACKDROP) == true, 3.0f);
        test.Settle(0.6f);

        // Reveal leaves the row at the bottom edge of the rows, its button half under the fade there, a little further down it's all in sight
        test.Run(() => SettingsPage!.RowsScroll.Offset += 160.0f);
        test.Settle(0.2f);
        test.Check("backdrop arrange: the backdrop's section has a button to move and resize it, the picture off for the terminals", () =>
            ArrangeButton() is not null && panel.Backdrop is null);

        // The button, the overlay over the panes, the picture shown for it though the file still has it off
        test.Click(Of(ArrangeButton));
        test.WaitFor(() => BackdropArrange is { Rect.IsEmpty: false }, 3.0f);
        test.Settle(0.2f);
        test.Check("backdrop arrange: the overlay is up over the terminals with the picture, the settings out of the way", () =>
            BackdropArrange is { IsFocused: true } && panel.Backdrop is not null && settingsView?.Window is { Visible: false } && !InFile().BackdropTerminal);
        test.Run(() => Shot("backdrop-arrange-up"));

        // A drag inside moves it, on the panel there and then, and nothing's written yet
        test.Run(() =>
        {
            before = Overlay().Rect;
            from = before.Center;
        });
        test.Drag(Local(() => from), Local(() => from + new Vector2(-150.0f, 80.0f)));
        test.Wait();
        test.Check("backdrop arrange: a drag moves the picture behind the terminals with the pointer", () =>
            Near(Overlay().Rect.Center.X, before.Center.X - 150.0f, 2.0f) && Near(Overlay().Rect.Center.Y, before.Center.Y + 80.0f, 2.0f)
            && panel.BackdropOffset.X > 0.0f && panel.BackdropOffset.Y > 0.0f && InFile().TerminalBackdropAcross == 0.0f);

        // A press on a terminal beside it goes nowhere
        test.Run(() => focusBefore = panel.FocusedView);
        test.Click(Local(() => new Vector2(Overlay().Room!.Value.Min.X + 40.0f, Overlay().Room!.Value.Min.Y + 40.0f)));
        test.Wait();
        test.Check("backdrop arrange: nothing behind gets a click, the keyboard stays with the overlay", () =>
            compositor.Focus is ArrangeOverlay && panel.FocusedView == focusBefore && BackdropArrange is not null);

        // The bottom left corner pulled out sizes it from the top right, keeping its shape
        test.Run(() => before = Overlay().Rect);
        test.Drag(Local(() => before.Min), Local(() => before.Min - new Vector2(80.0f, 10.0f)));
        test.Wait();
        test.Check("backdrop arrange: a corner sizes it, its shape kept", () =>
            Overlay().Rect.Width > before.Width + 70.0f && Near(Overlay().Rect.Width / Overlay().Rect.Height, before.Width / before.Height, 0.02f)
            && Near(Overlay().Rect.Max.X, before.Max.X, 1.0f) && Near(Overlay().Rect.Max.Y, before.Max.Y, 1.0f));
        test.Run(() => Shot("backdrop-arrange-moved"));

        // Cancel, the file as it was and the picture off again
        test.Click(Of(() => Overlay().CancelButton));
        test.Wait();
        test.Check("backdrop arrange: Cancel leaves the file as it was, the picture off again and the settings back", () =>
            BackdropArrange is null && panel.Backdrop is null && !InFile().BackdropTerminal && InFile().TerminalBackdropScale == 0.5f
            && settingsView?.Window is { Visible: true });

        // Again, Reset and a move, and Done
        test.Click(Of(ArrangeButton));
        test.WaitFor(() => BackdropArrange is { Rect.IsEmpty: false }, 3.0f);
        test.Run(() => from = Overlay().Rect.Center);
        test.Drag(Local(() => from), Local(() => from + new Vector2(-60.0f, 0.0f)));
        test.Wait();
        test.Click(Of(() => Overlay().ResetButton));
        test.Wait();
        test.Check("backdrop arrange: Reset puts it back where it comes, the overlay still up", () =>
            BackdropArrange is not null && panel.BackdropOffset == Vector2.Zero && panel.BackdropScale == 0.5f && panel.BackdropStretch == 1.0f);
        test.Run(() => from = Overlay().Rect.Center);
        test.Drag(Local(() => from), Local(() => from + new Vector2(-200.0f, 40.0f)));
        test.Wait();
        float across = 0.0f;
        test.Run(() => across = panel.BackdropOffset.X);
        test.Click(Of(() => Overlay().DoneButton));
        test.Wait();
        test.Check("backdrop arrange: Done writes it to the file and switches the picture on for the terminals", () =>
            BackdropArrange is null && InFile() is { BackdropTerminal: true } kept && across > 0.0f && Near(kept.TerminalBackdropAcross, across, 0.0001f)
            && panel.Backdrop is not null && panel.BackdropOffset.X == kept.TerminalBackdropAcross);
        test.Run(() =>
        {
            CloseSettings();
            Shot("backdrop-arrange-done");
        });

        // From the palette, moved, and Escape puts it back where Done left it
        test.Run(ArrangeBackdrop);
        test.WaitFor(() => BackdropArrange is { Rect.IsEmpty: false }, 3.0f);
        test.Run(() => from = Overlay().Rect.Center);
        test.Drag(Local(() => from), Local(() => from + new Vector2(0.0f, 120.0f)));
        test.Wait();
        test.Run(() => UIKeyboard.Press(Key.Escape, false, false, false, false));
        test.Wait();
        test.Check("backdrop arrange: Escape puts it back as the file has it, the terminal has the keyboard again", () =>
            BackdropArrange is null && panel.BackdropOffset.X == InFile().TerminalBackdropAcross && panel.BackdropOffset.Y == InFile().TerminalBackdropUp
            && compositor.Focus is TerminalView);

        // The file as it came, the picture off for the terminals again
        test.Run(() =>
        {
            var editor = new FileSectionSource(host.Settings.Path, SettingsSections.EDITOR, EditorSettingsCatalog.Settings, EditorSettingsCatalog.Usual);
            foreach (string key in (string[])["backdrop_terminal", "terminal_backdrop_scale", "terminal_backdrop_across", "terminal_backdrop_up", "terminal_backdrop_stretch"])
                editor.Reset(key);
            ReadBackdrop();
        });
        test.Wait();
        test.Check("backdrop arrange: and the file put back, the picture's gone", () => panel.Backdrop is null);
    }
}
