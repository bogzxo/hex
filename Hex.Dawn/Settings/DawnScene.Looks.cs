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

        // A look saved or deleted is one more or one less in the menu
        FillLooks();
    }

    // The menu of the looks, filled again as they're saved and deleted
    private Menu? lookMenu;

    /// <summary>
    /// The looks in a menu of View, Hex's and then the ones saved in the settings, ticked while the settings are in one,
    /// so a look is a key or two away in the palette as well (View, Look, Frosted Glass). Only Hex's three were in it,
    /// a look of your own was only in the settings.
    /// </summary>
    private void AddLooks(Menu menu)
    {
        lookMenu = menu;
        FillLooks();
    }

    private void FillLooks()
    {
        if (lookMenu is not { } menu)
            return;

        menu.Clear();
        List<HexLook> all = HexLooks.All(settings);
        for (int i = 0; i < all.Count; i++)
        {
            // A line between Hex's and the user's own
            if (i == HexLooks.BuiltIn.Length)
                menu.AddSeparator();

            HexLook picked = all[i];
            MenuItem item = menu.Add(picked.Name, () => PickLook(picked));
            item.IsChecked = () => HexLooks.Current(settings, terminals.Settings.Theme)?.Name == picked.Name;
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
