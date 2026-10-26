using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

// What an extension can be told, kept in the settings file. Its section of the settings window is the window's
// (ExtensionSettingsSource), a row a setting like any other.
internal sealed partial class HexExtensions
{
    /// <summary>A setting of an extension, what it was told or else what it says it is to begin with, null for one it hasn't got.</summary>
    internal IRuntimeValue SettingOf(HexExtension extension, string key)
    {
        if (context.Settings.Values.ExtensionSettings.TryGetValue(extension.Id, out var told) && told.TryGetValue(key, out IRuntimeValue? value))
            return value;

        return extension.Settings.Find(setting => setting.Key == key)?.Default ?? Values.Null;
    }

    /// <summary>Changes a setting of an extension and keeps it, telling the extension (on_setting) unless it changed it itself.</summary>
    internal void SetSetting(HexExtension extension, string key, IRuntimeValue value, bool tell = false)
    {
        if (!context.Settings.Values.ExtensionSettings.TryGetValue(extension.Id, out var told))
            context.Settings.Values.ExtensionSettings[extension.Id] = told = new Dictionary<string, IRuntimeValue>(StringComparer.OrdinalIgnoreCase);

        told[key] = value;
        if (context.Settings.Values.Save() is { } why)
            context.Notices.Notify(why, error: true);

        if (tell && extension.Enabled && extension.Failed is null && !extension.Raise("on_setting", HexOf(extension), new StringValue(key), value))
            Failed(extension);
    }
}
