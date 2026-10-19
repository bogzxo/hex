using Horizon.HIDL.Runtime;

namespace Hex.Dusk;

// The self test of the picture behind the terminals, Dawn's picture behind the code switched on for them in the one
// window of settings, put behind the panes there and then, moved and made fainter by its sliders, and gone again.
internal sealed partial class DuskScene
{
    private void TestBackdrop(DuskScript test)
    {
        test.Run(ShowSettings);
        test.Wait();
        test.Check("backdrop: the settings are a window of their own, the picture's rows in it", () =>
            settingsView is { Window.Parent: not null } && SettingsPage is { } page && page.Settings.Any(setting => setting.Key == "backdrop_terminal")
            && page.Settings.Any(setting => setting.Key == "backdrop_image" && setting.Buttons is { Length: 2 }));
        test.Check("backdrop: off for the terminals as it comes", () => panel.Backdrop is null);

        test.Run(() => SettingsPage!.Enter("backdrop_terminal", Values.Bool(true)));
        test.Wait();
        test.Check("backdrop: on, the picture Hex comes with is behind the terminals there and then", () =>
            panel.Backdrop is { } picture && panel.FocusedView?.Backdrop == picture);

        test.Run(() =>
        {
            SettingsPage!.Enter("backdrop_opacity", new NumberValue(0.5f));
            SettingsPage.Enter("terminal_backdrop_across", new NumberValue(0.3f));
            SettingsPage.Enter("terminal_backdrop_scale", new NumberValue(0.3f));
            SettingsPage.Enter("backdrop_across", new NumberValue(0.6f));
        });
        test.Wait();
        test.Check("backdrop: and its sliders make it fainter, move it and size it, by the terminals' own place and size and not the code's", () =>
            panel.BackdropOpacity == 0.5f && panel.BackdropOffset.X == 0.3f && panel.BackdropScale == 0.3f && panel.FocusedView?.BackdropOpacity == 0.5f);

        test.Run(() =>
        {
            SettingsPage!.Reset("backdrop_opacity");
            SettingsPage.Reset("terminal_backdrop_across");
            SettingsPage.Reset("terminal_backdrop_scale");
            SettingsPage.Reset("backdrop_across");
            SettingsPage.Enter("backdrop_terminal", Values.Bool(false));
        });
        test.Wait();
        test.Check("backdrop: and off again it's gone", () => panel.Backdrop is null);
        test.Run(CloseSettings);
        test.Wait();
        test.Check("backdrop: the window's x or Escape puts it away", () => settingsView is null);
    }
}
