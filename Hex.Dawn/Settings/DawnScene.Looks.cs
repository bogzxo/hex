using Hex.Core;
using Hex.Shell;

using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The profiles of how Hex looks in Dawn's settings, Hex.Shell's picker of them over Dawn's own settings. A look picked
// is put in force there and then, its themes and all, and kept in the file for Dusk to take in.
internal sealed partial class DawnScene : IHexLookOwner
{
    // The picker while the settings are up
    private HexLookPicker? lookPicker;

    HexSettings IHexLookOwner.ReadSettings() => settings;

    string IHexLookOwner.TerminalTheme => terminals.Settings.Theme;

    void IHexLookOwner.Keep(HexSettings kept, HexLook? picked)
    {
        if (picked is not null)
        {
            if (HexLooks.HasEditorTheme(picked))
                UseEditorSkin(settings.EditorSkin, settings.EditorTheme, save: false);
            if (HexLooks.TerminalTheme(picked) is { } theme && theme != terminals.Settings.Theme)
                new TerminalSettingsSource(terminals).Set("theme", new StringValue(theme));
        }

        ApplyEffects();
        SaveSettings();
        settingsView?.Refresh();
    }

    /// <summary>
    /// The looks Hex comes with in a menu of View, ticked while the settings are in one, and the rest of them in the
    /// settings, so a look is a key or two away in the palette as well (View, Look, Frosted Glass).
    /// </summary>
    private void AddLooks(Menu menu)
    {
        foreach (HexLook look in HexLooks.BuiltIn)
        {
            HexLook picked = look;
            MenuItem item = menu.Add(look.Name, () => PickLook(picked));
            item.IsChecked = () => HexLooks.Current(settings, terminals.Settings.Theme) == picked;
        }

        menu.AddSeparator();
        menu.Add(Said("looks.all"), () => OpenSettings(EditorSettingsCatalog.PROFILES), icon: "settings-gear");
    }

    /// <summary>A look put on from outside the settings, the palette or the menu, the same as a click on its card.</summary>
    private void PickLook(HexLook look)
    {
        HexLooks.Apply(look, settings);
        ((IHexLookOwner)this).Keep(settings, look);
        lookPicker?.Refresh();
        Say(Said("looks.picked", ("name", look.Name)));
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
