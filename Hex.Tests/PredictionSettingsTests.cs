using Hex.Core;
using Hex.Shell;
using Horizon.HIDL;
using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Tests;

/// <summary>
/// Edit prediction's settings: what they come as, the key kept protected in the file and read back, a folder's file
/// unable to switch predictions on or point them anywhere, and a folder's patterns added to the user's.
/// </summary>
public class PredictionSettingsTests
{
    [Fact]
    public void Ollama_picker_preserves_exact_tags_and_a_missing_configured_value_when_the_server_reconnects()
    {
        var original = EditorSettingsCatalog.Settings.Single(row => row.Key == "edit_predictions_model");
        string[] installed = ["code:Q4_K_M", "other:latest"];
        var row = OllamaModelPicker.ModelRow(original, installed, "");
        Assert.Equal(["", "code:Q4_K_M", "other:latest"], row.Choices);
        Assert.Equal("code:Q4_K_M", row.LabelOf("code:Q4_K_M"));
        var missing = OllamaModelPicker.ModelRow(row, installed, "older:1");
        Assert.Equal(["older:1", "code:Q4_K_M", "other:latest"], missing.Choices);
        Assert.Same(missing, OllamaModelPicker.ModelRow(missing, installed, "older:1"));
        var empty = OllamaModelPicker.ModelRow(missing, [], "");
        Assert.Equal([""], empty.Choices);
        var offline = OllamaModelPicker.ModelRow(row, null, "older:1");
        Assert.Equal(original, offline);
    }

    // Obviously not a key anybody has
    private const string FAKE_KEY = "sk-test-not-a-key";

    // Whether a key is protected the way a settings file needs here without touching a Keychain or a secret service:
    // DPAPI on Windows, or the file alone when the environment says so
    private static bool KeyStoreSafe => OperatingSystem.IsWindows() || HexLinkSecrets.FileOnlyByEnvironment;

