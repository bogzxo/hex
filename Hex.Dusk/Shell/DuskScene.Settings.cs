using Hex.Shell;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dusk;

// The settings (Ctrl+,), Hex's one window of them, the terminal's section put in force as it changes and the editor's
// written to the file for Dawn to take in (its effects and its picture are Dusk's too and put in force here). The same
// window Dawn shows, one search over all of it.
internal sealed partial class DuskScene
{
    private SettingsWindow? settingsView;

    /// <summary>The page while it is up, for the self test.</summary>
    internal SettingsPage? SettingsPage => settingsView?.Window.Parent is not null ? settingsView.Page : null;

    private void ShowSettings()
    {
        if (chooser is { IsOpen: true })
            chooser.Close();

        if (settingsView is { Window.Parent: not null } open)
        {
            open.Window.BringToFront();
            open.Page.Search.Focus();
            return;
        }

        // The editor's section, written for Dawn, the effects and the picture of it put in force here as they change
        var editor = new FileSectionSource(host.Settings.Path, SettingsSections.EDITOR, EditorSettingsCatalog.Settings, EditorSettingsCatalog.Usual);
        editor.Pressed = (key, which) => SettingPressed(editor, key, which);
        editor.Changed = () =>
        {
            ReadEffects();
            ReadBackdrop();
        };

        settingsView = new SettingsWindow([new TerminalSettingsSource(host, EditorSettingsCatalog.THEME), editor, new WindowsSource(), new DefaultTerminalSource(said => Notify(said, error: true))], CloseSettings, EditorSettingsCatalog.Parts,
            sections: EditorSettingsCatalog.Sections);
        settingsView.Window.Color = compositor.Skin?.Palette.Raised;
        settingsView.Window.Glass = effectGlass ? HexGlass.Window(popupGlass) : null;
        chrome.Root.Add(settingsView.Window);

        // The glass under its sliders, as they make it while they're dragged
        settingsView.SetPanel(EditorSettingsCatalog.GLASS, new GlassPreview(640.0f) { Look = () => (popupGlass, sideGlass, effectGlass) });

        // The profiles of how Hex looks over all of it, see DuskScene.Looks
        AddLookPicker(settingsView);
        settingsView.Page.Search.Focus();
    }

    private void CloseSettings()
    {
        if (settingsView?.Window is { Parent: { } parent } window)
            parent.Remove(window);
        settingsView = null;
        lookPicker = null;
        panel.FocusedView?.Focus();

        // The editor's section has the effects and the picture, which are Dusk's too
        ReadEffects();
        ReadBackdrop();
    }
}
