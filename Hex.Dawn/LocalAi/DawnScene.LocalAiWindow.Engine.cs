using System.Globalization;
using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The Local AI window's Engine page (llama.cpp's build and server, Ollama's address, which one runs) and its Predictions and Privacy
// pages (how predictions behave, what's never sent). Every row is an explained row.
internal sealed partial class DawnScene
{
    private void DrawLocalAiEngine(StackPanel body)
    {
        LocalAiHeading(body, Said("local_ai.engine_choice"));
        LocalAiChoicesRow(body, "engine_choice", HexSettings.LocalAiEngines, settings.LocalAiEngine, value => "local_ai.engine_" + value, value =>
        {
            SetLocalAiEngine(value);
            RefreshLlamaSetup();
        });
        // The row's own line says what the engine is; a second one saying it again was cut

        if (settings.LocalAiEngine == HexSettings.LOCAL_AI_OLLAMA)
            DrawLocalAiOllama(body);
        else
            DrawLocalAiLlamaCpp(body);
    }

    // The engine written to the settings, and the prediction provider with it
    private void SetLocalAiEngine(string engine)
    {
        settings.LocalAiEngine = engine;
        settings.EditPredictionsProvider = engine == HexSettings.LOCAL_AI_OLLAMA ? "ollama" : PredictionProviders.LLAMACPP;
        SaveSettings();
        SettingsPage?.Refresh("edit_predictions_provider");
        predictionItemWritten = false;
        if (engine == HexSettings.LOCAL_AI_OLLAMA)
            AskOllama();
    }

