using Hex.Core;
using Hex.Shell;

using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dusk;

// The Settings window (Ctrl+,), the same one for all of Hex. The terminal section is applied as it changes and the
// editor section written to the file for Dawn to reload (its effects and its picture are Dusk's too and applied here). The same
// window Dawn shows, one search over all of it.
internal sealed partial class DuskScene
{
    private SettingsWindow? settingsView;
    private OllamaModelPicker? ollamaModels;

    // The part of the settings that's the terminal's, what Dusk's window is for
    private const string TERMINAL_PART = "Terminal";

    /// <summary>The editor's rows Dusk's window shows, those Dawn's does but the one that opens the Local AI window, which Dusk hasn't got.</summary>
    internal static readonly UISetting[] DuskEditorRows = [.. EditorSettingsCatalog.Shown.Where(row => row.Key != EditorSettingsCatalog.LOCAL_AI_WINDOW)];

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

        // The editor section, written for Dawn, its effects and picture applied here as they change. Local AI's rows are
        // the Local AI window's, which is Dawn's, so neither they nor the row that opens it are here
        var editor = new FileSectionSource(host.Settings.Path, SettingsSections.EDITOR, DuskEditorRows, EditorSettingsCatalog.Usual);
        editor.Pressed = (key, which) => SettingPressed(editor, key, which);
        editor.Changed = () =>
        {
            ReadEffects();
            ReadBackdrop();
        };

        // Editor · Text as well, the tab size and the rest Dawn applies to its files, written into the same section.
        // Each source keeps the keys of the section it doesn't have, so the two write side by side
        var text = new FileSectionSource(host.Settings.Path, SettingsSections.EDITOR, EditorSettingsCatalog.Text, EditorSettingsCatalog.Usual);

        if (!HexAi.Off)
            ollamaModels = new();
        settingsView = new SettingsWindow(
            [new TerminalSettingsSource(host, EditorSettingsCatalog.THEME), new EditorSettingsSource(editor), text,
                new WindowsSource(), new DefaultTerminalSource(said => Notify(said, error: true))],
            CloseSettings, EditorSettingsCatalog.Parts, sections: EditorSettingsCatalog.Sections, texts: SettingsPageTexts.Make());

        // Dusk's window is the terminal's, the other parts fold away the first time it opens and a search puts the
        // terminal's rows a little over the rest
        settingsView.Page.HomePart = TERMINAL_PART;
        settingsView.Window.Color = compositor.Skin?.Palette.Raised;
        settingsView.Window.Glass = effectGlass ? HexGlass.Window(popupGlass) : null;

        chrome.Root.Add(settingsView.Window);

        // The glass under its sliders, as they make it while they're dragged
        settingsView.SetPanel(EditorSettingsCatalog.GLASS, new GlassPreview(640.0f) { Look = () => (popupGlass, sideGlass, effectGlass) });

        // The profiles of how Hex looks over all of it, see DuskScene.Looks
        AddLookPicker(settingsView);
        settingsView.Page.GoToPart(TERMINAL_PART);
        settingsView.Page.Search.Focus();
    }

    // Dusk writes Dawn's section directly, so it must translate the key at the file boundary itself.
    private sealed class EditorSettingsSource(FileSectionSource file) : ISettingsSource
    {
        public string Section => file.Section;
        public IReadOnlyList<UISetting> Settings => file.Settings;
        public bool IsModified(string key) => file.IsModified(key);
        public IRuntimeValue? Default(string key) => file.Default(key);
        public void Reset(string key) => file.Reset(key);
        public void Press(string key, int which) => file.Press(key, which);

        public IRuntimeValue? Get(string key) => key == EditorSettingsCatalog.PREDICTION_KEY
            ? new StringValue(ReadPredictionKey(file.Get(key)) ?? string.Empty)
            : file.Get(key);

        public void Set(string key, IRuntimeValue value)
        {
            if (key != EditorSettingsCatalog.PREDICTION_KEY)
            {
                file.Set(key, value);
                return;
            }

            if (value is not StringValue text)
                return;

            string entered = text.Value.Trim();
            // Submitting the empty box must preserve an unreadable key. Reset can still remove it explicitly.
            if (entered.Length == 0 && ReadPredictionKey(file.Get(key)) is null)
                return;

            file.Set(key, new StringValue(PredictionKey.Wrap(entered)));
        }

        private static string? ReadPredictionKey(IRuntimeValue? value)
        {
            if (value is not StringValue stored)
                return string.Empty;

            try
            {
                string key = PredictionKey.Unwrap(stored.Value, out bool failed);
                return failed ? null : key;
            }
            catch (HexLinkSecretsUnavailableException)
            {
                return null;
            }
        }
    }

    private void CloseSettings()
    {
        if (settingsView?.Window is { Parent: { } parent } window)
            parent.Remove(window);
        ollamaModels?.Update(null);
        ollamaModels = null;
        settingsView = null;
        lookPicker = null;
        panel.FocusedView?.Focus();

        // The editor's section has the effects and the picture, which are Dusk's too
        ReadEffects();
        ReadBackdrop();
    }
}
