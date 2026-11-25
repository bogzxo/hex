using System.Globalization;
using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The Local AI window's Simple mode (docs/local-ai-window-plan.md, 3): what is happening in a sentence with Start / Stop, the
// setup in use (or the gallery), and the four features with a sentence each, a way to try them and why one is off.
internal sealed partial class DawnScene
{
    private void DrawLocalAiSimple(StackPanel body)
    {
        LocalAiRoleModels roles = LocalAiRoles.Resolve(settings, LocalModelLibrary.Shared);
        bool first = LocalAiSetupFlow.NeedsFirstRun(settings);

        // 1. The status: nothing else while a person is being welcomed, the gallery is the first run
        if (!first)
        {
            body.Add(LinkHeading(Said("local_ai.simple_status")));
            body.Add(new Label { Text = UIRichText.Literal(LocalAiStatusSentence(roles)), TextScale = LINK_TEXT, Wrap = true, Align = Origin.TopLeft, Fill = UIFill.Horizontal });
            int before = body.Children.Count;
            DrawLocalAiStatusActions(body);
            // An action row that got no buttons is left out, so the status isn't followed by a gap
            if (body.Children.Count > before && body.Children[before] is StackPanel { Children.Count: 0 } empty)
                body.Remove(empty);
        }

        // A paired device's model doing the chat work, said with a way to change it
        if (!first && LocalAiLinkedChat() is { } linkedChat)
        {
            StackPanel line = LinkActionRow(body);
            line.Add(new Label { Text = UIRichText.Literal(Said("local_ai.using_linked", ("model", linkedChat.Title), ("device", linkedChat.Device ?? string.Empty))), TextScale = LINK_TEXT, Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left });
            line.Add(LinkButton(Said("local_ai.using_linked_change"), "arrow-swap", () => OpenModelPicker(LocalAiRole.Chat, null, picked => LocalAiAssign(LocalAiRole.Chat, picked))));
        }

        // 2. The setup: its card, or the gallery when there is none or a change was asked for
        if (first || localAiGallery || localAiRun is not null)
        {
            DrawLocalAiSetups(body, detailed: false);
            if (first)
                return;
        }
        else
        {
            body.Add(LinkHeading(Said("local_ai.simple_setup")));
            // With no setup the line has its own "Choose a setup…"; with one, "Change setup…" opens the gallery here
            DrawLocalAiSetupLine(body, heading: false, gallery: true);
            if (LocalAiSetups.Find(settings.LocalAiSetup) is not null)
                LinkActionRow(body).Add(LinkButton(Said("local_ai.setup_change"), "arrow-swap", () =>
                {
                    localAiGallery = true;
                    RefreshLlamaSetup();
                }));
        }

        // 3. The features
        body.Add(LinkHeading(Said("local_ai.simple_features")));
        DrawLocalAiFeatures(body, roles);
        DrawLocalAiTry(body, roles);
    }

    /// <summary>The chat model of a paired device that AI Edit and the agents use now (chosen, or the only one there is when none is here), null when it isn't a linked one.</summary>
    private AiEditModel? LocalAiLinkedChat() =>
        AiEditModels.Resolve(settings, out _) is { Provider: PredictionProviders.LINKAI } linked ? linked : null;

    // The sentence of the status, from where the engine stands
    private string LocalAiStatusSentence(LocalAiRoleModels roles)
    {
        LocalAiStatusKind kind = LocalAiStatus();
        string linkedName = roles.Chat is null && LocalAiLinkedChat() is { } linkedChat ? linkedChat.Title + " (" + linkedChat.Device + ")" : string.Empty;
        string model = roles.Predict?.Name ?? roles.Chat?.Name ?? (linkedName.Length > 0 ? linkedName : Said("local_ai.none"));
        string chat = roles.Chat?.Name ?? (linkedName.Length > 0 ? linkedName : Said("local_ai.none"));
        string where = settings.LocalAiEngine == HexSettings.LOCAL_AI_OLLAMA ? Said("local_ai.where_ollama")
            : llamaPlatform?.Gpu is not null ? Said("local_ai.where_gpu") : Said("local_ai.where_cpu");
        string when = settings.EditPredictions ? Said(settings.EditPredictionsInvocation == "shortcut" ? "local_ai.when_ask" : "local_ai.when_typing") : Said("local_ai.when_off");
        string said = string.Empty;
        foreach (string key in new[] { LLAMA_BUILD_WORK }.Concat(LlamaCppModels.All.Select(each => LLAMA_MODEL_WORK + each.Id)))
        {
            if (Context.Work.Find(key) is not null && llamaWorkSaid.TryGetValue(key, out string? text))
            {
                said = text;
                break;
            }
        }
        return Said(LocalAiWindowState.StatusKey(kind), ("model", model), ("chat", chat), ("where", where), ("when", when), ("why", llamaServer?.Problem ?? string.Empty), ("said", said));
    }

