using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// What an extension can be told, kept in the settings file, and its section of the settings window (SettingsPage makes
// it, a row a setting out of the template here or the extension's own layout).
internal sealed partial class HexScene
{
    private const string EXTENSION_SETTING = "extension_setting.hor";

    /// <summary>A setting of an extension, what it was told or else what it says it is to begin with, null for one it hasn't got.</summary>
    private IRuntimeValue SettingOf(HexExtension extension, string key)
    {
        if (settings.ExtensionSettings.TryGetValue(extension.Id, out var told) && told.TryGetValue(key, out IRuntimeValue? value))
            return value;

        return extension.Settings.Find(setting => setting.Key == key)?.Default ?? Values.Null;
    }

    /// <summary>Changes a setting of an extension and keeps it, telling the extension (on_setting) unless it changed it itself.</summary>
    private void SetSetting(HexExtension extension, string key, IRuntimeValue value, bool tell = false)
    {
        if (!settings.ExtensionSettings.TryGetValue(extension.Id, out var told))
            settings.ExtensionSettings[extension.Id] = told = new Dictionary<string, IRuntimeValue>(StringComparer.OrdinalIgnoreCase);

        told[key] = value;
        if (settings.Save() is { } why)
            Notify(why, error: true);

        if (tell && extension.Enabled && extension.Failed is null && !extension.Raise("on_setting", HexOf(extension), new StringValue(key), value))
            ExtensionFailed(extension);
    }

    // What the extensions made of their own layouts of settings, let go of as the window goes
    private readonly List<ExtensionUI> settingsMade = [];

    /// <summary>Lets go of what the extensions put on their sections, as the settings go.</summary>
    private void ForgetExtensionTabs()
    {
        foreach (ExtensionUI page in settingsMade)
            page.Closed = true;
        settingsMade.Clear();
    }

    /// <summary>A row for one thing an extension can be told, kept as it is changed and the extension told so (on_setting).</summary>
    private void AddExtensionSetting(HexExtension extension, ExtensionSetting setting, StackPanel options)
    {
        var made = layout.Instantiate(EXTENSION_SETTING, options);
        var title = made.Get<Label>("title");
        var text = made.Get<TextBox>("text_value");
        var number = made.Get<NumberBox>("number_value");
        var flag = made.Get<ToggleButton>("flag_value");

        title.Text = setting.Title;
        title.Tooltip = setting.About;
        text.Visible = setting.Kind is not ("number" or "flag");
        number.Visible = setting.Kind == "number";
        flag.Visible = setting.Kind == "flag";

        IRuntimeValue now = SettingOf(extension, setting.Key);
        if (text.Visible)
        {
            text.Text = now is StringValue said ? said.Value : string.Empty;
            text.Placeholder = setting.Kind == "path" ? Said("extensions.a_path") : string.Empty;
            text.Tooltip = setting.About;
            text.OnChanged = value => SetSetting(extension, setting.Key, new StringValue(value), tell: true);
        }
        else if (number.Visible)
        {
            number.Value = now is NumberValue amount ? amount.Value : 0;
            number.Tooltip = setting.About;
            number.OnValueChanged = value => SetSetting(extension, setting.Key, new NumberValue(value), tell: true);
        }
        else
        {
            flag.State = now is BooleanValue { Value: true };
            flag.Tooltip = setting.About;
            flag.OnPressed = () => SetSetting(extension, setting.Key, Values.Bool(flag.State), tell: true);
        }

        settingControls[$"extension_{extension.Id}_{setting.Key}"] = text.Visible ? text : number.Visible ? number : flag;
    }

    private string AboutExtension(HexExtension extension)
    {
        string by = extension.Author.Length > 0 ? Said("extensions.by", ("author", Literal(extension.Author))) : string.Empty;
        string where = extension.BuiltIn ? Said("extensions.built_in") : Said("extensions.own");
        string text = $"**{Literal(extension.Name)}** {Literal(extension.Version)}  {by}\n{Literal(extension.Description)}\n[color:#7f8a9e]{where}[/color]";
        return extension.Failed is { } why ? text + "\n[color:#ff7366]" + Literal(why) + "[/color]" : text;
    }
}
