using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The settings extensions describe in their extension object (a title, what it does, its kind and its default each) as
// rows of the settings like every other one, help under the name, a reset while one isn't what the extension says it
// comes as, and found by the search. They used to be a template of their own with the help in a tooltip and nothing to
// put one back.
internal sealed partial class DawnScene
{
    /// <summary>A row a setting of every extension that is on and hasn't a layout of its own for them, kept in the extensions section of the file.</summary>
    private sealed class ExtensionSettingsSource : ISettingsSource
    {
        private readonly DawnScene hex;

        // The extension and its setting behind a row, by the row's key
        private readonly Dictionary<string, (HexExtension Extension, ExtensionSetting Setting)> told = new(StringComparer.Ordinal);

        public string Section => SettingsSections.EXTENSIONS;

        public IReadOnlyList<UISetting> Settings { get; }

        public ExtensionSettingsSource(DawnScene hex)
        {
            this.hex = hex;
            var rows = new List<UISetting>();
            foreach (HexExtension extension in hex.extensions.All)
            {
                if (!extension.Enabled || extension.Failed is not null || extension.SettingsLayout is not null)
                    continue;

                foreach (ExtensionSetting setting in extension.Settings)
                {
                    string key = KeyOf(extension, setting);
                    if (!told.TryAdd(key, (extension, setting)))
                        continue;
                    rows.Add(RowOf(key, extension, setting));
                }
            }

            Settings = rows;
        }

        /// <summary>The key of a setting's row, which no other source has, the extension's folder and the setting's name.</summary>
        internal static string KeyOf(HexExtension extension, ExtensionSetting setting) => $"extension_{extension.Id}_{setting.Key}";

        private static UISetting RowOf(string key, HexExtension extension, ExtensionSetting setting)
        {
            string group = EXTENSION_SECTION + extension.Name;
            return setting.Kind switch
            {
                // A number with ends is kept between them, a slider when the extension asks for one, in the steps it says
                "number" => new UISetting(key, setting.Title, UIFormKind.Number, setting.About, group,
                    Least: setting.Ranged ? setting.Least : 0.0f, Most: setting.Ranged ? setting.Most : 0.0f,
                    Step: setting.Step > 0.0f ? setting.Step : 1.0f, Slider: setting.Slider && setting.Ranged),
                "flag" => new UISetting(key, setting.Title, UIFormKind.Flag, setting.About, group),
                "choice" => new UISetting(key, setting.Title, UIFormKind.Choice, setting.About, group, setting.Choices,
                    ChoiceLabels: setting.Labels, ChoiceHelp: setting.Help),
                "path" => new UISetting(key, setting.Title, UIFormKind.Text, setting.About, group, Buttons: [Said("extension_settings.choose")],
                    Placeholder: Said("extensions.a_path")),
                _ => new UISetting(key, setting.Title, UIFormKind.Text, setting.About, group)
            };
        }

        public IRuntimeValue? Get(string key)
        {
            if (!told.TryGetValue(key, out var row))
                return null;

            // What the file has made to fit, a number written past its ends shown at the end it's kept to, a choice that
            // isn't one shown as what the extension says it comes as
            IRuntimeValue now = hex.extensions.SettingOf(row.Extension, row.Setting.Key);
            return row.Setting.Kind switch
            {
                "number" or "choice" => row.Setting.Fitted(now) ?? row.Setting.Default,
                "flag" => Values.Bool(now is BooleanValue { Value: true }),
                _ => now is StringValue ? now : new StringValue(string.Empty)
            };
        }

        public bool IsModified(string key) =>
            told.TryGetValue(key, out var row) && !SettingValues.Same(hex.extensions.SettingOf(row.Extension, row.Setting.Key), row.Setting.Default);

        public void Set(string key, IRuntimeValue value)
        {
            if (!told.TryGetValue(key, out var row))
                return;

            // Only a value of the setting's own kind, a number brought between its ends and a choice that's one of its
            // choices. A page never hands it anything else but a test might
            if (row.Setting.Fitted(value) is { } fits)
                hex.extensions.SetSetting(row.Extension, row.Setting.Key, fits, tell: true);
        }

        public void Reset(string key)
        {
            if (told.TryGetValue(key, out var row))
                hex.extensions.SetSetting(row.Extension, row.Setting.Key, row.Setting.Default, tell: true);
        }

        public void Press(string key, int which)
        {
            if (!told.TryGetValue(key, out var row) || row.Setting.Kind != "path")
                return;

            // From the folder of the file it's set to when it's one that's there, the extension's folder otherwise
            string folder = row.Extension.Folder;
            if (Get(key) is StringValue { Value.Length: > 0 } now)
            {
                string at = Path.IsPathRooted(now.Value) ? now.Value : Path.Combine(row.Extension.Folder, now.Value);
                if (Path.GetDirectoryName(Path.GetFullPath(at)) is { } around && Directory.Exists(around))
                    folder = around;
            }

            hex.AskFile(Said("extension_settings.choose_title", ("setting", row.Setting.Title)), folder, [(Said("extension_settings.all_files"), "*.*")], picked =>
            {
                Set(key, new StringValue(Path.GetFullPath(picked)));
                hex.SettingsPage?.Refresh(key);
            });
        }
    }
}