    // Start / Stop with the status, what to do about a problem, or the pause and cancel of a download
    private void DrawLocalAiStatusActions(StackPanel body)
    {
        LocalAiStatusKind kind = LocalAiStatus();
        StackPanel actions = LinkActionRow(body);
        if (kind == LocalAiStatusKind.Downloading)
        {
            llamaServerText = null;
            if (localAiRun is { } run)
            {
                actions.Add(LinkButton(Said("local_ai.run_pause"), "debug-pause", () => StopLocalAiDownloads(run, discard: false)));
                actions.Add(LinkButton(Said("llamacpp.cancel"), "debug-stop", () => StopLocalAiDownloads(run, discard: true)));
            }
            else
                foreach (LlamaCppModel model in LlamaCppModels.All.Where(each => Context.Work.Find(LLAMA_MODEL_WORK + each.Id) is not null))
                    LlamaWorkRow(body, LLAMA_MODEL_WORK + model.Id);
            return;
        }
        if (settings.LocalAiEngine == HexSettings.LOCAL_AI_OLLAMA)
        {
            actions.Add(LinkButton(Said("local_ai.ollama_check"), "refresh", () =>
            {
                localAiOllamaUp = null;
                AskOllama();
                RefreshLlamaSetup();
            }));
            return;
        }
        // A server found on disk but not yet taken into the settings is taken in now, so Start is there
        if (settings.LlamaCppConfig is null && AdoptLlamaServer() is not null)
            _ = settings.LlamaCppConfig;
        if (settings.LlamaCppConfig is null)
            return;
        LlamaCppServer server = LlamaServer;
        LlamaCppServerState state = server.State;
        bool running = state is LlamaCppServerState.Ready or LlamaCppServerState.Starting;
        bool outside = state == LlamaCppServerState.Ready && server.Adopted;
        if (outside)
            DrawLocalAiOutsideNotice(body);
        if (!outside)
        {
            Button start = actions.Add(LinkButton(Said(state == LlamaCppServerState.Failed ? "local_ai.start_again" : running ? "llamacpp.restart" : "llamacpp.start"), running ? "debug-restart" : "play", () =>
            {
                llamaSpeeds = [];
                StopLlamaServer();
                StartLlamaServer();
                RefreshLlamaSetup();
            }));
            start.Primary = !running;
            start.Enabled = state != LlamaCppServerState.Starting;
        }
        if (running && !outside)
            actions.Add(LinkButton(Said("llamacpp.stop"), "debug-stop", () =>
            {
                StopLlamaServer();
                RefreshLlamaSetup();
            }));
        if (state == LlamaCppServerState.Failed)
            actions.Add(LinkButton(Said("local_ai.show_log"), "files", () => GoToLocalAiPage(LocalAiPage.Diagnostics)));
        // Why Hex can't stop it is in the status sentence itself
    }

