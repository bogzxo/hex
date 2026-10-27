using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;

using Key = Silk.NET.Input.Key;

namespace Hex.Dawn;

// The self test of laying the picture behind the code out by hand. Its button in the settings puts the overlay up over
// the code with the picture switched on for it, a drag inside moves it, a corner sizes it keeping its shape and the wheel
// scales it, while the code behind never gets a click. Reset puts it back where it comes, Done keeps it, and from the
// palette Escape puts it back as it was and Enter keeps it. Everything is put back as it was at the end.
internal sealed partial class DawnScene
{
    private void TestBackdropArrange(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "backdrop_arrange.cs");
        var was = (settings.BackdropOn, settings.BackdropScale, settings.BackdropAcross, settings.BackdropUp, settings.BackdropStretch);
        UIRect before = default;
        Vector2 from = default;
        (UITextPosition Caret, UITextPosition Anchor) caret = default;

        ArrangeOverlay Overlay() => BackdropArrange!;
        Button? ArrangeButton() => SettingsWindow is { } window
            ? Descendants(window).OfType<Button>().FirstOrDefault(button => button.Label == Said("settings_catalog.backdrop_arrange_button") && button.IsShownInHierarchy)
            : null;
        Button BarButton(Func<ArrangeOverlay, Button> which) => which(Overlay());
        Func<Vector2> Local(Func<Vector2> point) => () => chrome.ToWorld(point());
        void Shot(string name) => test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, name));
        });

        test.Run(() =>
        {
            File.WriteAllLines(file, Enumerable.Range(0, 80).Select(line => $"    var line{line} = Compute({line}) * {line % 7} + offset; // the code the picture sits behind"));
            Open(file);
            SwitchView(HexView.Code);
            settings.BackdropOn = false;
            ApplyBackdrop();
            CloseSettings();
            OpenSettings(EditorSettingsCatalog.BACKDROP);
        });
        test.WaitFor(() => SettingsPage is not null, 3.0f);
        test.Run(() => settingsView?.Reveal(EditorSettingsCatalog.ARRANGE_BACKDROP));
        test.WaitFor(() => SettingsPage?.IsRowInView(EditorSettingsCatalog.ARRANGE_BACKDROP) == true, 3.0f);
        test.Settle(0.6f);

        // Reveal leaves the row at the bottom edge of the rows, its button half under the fade there, a little further down it's all in sight
        test.Run(() => SettingsPage!.RowsScroll.Offset += 160.0f);
        test.Settle(0.2f);
        test.Check("backdrop arrange: the backdrop's section has a button to move and resize it", () => ArrangeButton() is not null);
        Shot("backdrop_arrange_settings.png");

        // The button, the overlay up over the code with the picture on for it and the settings out of the way
        test.Click(At(ArrangeButton));
        test.WaitFor(() => BackdropArrange is { Rect.IsEmpty: false }, 3.0f);
        test.Settle(0.3f);
        test.Check("backdrop arrange: the overlay is up over the code, the picture switched on for it, the settings out of the way", () =>
            BackdropArrange is { IsFocused: true } overlay && settings.BackdropOn && SettingsWindow is { Visible: false }
            && code.BackdropPlaced is { } placed && Near(placed.Center.X, overlay.Rect.Center.X, 1.0f));
        Shot("backdrop_arrange_up.png");

        // A drag inside moves it, as shares of the window, and the code behind gets nothing of it
        test.Run(() =>
        {
            before = Overlay().Rect;
            from = before.Center;
            caret = (code.Document.Caret, code.Document.Anchor);
        });
        test.Drag(Local(() => from), Local(() => from + new Vector2(-120.0f, 60.0f)));
        test.Wait();
        test.Check("backdrop arrange: a drag inside moves the picture with the pointer", () =>
            Near(Overlay().Rect.Center.X, before.Center.X - 120.0f, 2.0f) && Near(Overlay().Rect.Center.Y, before.Center.Y + 60.0f, 2.0f)
            && Near(Overlay().Rect.Width, before.Width, 0.5f));
        test.Check("backdrop arrange: kept as shares of the window, on the editor there and then", () =>
            settings.BackdropAcross > 0.0f && settings.BackdropUp > 0.0f && code.BackdropPlaced is { } placed && Near(placed.Center.X, Overlay().Rect.Center.X, 1.0f));

        // A click on the code beside the picture goes nowhere
        test.Click(Local(() => new Vector2(Overlay().Room!.Value.Min.X + 60.0f, Overlay().Room!.Value.Max.Y - 80.0f)));
        test.Wait();
        test.Check("backdrop arrange: nothing behind gets a click, the code's caret and the keyboard stay where they were", () =>
            (code.Document.Caret, code.Document.Anchor) == caret && !code.IsFocused && BackdropArrange is { IsFocused: true });

        // The top left corner pulled out sizes it, keeping its shape
        test.Run(() => before = Overlay().Rect);
        test.Drag(Local(() => new Vector2(before.Min.X, before.Max.Y)), Local(() => new Vector2(before.Min.X - 90.0f, before.Max.Y + 20.0f)));
        test.Wait();
        test.Check("backdrop arrange: a corner sizes it from the other corner, its shape kept", () =>
            Overlay().Rect.Width > before.Width + 80.0f && Near(Overlay().Rect.Width / Overlay().Rect.Height, before.Width / before.Height, 0.02f)
            && Near(Overlay().Rect.Max.X, before.Max.X, 1.0f) && Near(Overlay().Rect.Min.Y, before.Min.Y, 1.0f) && settings.BackdropScale > 0.5f);

        // The wheel over it scales it round the pointer
        test.Run(() => before = Overlay().Rect);
        test.Hover(Local(() => before.Center));
        test.Run(() => compositor.Scroll(1.0f));
        test.Wait();
        test.Check("backdrop arrange: the wheel scales it round the pointer", () =>
            Near(Overlay().Rect.Width, before.Width * ArrangeOverlay.WHEEL_STEP, 1.0f) && Near(Overlay().Rect.Center.X, before.Center.X, 1.0f));
        Shot("backdrop_arrange_moved.png");

        // Reset, back where it comes, and still being laid out
        test.Click(At(() => BarButton(overlay => overlay.ResetButton)));
        test.Wait();
        test.Check("backdrop arrange: Reset puts it back where it comes, the overlay still up", () =>
            BackdropArrange is not null && settings.BackdropScale == 0.5f && settings.BackdropAcross == 0.0f && settings.BackdropUp == 0.0f && settings.BackdropStretch == 1.0f);

        // A move and Done, kept and the settings back
        test.Run(() => from = Overlay().Rect.Center);
        test.Drag(Local(() => from), Local(() => from + new Vector2(-200.0f, 0.0f)));
        test.Wait();
        float kept = 0.0f;
        test.Run(() => kept = settings.BackdropAcross);
        test.Click(At(() => BarButton(overlay => overlay.DoneButton)));
        test.Wait();
        test.Check("backdrop arrange: Done keeps it, the overlay gone and the settings window back", () =>
            BackdropArrange is null && kept > 0.0f && settings.BackdropAcross == kept && settings.BackdropOn && SettingsWindow is { Visible: true });
        test.Check("backdrop arrange: and its rows say where it is now", () =>
            SettingsPage?.Settings.Any(row => row.Key == "backdrop_stretch") == true && new EditorSource(this).Get("backdrop_across") is Horizon.HIDL.Runtime.NumberValue { Value: var across } && across == kept);

        // From the palette, moved and then Escape, back where Done left it
        test.Run(() =>
        {
            CloseSettings();
            AllCommands().First(command => command.Text == Said("settings_palette.arrange_backdrop")).Picked();
        });
        test.WaitFor(() => BackdropArrange is { Rect.IsEmpty: false }, 3.0f);
        test.Run(() => from = Overlay().Rect.Center);
        test.Drag(Local(() => from), Local(() => from + new Vector2(0.0f, 150.0f)));
        test.Wait();
        test.Check("backdrop arrange: from the palette too, moved up", () => settings.BackdropUp > 0.05f);
        test.Run(() => UIKeyboard.Press(Key.Escape, false, false, false, false));
        test.Wait();
        test.Check("backdrop arrange: Escape puts it back as it was and the code has the keyboard again", () =>
            BackdropArrange is null && settings.BackdropUp == 0.0f && settings.BackdropAcross == kept && code.IsFocused);

        // And Enter keeps what it was moved to
        test.Run(ArrangeBackdrop);
        test.WaitFor(() => BackdropArrange is { Rect.IsEmpty: false }, 3.0f);
        test.Run(() => UIKeyboard.Press(Key.Up, false, true, false, false));
        test.Wait();
        test.Run(() => UIKeyboard.Press(Key.Enter, false, false, false, false));
        test.Wait();
        test.Check("backdrop arrange: Shift and an arrow nudge it ten pixels, Enter keeps it", () => BackdropArrange is null && settings.BackdropUp > 0.0f);

        test.Run(() =>
        {
            (settings.BackdropOn, settings.BackdropScale, settings.BackdropAcross, settings.BackdropUp, settings.BackdropStretch) = was;
            ApplyBackdrop();
            SaveSettings();
        });
        test.Wait();
    }
}