    private static void InFolder(Action<string> test)
    {
        string folder = Path.Combine(Path.GetTempPath(), "hex-prediction-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            test(folder);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void WriteEditor(string file, HIDLObject editor)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, SettingsSections.Write(file, (SettingsSections.EDITOR, editor)));
    }

    [Fact]
    public void The_settings_come_off_with_ollama_on_this_machine_and_the_secret_files_excluded()
    {
        var settings = new HexSettings(null);
        Assert.False(settings.EditPredictions);
        Assert.False(PredictionSettings.On(settings, "csharp"));
        Assert.Equal("ollama", PredictionSettings.Provider(settings));
        Assert.Equal("http://localhost:11434", PredictionSettings.ApiUrl(settings));
        Assert.Equal(string.Empty, PredictionSettings.Model(settings));
        Assert.Equal("infer", PredictionSettings.PromptFormat(settings));
        Assert.Equal(256, PredictionSettings.MaxTokens(settings));
        Assert.False(settings.PredictionShortcutOnly);
        Assert.Equal(TimeSpan.FromMilliseconds(100), PredictionSettings.Debounce(settings));
        Assert.True(settings.EditPredictionsWorkspace);
        Assert.False(PredictionSettings.Share(settings));
        Assert.False(PredictionSettings.Configured(settings));
        Assert.Equal(["plaintext", "markdown"], settings.EditPredictionsDisabledLanguages);
        Assert.Equal(["**/.env", "**/.env.*", "**/*.pem", "**/*.key", "**/*.pfx", "**/*.p12", "**/id_rsa*", "**/id_ed25519*", "**/*secret*",
            "**/*credential*", "**/.npmrc", "**/.pypirc", "**/.netrc", "**/*.kdbx"], PredictionSettings.Exclude(settings));
    }

    [Fact]
    public void On_follows_the_switch_the_folder_and_the_language()
    {
        var settings = new HexSettings(null) { EditPredictions = true, EditPredictionsModel = "zeta" };
        Assert.Equal(!HexAi.Off, PredictionSettings.On(settings, "csharp"));
        Assert.False(PredictionSettings.On(settings, "Markdown"));
        Assert.False(PredictionSettings.On(settings, "plaintext"));
        Assert.True(PredictionSettings.Configured(settings));

        settings.EditPredictionsWorkspace = false;
        Assert.False(PredictionSettings.On(settings, "csharp"));

        settings.EditPredictionsWorkspace = true;
        settings.EditPredictionsModel = string.Empty;
        settings.EditPredictionsProvider = "link";
        Assert.True(PredictionSettings.Configured(settings));
    }

    [Fact]
    public void Ollamas_address_follows_the_provider_to_its_completions_and_back_and_any_other_stays()
    {
        Assert.Equal("http://localhost:11434/v1/completions", PredictionSettings.UrlForProvider("openai", "http://localhost:11434"));
        Assert.Equal("http://localhost:11434", PredictionSettings.UrlForProvider("ollama", "http://localhost:11434/v1/completions"));
        Assert.Equal("http://box:11434", PredictionSettings.UrlForProvider("openai", "http://box:11434"));
        Assert.Equal("http://localhost:11434/", PredictionSettings.UrlForProvider("openai", "http://localhost:11434/"));
        Assert.Equal("http://localhost:11434/v1/completions", PredictionSettings.UrlForProvider("link", "http://localhost:11434/v1/completions"));
    }

    [Fact]
    public void A_key_kept_by_the_file_alone_round_trips()
    {
        string stored = PredictionKey.Wrap(FAKE_KEY, fileOnly: true);
        Assert.StartsWith(PredictionKey.PREFIX, stored);
        Assert.DoesNotContain(FAKE_KEY, stored);
        Assert.Equal(FAKE_KEY, PredictionKey.Unwrap(stored, out bool failed));
        Assert.False(failed);
        Assert.Equal(string.Empty, PredictionKey.Wrap(string.Empty, fileOnly: true));
    }

    [Fact]
    public void A_key_typed_by_hand_is_taken_as_it_is_and_a_broken_one_is_empty_and_says_so()
    {
        Assert.Equal(FAKE_KEY, PredictionKey.Unwrap("  " + FAKE_KEY + " ", out bool plainFailed));
        Assert.False(plainFailed);

        Assert.Equal(string.Empty, PredictionKey.Unwrap("protected:not base64!", out bool notBase64));
        Assert.True(notBase64);

        Assert.Equal(string.Empty, PredictionKey.Unwrap("protected:" + Convert.ToBase64String("nothing wrapped"u8.ToArray()), out bool notWrapped));
        Assert.True(notWrapped);
    }

    [Fact]
    public void The_key_falls_back_to_the_environment_when_the_setting_is_empty()
    {
        var settings = new HexSettings(null) { EditPredictionsApiKey = FAKE_KEY };
        Assert.Equal(FAKE_KEY, PredictionSettings.ApiKey(settings));
        settings.EditPredictionsApiKey = string.Empty;
        Assert.Equal(PredictionKey.FromEnvironment(), PredictionSettings.ApiKey(settings));
    }

    [Fact]
    public void On_Windows_the_key_is_protected_by_DPAPI_and_the_stored_text_has_no_key_in_it()
    {
        if (!OperatingSystem.IsWindows() || HexLinkSecrets.FileOnlyByEnvironment)
            return;

        string stored = PredictionKey.Wrap(FAKE_KEY);
        Assert.DoesNotContain(FAKE_KEY, stored);
        Assert.DoesNotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(FAKE_KEY)), stored);
        Assert.Equal(HexLinkProtection.Dpapi, HexLinkSecrets.ProtectionOf(Convert.FromBase64String(stored[PredictionKey.PREFIX.Length..])));
        Assert.Equal(FAKE_KEY, PredictionKey.Unwrap(stored, out bool failed));
        Assert.False(failed);
    }

    [Fact]
    public void The_key_is_written_protected_read_back_and_a_key_typed_into_the_file_is_protected_at_the_next_save()
    {
        if (!KeyStoreSafe)
            return;

        InFolder(folder =>
        {
            string file = Path.Combine(folder, "settings.hor");
            var written = new HexSettings(file) { EditPredictionsApiKey = FAKE_KEY };
            Assert.Null(written.Save());
            string text = File.ReadAllText(file);
            Assert.Contains("edit_predictions_api_key", text);
            Assert.Contains(PredictionKey.PREFIX, text);
            Assert.DoesNotContain(FAKE_KEY, text);

            // Saved again unchanged, the protected text stays the same
            Assert.Null(new HexSettings(file).Save());
            Assert.Equal(text, File.ReadAllText(file));

            var read = new HexSettings(file);
            Assert.Equal(FAKE_KEY, read.EditPredictionsApiKey);
            Assert.False(read.EditPredictionsApiKeyUnreadable);

            // Typed into the file by hand, used as it is and protected once the settings are saved
            WriteEditor(file, new HIDLObject().Add("edit_predictions_api_key", FAKE_KEY));
            var byHand = new HexSettings(file);
            Assert.Equal(FAKE_KEY, byHand.EditPredictionsApiKey);
            Assert.Null(byHand.Save());
            Assert.DoesNotContain(FAKE_KEY, File.ReadAllText(file));
            Assert.Equal(FAKE_KEY, new HexSettings(file).EditPredictionsApiKey);
        });
    }

    [Fact]
    public void A_protected_key_that_cant_be_read_is_empty_and_said_to_be_unreadable()
    {
        InFolder(folder =>
        {
            string file = Path.Combine(folder, "settings.hor");
            WriteEditor(file, new HIDLObject().Add("edit_predictions_api_key", "protected:" + Convert.ToBase64String("HXL1\u0009broken"u8.ToArray())));
            var settings = new HexSettings(file);
            Assert.Equal(string.Empty, settings.EditPredictionsApiKey);
            Assert.True(PredictionSettings.ApiKeyUnreadable(settings));
        });
    }

    [Fact]
    public void A_folders_settings_cant_switch_predictions_on_or_change_the_server_the_model_or_the_key()
    {
        InFolder(folder =>
        {
            string user = Path.Combine(folder, "settings.hor");
            WriteEditor(user, new HIDLObject().Add("edit_predictions_model", "mine").Add("edit_predictions_debounce", 375));
            var settings = new HexSettings(user);

            string project = Path.Combine(folder, "project");
            WriteEditor(HexSettings.WorkspacePathOf(project), new HIDLObject()
                .Add("edit_predictions", true)
                .Add("edit_predictions_invocation", "shortcut")
                .Add("edit_predictions_provider", "openai")
                .Add("edit_predictions_api_url", "http://elsewhere.example:8080")
                .Add("edit_predictions_api_key", FAKE_KEY)
                .Add("edit_predictions_model", "theirs")
                .Add("edit_predictions_share", true)
                .Add("edit_predictions_disabled_languages", Natives.List(Array.Empty<string>())));
            Assert.Null(settings.LoadWorkspace(project));

            Assert.False(settings.EditPredictions);
            Assert.Equal("ollama", settings.EditPredictionsProvider);
            Assert.Equal("http://localhost:11434", settings.EditPredictionsApiUrl);
            Assert.Equal(string.Empty, settings.EditPredictionsApiKey);
            Assert.Equal("mine", settings.EditPredictionsModel);
            Assert.False(settings.PredictionShortcutOnly);
            Assert.Equal(375, settings.EditPredictionsDebounce);
            Assert.False(settings.EditPredictionsShare);
            Assert.Equal(["plaintext", "markdown"], settings.EditPredictionsDisabledLanguages);
            Assert.Empty(settings.WorkspaceValues);
            Assert.All(new[] { "edit_predictions", "edit_predictions_provider", "edit_predictions_api_url", "edit_predictions_api_key", "edit_predictions_model",
                "edit_predictions_prompt_format", "edit_predictions_max_tokens", "edit_predictions_debounce", "edit_predictions_disabled_languages", "edit_predictions_share" },
                key => Assert.False(HexSettings.IsWorkspaceKey(key), key));
        });
    }

    [Fact]
    public void A_folder_switches_predictions_off_and_adds_patterns_without_taking_the_users_away()
    {
        InFolder(folder =>
        {
            string user = Path.Combine(folder, "settings.hor");
            WriteEditor(user, new HIDLObject().Add("edit_predictions", true).Add(HexSettings.EDIT_PREDICTIONS_EXCLUDE, Natives.List(["**/.env", "**/mine/**"])));
            var settings = new HexSettings(user);

            string project = Path.Combine(folder, "project");
            WriteEditor(HexSettings.WorkspacePathOf(project), new HIDLObject()
                .Add("edit_predictions_workspace", false)
                .Add(HexSettings.EDIT_PREDICTIONS_EXCLUDE, Natives.List(["**/fixtures/**"])));
            Assert.Null(settings.LoadWorkspace(project));

            Assert.False(PredictionSettings.On(settings));
            Assert.Equal(["**/.env", "**/mine/**", "**/fixtures/**"], PredictionSettings.Exclude(settings));

            // The user's file keeps only the user's patterns
            Assert.Null(settings.Save());
            var saved = SettingsSections.Section(SettingsSections.Read(user)!, SettingsSections.EDITOR);
            Assert.True(saved[HexSettings.EDIT_PREDICTIONS_EXCLUDE] is ListValue { Items.Count: 2 });
            Assert.False(saved.ContainsKey("edit_predictions_workspace"));

            // The folder let go of, the user's are what's left
            Assert.Null(settings.LoadWorkspace(null));
            Assert.Equal(["**/.env", "**/mine/**"], PredictionSettings.Exclude(settings));
            Assert.True(settings.EditPredictionsWorkspace);
        });
    }

    [Fact]
    public void The_rows_are_in_the_AI_section_under_the_switch_and_the_key_is_listed_only_as_set()
    {
        UISetting[] rows = [.. EditorSettingsCatalog.Settings.Where(row => row.Key.StartsWith("edit_predictions", StringComparison.Ordinal))];
        Assert.Equal(13, rows.Length);
        Assert.Equal(["after_typing", "shortcut"], rows.Single(row => row.Key == "edit_predictions_invocation").Choices!);
        Assert.All(rows, row => Assert.Equal(EditorSettingsCatalog.AI, row.Group));
        Assert.All(rows.Where(row => row.Key != "edit_predictions"), row => Assert.Equal("edit_predictions", row.DependsOn));
        Assert.Contains(EditorSettingsCatalog.AI, EditorSettingsCatalog.Sections);
        Assert.All(rows, row => Assert.True(EditorSettingsCatalog.Usual.ContainsKey(row.Key), row.Key));

        Assert.True(EditorSettingsCatalog.IsSecret(EditorSettingsCatalog.PREDICTION_KEY));
        Assert.True(EditorSettingsCatalog.Masked(EditorSettingsCatalog.PREDICTION_KEY, new StringValue(FAKE_KEY)) is StringValue { Value: "set" });
        Assert.True(EditorSettingsCatalog.Masked(EditorSettingsCatalog.PREDICTION_KEY, new StringValue(string.Empty)) is StringValue { Value: "" });
        Assert.True(EditorSettingsCatalog.Masked("edit_predictions_model", new StringValue("zeta")) is StringValue { Value: "zeta" });
    }
}