    private void DrawLocalAiOllama(StackPanel body)
    {
        LocalAiHeading(body, Said("local_ai.ollama_heading"));
        var control = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8, Size = new Vector2(430, LINK_ROW) };
        var box = control.Add(new TextBox { Text = settings.EditPredictionsApiUrl, Size = new Vector2(320, LINK_ROW), Anchor = Origin.Left, Pivot = Origin.Left });
        box.OnSubmitted = typed =>
        {
            if (Uri.TryCreate(typed.Trim(), UriKind.Absolute, out _))
            {
                settings.EditPredictionsApiUrl = typed.Trim();
                SaveSettings();
                localAiOllamaUp = null;
                AskOllama();
            }
            RefreshLlamaSetup();
        };
        control.Add(LinkButton(Said("local_ai.ollama_check"), "refresh", () =>
        {
            localAiOllamaUp = null;
            AskOllama();
            RefreshLlamaSetup();
        }));
        LocalAiExplainedRow(body, "engine_ollama_url", control);
        if (!LocalAiSearching)
            body.Add(LinkLine(localAiOllamaUp switch { true => "$(pass-filled) " + Said("local_ai.ollama_up"), false => "$(error) " + Said("local_ai.ollama_down"), _ => "$(loading~spin) " + Said("local_ai.ollama_asking") }));
    }

    private void DrawLocalAiLlamaCpp(StackPanel body)
    {
        AskLlamaMachine();
        LocalAiHeading(body, Said("local_ai.build_heading"));
        string? found = FoundLlamaServer();
        // Up to date, or what the newest is, once the machine and the release are known; installed alone before that
        string buildWords = found is null ? "$(warning) " + Said("local_ai.build_missing")
            : llamaPlatform is not null && llamaRelease is { } newest && llamaBackend is { } kept
                ? (LocalAiSetupFlow.BuildInstalled(found, newest.Tag, kept) ? "$(check) " + Said("local_ai.build_up_to_date") : "$(cloud-download) " + Said("local_ai.build_update_available", ("tag", newest.Tag)))
                : "$(check) " + Said("local_ai.build_found_short");
        var build = new Label { Text = UIRichText.Literal(buildWords), TextScale = LINK_TEXT, Anchor = Origin.Left, Pivot = Origin.Left, Size = new Vector2(300, LINK_ROW), Align = Origin.Left };
        if (LocalAiExplainedRow(body, "engine_build", build) is not null && !LocalAiSearching)
            DrawLocalAiBuild(body, allowPickOwn: true);

        LocalAiHeading(body, Said("llamacpp.folder_heading"));
        var folder = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8, Size = new Vector2(430, LINK_ROW) };
        LlamaFolderRow(folder, settings.LlamaCppFolderInForce, value =>
        {
            settings.LlamaCppFolder = value;
            SaveSettings();
        });
        // The free space sits on the row, after Browse, not on a line of its own
        string freeText = Said("local_ai.folder_free", ("free", LlamaFree(settings.LlamaCppFolderInForce)));
        float freeWidth = LocalAiTextWidth(freeText, 6.5f, 8);
        folder.Add(new Label { Text = UIRichText.Literal(freeText), TextScale = LINK_SMALL, Dim = true, Size = new Vector2(freeWidth, LINK_ROW), Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left });
        folder.Size = new Vector2(folder.Size.X + 8 + freeWidth, LINK_ROW);
        LocalAiExplainedRow(body, "engine_folder", folder);

        LocalAiHeading(body, Said("local_ai.server_heading"));
        LocalAiTextRow(body, "engine_port", settings.LlamaCppPort.ToString(CultureInfo.InvariantCulture), typed =>
        {
            if (int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                settings.LlamaCppPort = Math.Clamp(number, 1024, 65535);
            SaveSettings();
            RefreshLlamaSetup();
        }, width: 90);
        LocalAiToggleRow(body, "engine_autostart", settings.LlamaCppAutostart, on =>
        {
            settings.LlamaCppAutostart = on;
            SaveSettings();
            RefreshLlamaSetup();
        }, reset: () => LocalAiApplyRecommended("engine_autostart"));
        LocalAiTextRow(body, "extra_args", settings.LlamaCppExtraArgs, typed =>
        {
            settings.LlamaCppExtraArgs = typed;
            SaveSettings();
            RefreshLlamaSetup();
        }, width: 320, placeholder: Said("local_ai.none"));
    }

    // llama.cpp's build: found, or the one this machine wants downloaded; the same in the setup flow and on the Engine page
    private void DrawLocalAiBuild(StackPanel body, bool allowPickOwn)
    {
        string? found = FoundLlamaServer();
        bool going = Context.Work.Find(LLAMA_BUILD_WORK) is not null;
        if (found is not null && !going)
        {
            body.Add(LinkLine("$(check) " + Said("llamacpp.build_found", ("path", LocalAiPathText(found)))));
            if (settings.LlamaCppServer != found)
            {
                settings.LlamaCppServer = found;
                SaveSettings();
            }
        }
        else if (!going)
            body.Add(LinkLine(Said("llamacpp.build_intro")));

        if (llamaPlatform is { } platform && llamaRelease is { } release)
        {
            // The backends this release has for the machine, the first the one to use
            List<string> backends = LlamaCppBuilds.Backends(platform, release.Assets);
            if (backends.Count > 1)
            {
                StackPanel choices = LinkActionRow(body);
                foreach (string backend in backends)
                {
                    // The one chosen has a filled mark, the others an empty one; the first the machine would use says so in its words
                    bool chosen = backend == llamaBackend;
                    string label = Said("llamacpp.backend_" + backend) + (backend == backends[0] ? " · " + Said("local_ai.recommended_chip") : string.Empty);
                    Button choice = choices.Add(LinkButton(label, chosen ? "pass-filled" : "circle-large-outline", () =>
                    {
                        llamaBackend = backend;
                        RefreshLlamaSetup();
                    }));
                    choice.Selected = chosen;
                    choice.Primary = chosen;
                    choice.Tooltip = Said("llamacpp.backend_" + backend + "_help");
                }
            }
            if (llamaBackend is { } picked && LlamaCppBuilds.Choose(platform, picked, release.Assets) is { } build)
            {
                long size = build.Archive.Size + (build.Runtime?.Size ?? 0);
                // The build's tag, file names and size are the details of the button, not a line of their own
                string details = Said("llamacpp.build_what", ("tag", release.Tag), ("file", build.Archive.Name + (build.Runtime is { } runtime ? " + " + runtime.Name : string.Empty)),
                    ("size", (size / 1e6).ToString("0", CultureInfo.InvariantCulture)));
                StackPanel row = LinkActionRow(body);
                // The build that's in use already is reinstalled, not "instead"
                bool installed = LocalAiSetupFlow.BuildInstalled(found, release.Tag, build.Backend);
                Button get = row.Add(LinkButton(Said(LocalAiSetupFlow.BuildButtonKey(found is not null, installed), ("tag", release.Tag)), "cloud-download", () => DownloadLlamaBuild(release, build)));
                get.Tooltip = details;
                get.Primary = found is null;
                get.Enabled = !going;
            }
        }
        else if (llamaReleaseProblem is { } why)
        {
            body.Add(LinkLine(Said("llamacpp.release_failed", ("why", why))));
            LinkActionRow(body).Add(LinkButton(Said("llamacpp.try_again"), "refresh", AskLlamaMachine));
        }
        else
            body.Add(LinkLine("$(loading~spin) " + Said("llamacpp.release_looking")));

        if (allowPickOwn)
            LinkActionRow(body).Add(LinkButton(Said("llamacpp.build_pick"), "folder-opened", () => Context.Windows.ChooseFolder(chosen =>
            {
                if (LlamaCppDownload.FindServer(chosen) is { } server)
                {
                    settings.LlamaCppServer = server;
                    SaveSettings();
                }
                else
                    Say(Said("llamacpp.build_none_there", ("folder", chosen)), error: true);
                RefreshLlamaSetup();
            })));
        LlamaWorkRow(body, LLAMA_BUILD_WORK);
    }

    // A folder in a box, and a button to pick another, added to a row
    private void LlamaFolderRow(StackPanel row, string folder, Action<string> picked)
    {
        var box = row.Add(new TextBox { Text = LocalAiPathText(folder), Size = new Vector2(320, LINK_ROW), Anchor = Origin.Left, Pivot = Origin.Left });
        box.OnSubmitted = typed =>
        {
            if (typed.Trim().Length > 0 && Path.IsPathFullyQualified(typed.Trim()))
                picked(typed.Trim());
            RefreshLlamaSetup();
        };
        row.Add(LinkButton(Said("llamacpp.browse"), "folder-opened", () => Context.Windows.ChooseFolder(chosen =>
        {
            picked(chosen);
            RefreshLlamaSetup();
        })));
    }

    /* Predictions */

    private void DrawLocalAiPredictions(StackPanel body)
    {
        LocalAiHeading(body, Said("local_ai.advanced_predictions"));
        LocalAiChoicesRow(body, "invocation", ["after_typing", "shortcut"], settings.EditPredictionsInvocation, value => "local_ai.invocation_" + value, value =>
        {
            settings.EditPredictionsInvocation = value;
            LocalAiAdvancedChanged();
        }, reset: () => LocalAiApplyRecommended("invocation"));
        LocalAiChoicesRow(body, "mode", ["eager", "subtle"], settings.EditPredictionsMode, value => "local_ai.mode_" + value, value =>
        {
            settings.EditPredictionsMode = value;
            LocalAiAdvancedChanged();
        }, reset: () => LocalAiApplyRecommended("mode"));
        LocalAiChoicesRow(body, "kind", ["auto", "edit_model", "fim", "instruct", "fim_instruct"], settings.EditPredictionsKind, value => "local_ai.kind_" + value, value =>
        {
            settings.EditPredictionsKind = value;
            LocalAiAdvancedChanged();
        }, reset: () => LocalAiApplyRecommended("kind"), dropdown: true);
        LocalAiTextRow(body, "debounce", settings.EditPredictionsDebounce.ToString(CultureInfo.InvariantCulture), typed =>
        {
            if (int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms))
                settings.EditPredictionsDebounce = Math.Clamp(ms, 0, 5000);
            LocalAiAdvancedChanged();
        }, width: 90, reset: () => LocalAiApplyRecommended("debounce"), unit: Said("local_ai.unit_ms"));
        LocalAiToggleRow(body, "renames", settings.EditPredictionsRenames, on =>
        {
            settings.EditPredictionsRenames = on;
            LocalAiAdvancedChanged();
        }, reset: () => LocalAiApplyRecommended("renames"));
        LocalAiToggleRow(body, "far", settings.EditPredictionsFar, on =>
        {
            settings.EditPredictionsFar = on;
            LocalAiAdvancedChanged();
        }, reset: () => LocalAiApplyRecommended("far"));
        LocalAiTextRow(body, "disabled_languages", string.Join("; ", settings.EditPredictionsDisabledLanguages), typed =>
        {
            settings.EditPredictionsDisabledLanguages = [.. typed.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(id => id.ToLowerInvariant())];
            LocalAiAdvancedChanged();
        }, width: 420, placeholder: Said("local_ai.no_languages"));

        if (!LocalAiSearching)
            LinkActionRow(body).Add(LinkButton(Said("local_ai.reset_page"), "discard", () =>
            {
                foreach (string key in new[] { "invocation", "mode", "kind", "debounce", "renames", "far" })
                    LocalAiApplyRecommended(key, redraw: false);
                LocalAiAdvancedChanged();
            }));
    }

    // A prediction or engine setting put back to the recommended value its help names; nothing when it names none
    private void LocalAiApplyRecommended(string key, bool redraw = true)
    {
        if (LocalAiHelp.RecommendedValue(key, null, settings) is not { } value)
            return;
        bool On() => value.Equals("true", StringComparison.OrdinalIgnoreCase);
        switch (key)
        {
            case "invocation": settings.EditPredictionsInvocation = value; break;
            case "mode": settings.EditPredictionsMode = value; break;
            case "kind": settings.EditPredictionsKind = value; break;
            case "debounce" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms): settings.EditPredictionsDebounce = ms; break;
            case "renames": settings.EditPredictionsRenames = On(); break;
            case "far": settings.EditPredictionsFar = On(); break;
            case "engine_autostart": settings.LlamaCppAutostart = On(); break;
            default: return;
        }
        if (redraw)
            LocalAiAdvancedChanged();
    }

    /* Privacy */

    private void DrawLocalAiPrivacy(StackPanel body)
    {
        LocalAiHeading(body, Said("local_ai.privacy"));
        var globs = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8, Size = new Vector2(220, LINK_ROW) };
        globs.Add(LinkButton(Said("local_ai.privacy_reset"), "discard", () =>
        {
            settings.EditPredictionsExclude = HexSettings.UsualEditPredictionsExclude;
            LocalAiAdvancedChanged();
        }));
        if (LocalAiExplainedRow(body, "exclude_globs", globs) is not null)
        {
            // One pattern a row, each edited in place and taken out with its bin; a last, empty box adds one
            string[] patterns = [.. settings.EditPredictionsExclude];
            float boxWidth = Math.Clamp(LocalAiRowWidth() - 120, 200, 460);
            for (int i = 0; i < patterns.Length; i++)
            {
                int at = i;
                StackPanel line = LinkActionRow(body);
                var pattern = line.Add(new TextBox { Text = patterns[i], Size = new Vector2(boxWidth, LINK_ROW), Anchor = Origin.Left, Pivot = Origin.Left });
                pattern.OnSubmitted = typed =>
                {
                    string[] now = [.. settings.EditPredictionsExclude];
                    if (at < now.Length)
                        settings.EditPredictionsExclude = [.. now.Select((each, index) => index == at ? typed.Trim() : each).Where(each => each.Length > 0)];
                    LocalAiAdvancedChanged();
                };
                Button remove = line.Add(LinkButton(string.Empty, "trash", () =>
                {
                    settings.EditPredictionsExclude = [.. settings.EditPredictionsExclude.Where((each, index) => index != at)];
                    LocalAiAdvancedChanged();
                }));
                remove.Style = "button_flat";
                remove.Size = new Vector2(30, LINK_ROW);
                remove.Tooltip = Said("local_ai.exclude_remove");
            }
            StackPanel adding = LinkActionRow(body);
            var fresh = adding.Add(new TextBox { Placeholder = Said("local_ai.exclude_add"), Size = new Vector2(boxWidth, LINK_ROW), Anchor = Origin.Left, Pivot = Origin.Left });
            fresh.OnSubmitted = typed =>
            {
                if (typed.Trim().Length > 0)
                    settings.EditPredictionsExclude = [.. settings.EditPredictionsExclude, typed.Trim()];
                LocalAiAdvancedChanged();
            };
        }

        LocalAiHeading(body, Said("local_ai.privacy_linked"));
        LocalAiToggleRow(body, "link_chat_answers", settings.EditPredictionsLinkChat, on =>
        {
            settings.EditPredictionsLinkChat = on;
            LocalAiAdvancedChanged();
        });
        LocalAiToggleRow(body, "serve_predictions", settings.EditPredictionsShare, on =>
        {
            settings.EditPredictionsShare = on;
            LocalAiAdvancedChanged();
        });
    }

    private void LocalAiAdvancedChanged(bool redraw = true)
    {
        SaveSettings();
        predictionItemWritten = false;
        if (redraw)
            RefreshLlamaSetup();
    }
}
