using System.Globalization;
using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The Local AI window's pieces and its Overview page (the frame is DawnScene.LocalAiWindow.Frame.cs).
internal sealed partial class DawnScene
{
    /* Pieces */

    // A card for a group of rows, edged in the accent when it's the one chosen
    private StackPanel LocalAiCard(StackPanel body, bool accent = false)
    {
        var card = body.Add(new Panel
        {
            Fill = UIFill.Horizontal, Radius = 8, Padding = new UIEdges(10, 8, 10, 8),
            Color = Skin?.Palette.Raised, Outline = accent ? Skin?.Palette.Accent : Skin?.Palette.Border
        });
        return card.Add(new StackPanel { Spacing = 4, Fill = UIFill.Horizontal, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
    }

    // A notice in Hex's style: an icon and a sentence on a tint of the tone's colour (warning, info or the plain panel)
    private void LocalAiNotice(StackPanel body, string icon, string text, UIBadgeTone tone = UIBadgeTone.Info)
    {
        Vector4? tint = tone switch
        {
            UIBadgeTone.Warning => Skin?.Palette.Warning,
            UIBadgeTone.Danger => Skin?.Palette.Error,
            UIBadgeTone.Success => Skin?.Palette.Success,
            _ => Skin?.Palette.Accent
        };
        var panel = body.Add(new Panel
        {
            Fill = UIFill.Horizontal, Radius = 6, Padding = new UIEdges(10, 6, 10, 6),
            Color = tint is { } colour ? colour with { W = 0.12f } : Skin?.Palette.Field, Outline = tint is { } edge ? edge with { W = 0.45f } : null
        });
        panel.Add(new Label { Text = UIRichText.Literal($"$({icon}) " + text), TextScale = LINK_SMALL, Wrap = true, Align = Origin.TopLeft, Fill = UIFill.Horizontal });
    }

    // "Started outside Hex", said once, with the way out: Hex's own server, set up on the Engine page
    private void DrawLocalAiOutsideNotice(StackPanel body)
    {
        LocalAiNotice(body, "info", Said("local_ai.outside_notice"));
        LinkActionRow(body).Add(LinkButton(Said("local_ai.outside_action"), "arrow-right", () => GoToLocalAiPage(LocalAiPage.Engine)));
    }

    // The width a row of the page has, from the window as it is now (the nav or not, the padding, the scroll bar)
    private float LocalAiRowWidth(float inset = 0) =>
        (localAiWindow?.Size.X ?? 960) - (LocalAiAdvanced ? LOCAL_AI_NAV + 12 : 18) - 12 - 22 - inset;

    // A guess of a chip's or button's width before it's laid out: its text and its padding
    private static float LocalAiTextWidth(string text, float perChar = 7.0f, float padding = 20.0f) => text.Length * perChar + padding;

    // Chips added to a row as many as fit in what's left of it after what's already there (taken), the rest as one "+N"
    private void LocalAiAddBadges(StackPanel row, IReadOnlyList<Badge> chips, float taken, float inset = 20)
    {
        if (chips.Count == 0)
            return;
        float available = LocalAiRowWidth(inset) - taken;
        float[] widths = [.. chips.Select(chip => LocalAiTextWidth(chip.Text, 6.5f, 18))];
        int fit = LocalAiSetupFlow.FitCount(widths, available, row.Spacing, 34);
        for (int i = 0; i < fit; i++)
            row.Add(chips[i]);
        if (fit < chips.Count)
        {
            string hidden = string.Join(", ", chips.Skip(fit).Select(chip => chip.Text));
            row.Add(LocalAiBadge(Said("local_ai.more_badges", ("count", (chips.Count - fit).ToString(CultureInfo.InvariantCulture))), UIBadgeTone.Neutral, Said("local_ai.more_badges_help", ("names", hidden))));
        }
    }

    private static Badge LocalAiBadge(string text, UIBadgeTone tone, string tooltip = "") =>
        new() { Text = text, TextScale = 0.11f, Tone = tone, Anchor = Origin.Left, Pivot = Origin.Left, Tooltip = tooltip };

    // A model's capabilities as chips
    private IEnumerable<Badge> LocalAiCapBadges(LocalModelCaps caps)
    {
        (LocalModelCaps Cap, string Name, UIBadgeTone Tone)[] all =
        [
            (LocalModelCaps.Predict, "predict", UIBadgeTone.Info), (LocalModelCaps.Fim, "fim", UIBadgeTone.Info), (LocalModelCaps.EditModel, "edit", UIBadgeTone.Info),
            (LocalModelCaps.Chat, "chat", UIBadgeTone.Success), (LocalModelCaps.Tools, "tools", UIBadgeTone.Neutral),
            (LocalModelCaps.Thinking, "thinking", UIBadgeTone.Neutral), (LocalModelCaps.Vision, "vision", UIBadgeTone.Neutral)
        ];
        // Which badges, in what order: the one function the picker uses too
        foreach (string key in LocalModelBadges.Keys(caps))
        {
            var (_, name, tone) = all.First(each => each.Name == key);
            yield return LocalAiBadge(Said("local_ai.cap_" + name), tone, Said("local_ai.cap_" + name + "_help"));
        }
    }

    // How a model fits the card, as a chip; nothing when it isn't known
    private Badge? LocalAiFitBadge(LocalModel model)
    {
        // The figures behind the verdict, for the context the model's preset runs with
        string Figures(string help) => model.Bytes > 0
            ? LocalModelLibrary.FitDetail(model, LocalModelPresets.For(model, settings).Context, LocalModelLibrary.VramBytes()) + "\n" + help
            : help;
        return model.Fit switch
        {
            LocalModelFit.Fits => LocalAiBadge(Said("local_ai.fit_fits"), UIBadgeTone.Success, Figures(Said("local_ai.fit_fits_help"))),
            LocalModelFit.Tight => LocalAiBadge(Said("local_ai.fit_tight"), UIBadgeTone.Warning, Figures(Said("local_ai.fit_tight_help"))),
            LocalModelFit.TooBig => LocalAiBadge(Said("local_ai.fit_toobig"), UIBadgeTone.Danger, Figures(Said("local_ai.fit_toobig_help"))),
            _ => null,
        };
    }

    // 9B · Q4_K_XL · 5.6 GB
    private static string LocalAiModelDetail(LocalModel model)
    {
        var parts = new List<string>();
        if (model.SizeLabel is { Length: > 0 } size)
            parts.Add(size);
        if (model.Quant is { Length: > 0 } quant)
            parts.Add(quant);
        if (model.Bytes > 0)
            parts.Add(LocalAiSetupFlow.SizeText(model.Bytes));
        return string.Join(" · ", parts);
    }

    // Assigns a model to a role, saves and draws again
    private void LocalAiAssign(LocalAiRole role, LocalModel model)
    {
        LocalAiRoles.Set(settings, role, model.Id);
        // The server and Ollama follow the Predictions model, so the Overview's engine status is true as soon as it's picked
        if (role == LocalAiRole.Predict)
            ApplyPredictModel(model);
        SaveSettings();
        SettingsPage?.Refresh(role == LocalAiRole.Predict ? "local_ai_predict_model" : "local_ai_chat_model");
        predictionItemWritten = false;
        RefreshLlamaSetup();
    }

    /* Overview */

    private void DrawLocalAiOverview(StackPanel body)
    {
        // Everything here is read from the roles as they are now, whoever assigned them (this window, the status bar's picker, the settings)
        LocalAiRoleModels roles = LocalAiRoles.Resolve(settings, LocalModelLibrary.Shared);
        if (settings.LlamaCppConfig is null)
            AdoptLlamaServer();
        if (settings.LlamaCppConfig is null && roles.Predict is { Path: not null } assigned && settings.LocalAiEngine == HexSettings.LOCAL_AI_LLAMACPP)
        {
            // A model was assigned without the server being told: the server follows the Predictions model
            ApplyPredictModel(assigned);
            SaveSettings();
        }
        LocalAiHeading(body, Said("local_ai.overview_engine"));
        if (!LocalAiSearching)
        {
            body.Add(LinkLine(LocalAiStatusSentence(roles)));
            DrawLocalAiEngineStatus(body);
            DrawLocalAiSetupLine(body);
        }

        LocalAiHeading(body, Said("local_ai.overview_models"));
        DrawLocalAiRoleCard(body, LocalAiRole.Predict, roles.Predict);
        // With no chat model here, the paired device's one in use (picked, or the only one there is) is the effective one
        LocalModel? effectiveChat = roles.Chat ?? (LocalAiLinkedChat() is { } linkedChat ? LocalModelLibrary.Shared.Find(linkedChat.Model) : null);
        DrawLocalAiRoleCard(body, LocalAiRole.Chat, effectiveChat);
        if (!LocalAiSearching)
        {
            if (roles.TwoModels)
                LocalAiNotice(body, "warning", Said("local_ai.two_models"), UIBadgeTone.Warning);
            else if (roles.Predict is not null && roles.Chat is null)
                LocalAiNotice(body, "info", Said("local_ai.no_chat"));

            // What to do next, so the page isn't left empty below the models
            DrawLocalAiTry(body, roles);
        }
    }

    // Three things to try now, when there is a model to try them with
    private void DrawLocalAiTry(StackPanel body, LocalAiRoleModels roles)
    {
        if (roles.Predict is null && roles.Chat is null)
            return;
        body.Add(LinkHeading(Said("local_ai.overview_try")));
        foreach (string key in new[] { "run_try_predict", "run_try_edit", "run_try_agent" })
            body.Add(LinkLine("$(arrow-right) " + Said("local_ai." + key)));
    }

    // The setup in use: its name, or what it was before it became custom, with the way back; the way to pick one when there is none
    private void DrawLocalAiSetupLine(StackPanel body, bool heading = true, bool gallery = false)
    {
        if (heading)
            body.Add(LinkHeading(Said("local_ai.overview_setup")));
        LocalAiSetup? setup = LocalAiSetups.Find(settings.LocalAiSetup);
        if (setup is null)
        {
            body.Add(LinkLine(Said("local_ai.setup_none")));
            // Simple shows the gallery on its own page, Advanced goes to the Setups page
            LinkActionRow(body).Add(LinkButton(Said("local_ai.setup_choose"), "play", () =>
            {
                if (gallery)
                {
                    localAiGallery = true;
                    RefreshLlamaSetup();
                }
                else
                    GoToLocalAiPage(LocalAiPage.Setups);
            })).Primary = true;
            return;
        }
        IReadOnlyList<LocalAiSetupChange> changes = LocalAiSetups.ChangesOf(setup, settings, LocalModelLibrary.Shared);
        string title = Said(setup.TitleKey);
        if (changes.Count == 0)
        {
            body.Add(LinkLine("$(check) " + Said("local_ai.setup_in_use", ("setup", title))));
            return;
        }
        body.Add(LinkLine("$(warning) " + Said("local_ai.setup_custom", ("setup", title))));
        foreach (LocalAiSetupChange change in changes)
            body.Add(LinkLine("  · " + LocalAiChangeText.ReversedLine(change, key => Said(key))));
        LinkActionRow(body).Add(LinkButton(Said("local_ai.setup_return", ("setup", title)), "discard", () => StartLocalAiRun(setup, confirmed: true)));
    }

    // The server's state, what it has loaded, the card's memory, and the buttons that run it
    private void DrawLocalAiEngineStatus(StackPanel body)
    {
        if (settings.LocalAiEngine == HexSettings.LOCAL_AI_OLLAMA)
        {
            body.Add(LinkLine(Said("local_ai.ollama_at", ("url", settings.EditPredictionsApiUrl))));
            body.Add(LinkLine(localAiOllamaUp switch { true => "$(pass-filled) " + Said("local_ai.ollama_up"), false => "$(error) " + Said("local_ai.ollama_down"), _ => "$(loading~spin) " + Said("local_ai.ollama_asking") }));
            LinkActionRow(body).Add(LinkButton(Said("local_ai.ollama_check"), "refresh", AskOllama));
            return;
        }

        LlamaCppServer server = LlamaServer;
        LlamaCppServerState state = server.State;
        if (settings.LlamaCppConfig is null)
        {
            // The same check the Engine step uses: a build that's found is no longer "not set up", what's missing is the model
            body.Add(LinkLine(Said(FoundLlamaServer() is not null ? "llamacpp.set_up_no_model" : "llamacpp.not_set_up")));
            LinkActionRow(body).Add(LinkButton(Said("local_ai.set_up"), "play", OpenLocalAiSetup)).Primary = true;
            return;
        }

        llamaServerText = body.Add(LinkLine(LlamaServerStateText(server)));
        if (state == LlamaCppServerState.Ready && server.Config is { } config)
            localAiLoadedText = body.Add(LinkLine(LocalAiLoadedWords(config.Alias)));
        localAiVramText = body.Add(LinkLine(LocalAiVramWords()));

        StackPanel actions = LinkActionRow(body);
        bool running = state is LlamaCppServerState.Ready or LlamaCppServerState.Starting;
        // A server that was running before Hex asked is left alone: Hex has no process to stop or start again
        bool outside = state == LlamaCppServerState.Ready && server.Adopted;
        if (!outside)
        {
            Button start = actions.Add(LinkButton(Said(running ? "llamacpp.restart" : "llamacpp.start"), running ? "debug-restart" : "play", () =>
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
        if (state == LlamaCppServerState.Ready)
        {
            Button measure = actions.Add(LinkButton(Said(llamaMeasuring ? "llamacpp.measuring" : "llamacpp.measure"), llamaMeasuring ? "loading~spin" : "dashboard", MeasureLlama));
            measure.Enabled = !llamaMeasuring;
        }
        if (llamaMeasuring)
            body.Add(LinkLine("$(loading~spin) " + Said("llamacpp.measuring")));
        if (outside)
            DrawLocalAiOutsideNotice(body);
        Button log = actions.Add(LinkButton(Said(localAiShowLog ? "local_ai.hide_log" : "local_ai.show_log"), "files", () =>
        {
            localAiShowLog = !localAiShowLog;
            RefreshLlamaSetup();
        }));
        log.Style = "button_flat";

        foreach (var (measured, speed) in llamaSpeeds)
            body.Add(LinkLine(Said("local_ai.speed_words", ("model", LocalAiFriendlyName(measured)), ("read", speed.PromptPerSecond.ToString("0", CultureInfo.InvariantCulture)),
                ("wrote", speed.AnswerPerSecond.ToString("0", CultureInfo.InvariantCulture)))));
        if (localAiShowLog)
        {
            var box = body.Add(new Panel { Fill = UIFill.Horizontal, Radius = 6, Padding = new UIEdges(10, 8, 10, 8), Color = Skin is { } look ? TerminalPalette.FromPalette(look.Palette).Background : null });
            llamaLogText = box.Add(new Label { Text = LlamaLogTail(server), CodeFont = code.Font, TextScale = 0.11f, Wrap = true, Fill = UIFill.Horizontal, Align = Origin.TopLeft, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
        }
    }

    // The router's own say of what's loaded, the configured alias until it has said
    private string LocalAiLoadedWords(string alias) => localAiLoaded switch
    {
        { Count: 0 } => Said("local_ai.loaded_none"),
        { } loaded => Said("local_ai.loaded", ("model", string.Join(", ", loaded.Select(LocalAiFriendlyName)))),
        _ => Said("local_ai.loaded", ("model", LocalAiFriendlyName(alias))),
    };

    // A role's card: what it's for, its model with badges, and the button that opens the picker
    private void DrawLocalAiRoleCard(StackPanel body, LocalAiRole role, LocalModel? model)
    {
        string name = role == LocalAiRole.Predict ? "predict" : "chat";
        // The row is the controls column's width and no wider: the name gives way (an ellipsis, the full name in a tooltip), then the chips
        float width = Math.Max(240.0f, LocalAiControlsWidth() - 30.0f);
        var row = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8, Size = new Vector2(width, LINK_ROW) };
        string changeText = Said("local_ai.change");
        float changeWidth = LocalAiTextWidth(changeText, 7.0f, 40);
        float room = width - changeWidth - row.Spacing;
        if (model is null)
            row.Add(new Label { Text = UIRichText.Literal(Said("local_ai.no_model")), TextScale = LINK_TEXT, Dim = true, Anchor = Origin.Left, Pivot = Origin.Left });
        else
        {
            // The name takes at most half of what is left of the row
            int nameChars = Math.Max(8, (int)(room * 0.5f / 7.5f));
            string shownName = LocalAiWindowState.Ellipsize(model.Name, nameChars);
            float nameWidth = LocalAiTextWidth(shownName, 7.5f, 0);
            row.Add(new Label
            {
                Text = $"[b]{UIRichText.Literal(shownName)}[/b]", TextScale = LINK_TEXT, Size = new Vector2(nameWidth, LINK_ROW), Align = Origin.Left,
                Anchor = Origin.Left, Pivot = Origin.Left, Tooltip = shownName == model.Name ? string.Empty : model.Name
            });
            float taken = nameWidth + row.Spacing;
            string detail = LocalAiModelDetail(model);
            float detailWidth = LocalAiTextWidth(detail, 6.0f, 8);
            // The detail only when it fits beside the name with the Change button
            if (detail.Length > 0 && taken + detailWidth + row.Spacing < room)
            {
                row.Add(new Label { Text = UIRichText.Literal(detail), TextScale = LINK_SMALL, Dim = true, Anchor = Origin.Left, Pivot = Origin.Left });
                taken += detailWidth + row.Spacing;
            }
            List<Badge> chips = [.. LocalAiCapBadges(model.Caps)];
            if (LocalAiFitBadge(model) is { } fit)
                chips.Add(fit);
            float[] widths = [.. chips.Select(chip => LocalAiTextWidth(chip.Text, 6.5f, 18))];
            int fits = LocalAiSetupFlow.FitCount(widths, room - taken, row.Spacing, 34);
            for (int i = 0; i < fits; i++)
                row.Add(chips[i]);
            if (fits < chips.Count)
                row.Add(LocalAiBadge(Said("local_ai.more_badges", ("count", (chips.Count - fits).ToString(CultureInfo.InvariantCulture))), UIBadgeTone.Neutral,
                    Said("local_ai.more_badges_help", ("names", string.Join(", ", chips.Skip(fits).Select(chip => chip.Text))))));
        }
        row.Add(LinkButton(changeText, "arrow-swap", () => OpenModelPicker(role, null, picked => LocalAiAssign(role, picked))));
        LocalAiExplainedRow(body, "role_" + name, row);
    }
}
