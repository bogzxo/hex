using Hex.Core;
using Hex.Shell;

using Horizon.UI.Components;

namespace Hex.Dusk;

// The profiles of how Hex looks in Dusk's settings, Hex.Shell's picker of them over the editor's section of the file,
// which is both modes'. A look picked is written there, its theme for Dusk put on, and its glass and effects read back
// in, Dawn takes in the rest when it sees the file change.
internal sealed partial class DuskScene : IHexLookOwner
{
    // The picker while the settings are up
    private HexLookPicker? lookPicker;

    HexSettings IHexLookOwner.ReadSettings() => new(host.Settings.Path);

    string IHexLookOwner.TerminalTheme => settings.Theme;

    void IHexLookOwner.Keep(HexSettings kept, HexLook? picked)
    {
        kept.Save();
        if (picked is not null && HexLooks.TerminalTheme(picked) is { } theme && theme != settings.Theme)
            UseTheme(theme);

        ReadEffects();
        ReadBackdrop();
        settingsView?.Refresh();
    }

    /// <summary>A look put on from the palette, the same as a click on its card in the settings.</summary>
    private void PickLook(HexLook look)
    {
        HexSettings settings = new(host.Settings.Path);
        HexLooks.Apply(look, settings);
        ((IHexLookOwner)this).Keep(settings, look);
        lookPicker?.Refresh();
        Say(HexText.Get("looks.picked", ("name", look.Name)));
    }

    /// <summary>The picker put in the settings' window, told when a row of it changes the settings under it.</summary>
    private void AddLookPicker(SettingsWindow view)
    {
        lookPicker = new HexLookPicker(this, 640.0f);
        view.SetPanel(EditorSettingsCatalog.PROFILES, lookPicker);

        // Any row can make the settings another look or none of them, the cards say which after every change
        var changed = view.Page.OnChanged;
        view.Page.OnChanged = (key, value) =>
        {
            changed?.Invoke(key, value);
            lookPicker?.Refresh();
        };
        var reset = view.Page.OnReset;
        view.Page.OnReset = key =>
        {
            reset?.Invoke(key);
            lookPicker?.Refresh();
        };
    }
}