    // The four features of docs/local-ai-window-plan.md 3, each a row with its sentence, its switch and a way to try it
    private void DrawLocalAiFeatures(StackPanel body, LocalAiRoleModels roles)
    {
        bool canPredict = roles.Predict is not null;
        bool canChat = roles.Chat is not null || LocalAiLinkedChat() is not null;
        string predictName = roles.Predict?.Name ?? Said("local_ai.none");

        // Code predictions: a switch and how they are asked for
        UIComponent predictControl = LocalAiFeatureToggle(settings.EditPredictions, canPredict, on =>
        {
            settings.EditPredictions = on;
            LocalAiFeatureChanged("edit_predictions");
        });
        LocalAiExplainedRow(body, "feature_predictions", predictControl, LINK_ROW, settings.EditPredictions ? "true" : "false");
        if (!canPredict)
            DrawLocalAiWhyOff(body, Said("local_ai.feature_needs_predict"));
        else if (!settings.EditPredictions)
            body.Add(LinkLine(Said("local_ai.predictions_off_hint")));
        else
        {
            StackPanel how = LinkActionRow(body);
            how.Add(new Label { Text = UIRichText.Literal(Said("local_ai.invocation")), TextScale = LINK_SMALL, Dim = true, Size = new Vector2(100, LINK_ROW), Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left });
            foreach (string value in new[] { "after_typing", "shortcut" })
            {
                Button choice = how.Add(LinkButton(Said("local_ai.simple_invocation_" + value), string.Empty, () =>
                {
                    settings.EditPredictionsInvocation = value;
                    LocalAiAdvancedChanged();
                }));
                choice.Selected = settings.EditPredictionsInvocation == value;
            }
        }

        // AI Edit and the Local agent run on the chat model: nothing to switch, a way to try them and why one can't
        DrawLocalAiUseRow(body, "feature_edit", canChat, "local_ai.feature_try", () => OpenAiEdit());
        if (!canChat)
            DrawLocalAiWhyOff(body, Said("local_ai.feature_needs_chat", ("model", predictName)));
        DrawLocalAiUseRow(body, "feature_agent", canChat, "local_ai.feature_try", ShowAgentsPanel);
        if (!canChat)
            DrawLocalAiWhyOff(body, Said("local_ai.feature_needs_chat", ("model", predictName)));

        // Link Chat answers: only with Link on
        if (settings.Link)
        {
            UIComponent linkControl = LocalAiFeatureToggle(settings.EditPredictionsLinkChat, canChat, on =>
            {
                settings.EditPredictionsLinkChat = on;
                LocalAiFeatureChanged("edit_predictions_link_chat");
            });
            LocalAiExplainedRow(body, "feature_linkchat", linkControl, LINK_ROW, settings.EditPredictionsLinkChat ? "true" : "false");
            if (!canChat)
                DrawLocalAiWhyOff(body, Said("local_ai.feature_needs_chat", ("model", predictName)));
        }
    }

    private ToggleButton LocalAiFeatureToggle(bool state, bool enabled, Action<bool> changed)
    {
        var toggle = new ToggleButton(Said(state ? "local_ai.on" : "local_ai.off"), state) { Anchor = Origin.Left, Pivot = Origin.Left, LabelScale = LINK_TEXT, Enabled = enabled };
        toggle.OnPressed = () => changed(toggle.State);
        return toggle;
    }

    // A feature with no switch of its own: whether it can be used with this setup, and the link that tries it
    private void DrawLocalAiUseRow(StackPanel body, string key, bool available, string tryKey, Action tryIt)
    {
        var control = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8, Size = new Vector2(300, LINK_ROW) };
        control.Add(LocalAiBadge(Said(available ? "local_ai.feature_available" : "local_ai.feature_unavailable"), available ? UIBadgeTone.Success : UIBadgeTone.Warning));
        Button tryButton = control.Add(LinkButton(Said(tryKey), "arrow-right", tryIt));
        tryButton.Enabled = available;
        LocalAiExplainedRow(body, key, control, LINK_ROW, available ? "true" : "false");
    }

    // Why a feature is off, and the way to change it
    private void DrawLocalAiWhyOff(StackPanel body, string why)
    {
        StackPanel row = LinkActionRow(body);
        row.Add(new Label { Text = UIRichText.Literal("$(info) " + why), TextScale = LINK_SMALL, Dim = true, Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left });
        Button change = row.Add(LinkButton(Said("local_ai.setup_change"), "arrow-swap", () =>
        {
            localAiGallery = true;
            RefreshLlamaSetup();
        }));
        change.Style = "button_flat";
    }

    // A feature switch written to the settings; the prediction item and the Settings row follow
    private void LocalAiFeatureChanged(string settingKey)
    {
        SaveSettings();
        predictionItemWritten = false;
        SettingsPage?.Refresh(settingKey);
        RefreshLlamaSetup();
    }
}
