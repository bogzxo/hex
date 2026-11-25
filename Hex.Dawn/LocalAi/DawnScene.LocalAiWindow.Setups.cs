using System.Globalization;
using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The setup gallery and its cards (docs/local-ai-window-plan.md, 4.2) and the "Use this setup" flow (4.3): confirm what changes,
// install the engine, download what's missing, apply everything in one settings write, start the server and time each role.
// The flow goes on from the editor's tick, so it runs on with the window closed.
internal sealed partial class DawnScene
{
    private enum LocalAiRunStage { Confirm, Engine, Downloads, Paused, Apply, Start, Test, Done, Failed }

    private sealed class LocalAiRun(LocalAiSetup setup)
    {
        public LocalAiSetup Setup { get; } = setup;
        public LocalAiRunStage Stage { get; set; }
        public string? Problem { get; set; }
        public bool BuildStarted, MeasureStarted;
        public HashSet<string> Started { get; } = new(StringComparer.Ordinal);
    }

    private LocalAiRun? localAiRun;

    // The card of the run just started, to scroll to when it has a place on the screen
    private UIComponent? localAiScrollTo;
    private bool localAiScrollPending;

    // The run as part of the window's redraw key
    private string LocalAiRunKey() => localAiRun is { } run ? run.Setup.Id + ":" + run.Stage : string.Empty;

    // The name of the linked device whose model the code model or the chat model is, null when both run on this machine
    private string? LocalAiLinkedDevice()
    {
        foreach (string id in new[] { settings.EditPredictionsModel, settings.LocalAiChatModel })
        {
            if (!LinkAiModelId.TryParse(id, out string fingerprint, out _))
                continue;
            HexLinkPeer? peer = LinkPeers.FirstOrDefault(each => string.Equals(each.Device.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
            return peer is null ? Said("local_ai.linked_device") : LinkNameOf(peer.Device.Id, peer.Device.Name);
        }
        return null;
    }

    // The model named by a setup's reference: a catalog title, or the Ollama name
    private static string LocalAiRefName(string reference) =>
        reference.StartsWith("ollama:", StringComparison.OrdinalIgnoreCase) ? reference[7..] : LlamaCppModels.Find(reference)?.Title ?? reference;

    /* The gallery */

    // The gallery of setups, with a welcome on top when nothing is set up (the first run); Advanced adds each card's full detail
    private void DrawLocalAiSetups(StackPanel body, bool detailed)
    {
        LocalModelLibrary library = LocalModelLibrary.Shared;
        bool first = LocalAiSetupFlow.NeedsFirstRun(settings);
        if (first)
        {
            StackPanel welcome = LocalAiCard(body);
            welcome.Add(new Label { Text = $"[b]{UIRichText.Literal(Said("local_ai.welcome_title"))}[/b]", TextScale = LINK_HEADING, Align = Origin.Left, Fill = UIFill.Horizontal });
            welcome.Add(new Label { Text = UIRichText.Literal(LocalAiLinkedDevice() is { } host ? Said("local_ai.welcome_text_linked", ("device", host)) : Said("local_ai.welcome_text")), TextScale = LINK_TEXT, Wrap = true, Align = Origin.TopLeft, Fill = UIFill.Horizontal });
        }
        else
            body.Add(LinkHeading(Said("local_ai.gallery_heading")));

        AskLlamaMachine();
        long vram = LocalModelLibrary.VramBytes();
        body.Add(LinkLine(vram > 0
            ? Said("local_ai.machine_gpu", ("gpu", llamaPlatform?.Gpu?.Name ?? Said("local_ai.unknown")), ("size", (vram / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture)))
            : Said("local_ai.machine_no_gpu")));

        List<LocalModel> ollama = [.. library.Models.Where(model => model.Source == LocalModelSource.Ollama)];
        List<LocalAiSetup> setups = [.. LocalAiSetups.All, .. localAiOllamaUp == true || ollama.Count > 0 ? LocalAiSetups.ForOllama(ollama) : []];
        LocalAiSetup? recommended = LocalAiSetups.Recommend(vram, library, ollama);
        foreach (LocalAiSetup setup in setups)
            DrawLocalAiSetupCard(body, setup, library, vram, recommended?.Id == setup.Id, detailed);
        if (!first && !LocalAiAdvanced)
            LinkActionRow(body).Add(LinkButton(Said("local_ai.gallery_close"), "check", () =>
            {
                localAiGallery = false;
                RefreshLlamaSetup();
            }));
    }

    private void DrawLocalAiSetupCard(StackPanel body, LocalAiSetup setup, LocalModelLibrary library, long vram, bool recommended, bool detailed)
    {
        LocalAiSetupFit fit = LocalAiSetups.FitOf(setup, library, vram);
        bool inUse = settings.LocalAiSetup == setup.Id;
        IReadOnlyList<LocalAiSetupChange> changes = inUse ? LocalAiSetups.ChangesOf(setup, settings, library) : [];
        bool unavailable = fit.HaveGb > 0 && !fit.Fits && !fit.Tight;
        bool diskShort = fit.DownloadBytes > 0 && fit.FreeDiskBytes > 0 && fit.FreeDiskBytes < fit.DownloadBytes;
        LocalAiRun? run = localAiRun is { } mine && mine.Setup.Id == setup.Id ? mine : null;

        StackPanel card = LocalAiCard(body, inUse || recommended || run is not null);
        StackPanel head = card.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8, Fill = UIFill.Horizontal, Size = new Vector2(0, 26) });
        head.Add(new Label { Text = $"[b]{UIRichText.Literal(Said(setup.TitleKey))}[/b]", TextScale = LINK_HEADING, Anchor = Origin.Left, Pivot = Origin.Left });
        if (recommended)
            head.Add(LocalAiBadge(Said("local_ai_setups.recommended_ribbon"), UIBadgeTone.Success));
        if (inUse)
            head.Add(LocalAiBadge(Said(changes.Count == 0 ? "local_ai.setup_badge_in_use" : "local_ai.setup_badge_custom"), changes.Count == 0 ? UIBadgeTone.Info : UIBadgeTone.Warning));
        card.Add(new Label { Text = UIRichText.Literal(Said(setup.PitchKey)), TextScale = LINK_TEXT, Wrap = true, Align = Origin.TopLeft, Fill = UIFill.Horizontal });
        foreach (string key in setup.GetKeys)
            card.Add(LinkLine("$(check) " + Said(key)));

        // What it needs: the card's memory with a mark of how it fits, the download, the disk
        string need = setup.Ollama ? Said("local_ai.needs_ollama") : fit.HaveGb <= 0
            ? "$(info) " + Said("local_ai.needs_vram_unknown", ("need", LocalAiGbText(fit.NeedGb)))
            : fit.Fits && !fit.Tight ? "$(check) " + Said("local_ai.needs_vram_fits", ("have", LocalAiGbText(fit.HaveGb)), ("need", LocalAiGbText(fit.NeedGb)))
            : fit.Tight ? "$(warning) " + Said("local_ai.needs_vram_tight", ("have", LocalAiGbText(fit.HaveGb)), ("need", LocalAiGbText(fit.NeedGb)))
            : "$(error) " + Said("local_ai.needs_vram_short", ("have", LocalAiGbText(fit.HaveGb)), ("need", LocalAiGbText(fit.NeedGb)));
        card.Add(LinkLine(need));
        if (!setup.Ollama)
        {
            card.Add(LinkLine(fit.DownloadBytes == 0
                ? "$(check) " + Said("local_ai.needs_downloaded")
                : "$(cloud-download) " + Said("local_ai.needs_download", ("size", LocalAiSetupFlow.SizeText(fit.DownloadBytes)), ("free", fit.FreeDiskBytes > 0 ? LocalAiSetupFlow.SizeText(fit.FreeDiskBytes) : "?"))));
            if (diskShort)
                card.Add(LinkLine("$(error) " + Said("local_ai.needs_disk_short", ("need", LocalAiSetupFlow.SizeText(fit.DownloadBytes)), ("free", LocalAiSetupFlow.SizeText(fit.FreeDiskBytes)))));
        }

        if (inUse && changes.Count > 0)
        {
            card.Add(LinkLine("$(warning) " + Said("local_ai.setup_custom", ("setup", Said(setup.TitleKey)))));
            foreach (LocalAiSetupChange change in changes)
                card.Add(LinkLine("  · " + LocalAiChangeText.ReversedLine(change, key => Said(key))));
        }

        // The caveat is a tinted note; the models, presets and behaviour are behind Details, in both modes
        LocalAiNotice(card, "info", Said(setup.TradeOffKey));
        bool detail = localAiOpenHelp == "setup:" + setup.Id;
        if (detail)
            DrawLocalAiSetupDetail(card, setup);

        if (run is not null)
        {
            // A run that has just begun brings its card into view, where its progress and Cancel are
            if (localAiScrollPending)
                localAiScrollTo = card;
            DrawLocalAiRun(card, run);
            return;
        }

        StackPanel buttons = LinkActionRow(card);
        // A setup that has to download says how much on its button, so the download is never a surprise
        bool downloads = !setup.Ollama && fit.DownloadBytes > 0;
        bool busyElsewhere = localAiRun is { Stage: not (LocalAiRunStage.Done or LocalAiRunStage.Failed) };
        string useText = busyElsewhere ? Said("local_ai.setup_use") : inUse && changes.Count > 0 ? Said("local_ai.setup_return", ("setup", Said(setup.TitleKey)))
            : downloads ? Said("local_ai.setup_download_use", ("size", LocalAiSetupFlow.SizeText(fit.DownloadBytes))) : Said("local_ai.setup_use");
        Button use = buttons.Add(LinkButton(useText, downloads ? "cloud-download" : "check", () => StartLocalAiRun(setup)));
        use.Primary = true;
        use.Enabled = !unavailable && !diskShort && localAiRun is not { Stage: not (LocalAiRunStage.Done or LocalAiRunStage.Failed) };
        string? why = null;
        if (localAiRun is { Stage: not (LocalAiRunStage.Done or LocalAiRunStage.Failed) })
            why = Said("local_ai.run_busy");
        else if (unavailable)
            why = Said("local_ai.needs_vram_short", ("have", LocalAiGbText(fit.HaveGb)), ("need", LocalAiGbText(fit.NeedGb)));
        if (why is not null)
        {
            use.Tooltip = why;
            if (!use.Enabled)
            {
                // Cancel is on the card that is running; the others only say why they wait
                card.Add(LinkLine("$(info) " + why));
            }
        }
        Button more = buttons.Add(LinkButton(Said(detail ? "local_ai.setup_less" : "local_ai.setup_details"), string.Empty, () =>
        {
            localAiOpenHelp = detail ? null : "setup:" + setup.Id;
            RefreshLlamaSetup();
        }));
        more.Style = "button_flat";
    }

    private static string LocalAiGbText(double gb) => gb.ToString("0.#", CultureInfo.InvariantCulture) + " GB";

    // Every model, profile and setting a setup would set
    private void DrawLocalAiSetupDetail(StackPanel card, LocalAiSetup setup)
    {
        string Profile(LocalAiSetupModel model) => Said("local_ai_profiles." + model.ProfileId + "_title");
        card.Add(LinkLine(Said("local_ai.detail_predict", ("model", LocalAiRefName(setup.Predict.CatalogId)), ("profile", Profile(setup.Predict)))));
        card.Add(LinkLine(setup.Chat.CatalogId == setup.Predict.CatalogId
            ? Said("local_ai.detail_chat_same")
            : Said("local_ai.detail_chat", ("model", LocalAiRefName(setup.Chat.CatalogId)), ("profile", Profile(setup.Chat)))));
        LocalAiBehaviour behaviour = setup.Behaviour;
        // How it behaves, in plain sentences rather than a list of switches
        string how = Said(behaviour.Invocation == "typing" ? "local_ai.behaviour_typing" : "local_ai.behaviour_ask")
            + (behaviour.FollowRenames ? " " + Said("local_ai.behaviour_renames") : string.Empty)
            + (behaviour.EditsElsewhere ? " " + Said("local_ai.behaviour_far") : string.Empty)
            + (behaviour.Autostart ? " " + Said("local_ai.behaviour_autostart") : string.Empty);
        card.Add(LinkLine(how));
    }

    /* The flow */

    /// <summary>Uses a setup: asks first when it replaces what's there, then installs, downloads, applies, starts and times it.</summary>
    private void StartLocalAiRun(LocalAiSetup setup, bool confirmed = false)
    {
        if (localAiRun is { Stage: not (LocalAiRunStage.Done or LocalAiRunStage.Failed) } going && going.Setup.Id != setup.Id)
            return;
        IReadOnlyList<LocalAiSetupChange> changes = LocalAiSetups.ChangesOf(setup, settings, LocalModelLibrary.Shared);
        bool replaces = !LocalAiSetupFlow.NeedsFirstRun(settings) && changes.Count > 0 && !confirmed;
        localAiRun = new LocalAiRun(setup) { Stage = replaces ? LocalAiRunStage.Confirm : LocalAiRunStage.Engine };
        localAiGallery = true;
        localAiScrollPending = true;
        // The run is shown on its card in the gallery: the Setups page in Advanced, the page itself in Simple
        if (LocalAiAdvanced)
            localAiPage = LocalAiPage.Setups;
        RefreshLlamaSetup();
        AdvanceLocalAiRun();
    }

    private void FailLocalAiRun(LocalAiRun run, string why)
    {
        run.Stage = LocalAiRunStage.Failed;
        run.Problem = why;
        RefreshLlamaSetup();
    }

    private long localAiRunAt;

    // Goes on with the run as far as what it waits for allows; asked every tick, so each stage only does something when it can
    private void AdvanceLocalAiRun()
    {
        if (localAiRun is not { } run || run.Stage is LocalAiRunStage.Confirm or LocalAiRunStage.Paused or LocalAiRunStage.Done or LocalAiRunStage.Failed)
            return;
        // The stages look at the disk (the build folder, the models): twice a second is soon enough, a frame is not
        long clock = Environment.TickCount64;
        if (clock - localAiRunAt < 500)
            return;
        localAiRunAt = clock;
        LocalAiSetup setup = run.Setup;
        LocalModelLibrary library = LocalModelLibrary.Shared;
        switch (run.Stage)
        {
            case LocalAiRunStage.Engine:
                if (setup.Ollama)
                {
                    if (settings.LocalAiEngine != HexSettings.LOCAL_AI_OLLAMA)
                        SetLocalAiEngine(HexSettings.LOCAL_AI_OLLAMA);
                    run.Stage = LocalAiRunStage.Downloads;
                    RefreshLlamaSetup();
                    return;
                }
                if (settings.LocalAiEngine != HexSettings.LOCAL_AI_LLAMACPP)
                    SetLocalAiEngine(HexSettings.LOCAL_AI_LLAMACPP);
                bool building = Context.Work.Find(LLAMA_BUILD_WORK) is not null;
                if (!building && FoundLlamaServer() is not null)
                {
                    AdoptLlamaServer();
                    run.Stage = LocalAiRunStage.Downloads;
                    RefreshLlamaSetup();
                }
                else if (!building && !run.BuildStarted)
                {
                    AskLlamaMachine();
                    if (llamaReleaseProblem is { } why)
                        FailLocalAiRun(run, Said("local_ai.run_no_release", ("why", why)));
                    else if (llamaPlatform is { } platform && llamaRelease is { } release && llamaBackend is { } backend && LlamaCppBuilds.Choose(platform, backend, release.Assets) is { } build)
                    {
                        run.BuildStarted = true;
                        DownloadLlamaBuild(release, build);
                    }
                }
                else if (!building && run.BuildStarted)
                    FailLocalAiRun(run, llamaWorkProblem ?? Said("local_ai.run_no_server"));
                break;

            case LocalAiRunStage.Downloads:
                LocalAiSetupFit fit = LocalAiSetups.FitOf(setup, library, LocalModelLibrary.VramBytes());
                bool going = false;
                foreach (string id in fit.MissingCatalogIds)
                {
                    if (LlamaCppModels.Find(id) is not { } model)
                        continue;
                    going |= Context.Work.Find(LLAMA_MODEL_WORK + model.Id) is not null;
                    if (CatalogFileOnDisk(model) is not null || Context.Work.Find(LLAMA_MODEL_WORK + model.Id) is not null)
                        continue;
                    if (llamaWorkProblem is not null && llamaWorkProblemOf == LLAMA_MODEL_WORK + model.Id)
                    {
                        FailLocalAiRun(run, Said("local_ai.run_download_failed", ("model", model.Title), ("why", llamaWorkProblem)));
                        return;
                    }
                    // A download this run started that is gone with no file and no problem was cancelled (from the Work panel's toast): stop, don't start it again
                    if (!run.Started.Add(model.Id))
                    {
                        run.Stage = LocalAiRunStage.Paused;
                        RefreshLlamaSetup();
                        return;
                    }
                    DownloadCatalogModel(model);
                    going = true;
                }
                if (!going && fit.MissingCatalogIds.Count == 0)
                {
                    run.Stage = LocalAiRunStage.Apply;
                    RefreshLlamaSetup();
                }
                break;

            case LocalAiRunStage.Apply:
                LocalAiSetups.Apply(setup, settings, library);
                LocalAiRoleModels roles = LocalAiRoles.Resolve(settings, library);
                if (roles.Predict is { } predict)
                    ApplyPredictModel(predict);
                AdoptLlamaServer();
                settings.EditPredictions = true;
                SaveSettings();
                SettingsPage?.Refresh("local_ai_predict_model");
                SettingsPage?.Refresh("local_ai_chat_model");
                SettingsPage?.Refresh("edit_predictions");
                predictionItemWritten = false;
                llamaTried = null;
                run.Stage = LocalAiRunStage.Start;
                RefreshLlamaSetup();
                break;

            case LocalAiRunStage.Start:
                llamaSpeeds = [];
                if (setup.Ollama)
                {
                    run.Stage = LocalAiRunStage.Done;
                    RefreshLlamaSetup();
                    return;
                }
                AdoptLlamaServer();
                if (settings.LlamaCppConfig is null)
                {
                    FailLocalAiRun(run, Said("local_ai.run_no_server"));
                    return;
                }
                StopLlamaServer();
                StartLlamaServer();
                run.Stage = LocalAiRunStage.Test;
                RefreshLlamaSetup();
                break;

            case LocalAiRunStage.Test:
                LlamaCppServer server = LlamaServer;
                if (server.State == LlamaCppServerState.Failed)
                    FailLocalAiRun(run, Said("local_ai.run_server_failed", ("why", server.Problem ?? string.Empty)));
                else if (server.State == LlamaCppServerState.Ready && !run.MeasureStarted && !llamaMeasuring)
                {
                    run.MeasureStarted = true;
                    MeasureLlama();
                }
                else if (run.MeasureStarted && !llamaMeasuring)
                {
                    run.Stage = LocalAiRunStage.Done;
                    RefreshLlamaSetup();
                }
                break;
        }
    }

    // Stops the downloads of the run: paused keeps the part files to carry on from, cancelled throws them away
    private void StopLocalAiDownloads(LocalAiRun run, bool discard)
    {
        foreach (LlamaCppModel model in LlamaCppModels.All)
        {
            if (Context.Work.Find(LLAMA_MODEL_WORK + model.Id) is { } going)
                going.Cancel();
            if (discard)
            {
                try
                {
                    string part = Path.Combine(settings.LlamaCppModelsInForce, model.File) + ".part";
                    if (File.Exists(part))
                        File.Delete(part);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Say(e.Message, error: true);
                }
            }
        }
        if (discard)
            EndLocalAiRun();
        else
            run.Stage = LocalAiRunStage.Paused;
        llamaWorkProblem = null;
        RefreshLlamaSetup();
    }

    // What the run says on its card: the list of changes to confirm, the progress, the result and what to try
    private void DrawLocalAiRun(StackPanel card, LocalAiRun run)
    {
        switch (run.Stage)
        {
            case LocalAiRunStage.Confirm:
            {
                card.Add(LinkLine(Said("local_ai.run_confirm")));
                foreach (LocalAiSetupChange change in LocalAiSetups.ChangesOf(run.Setup, settings, LocalModelLibrary.Shared))
                    card.Add(LinkLine("  · " + LocalAiChangeText.Line(change, key => Said(key))));
                StackPanel row = LinkActionRow(card);
                row.Add(LinkButton(Said("local_ai.run_use_it"), "check", () =>
                {
                    run.Stage = LocalAiRunStage.Engine;
                    RefreshLlamaSetup();
                    AdvanceLocalAiRun();
                })).Primary = true;
                row.Add(LinkButton(Said("llamacpp.cancel"), "debug-stop", () =>
                {
                    EndLocalAiRun();
                    RefreshLlamaSetup();
                }));
                break;
            }
            case LocalAiRunStage.Engine:
                card.Add(LinkLine("$(loading~spin) " + Said("local_ai.run_engine")));
                LlamaWorkRow(card, LLAMA_BUILD_WORK);
                break;
            case LocalAiRunStage.Downloads:
            case LocalAiRunStage.Paused:
            {
                card.Add(LinkLine((run.Stage == LocalAiRunStage.Paused ? "$(debug-pause) " : "$(loading~spin) ") + Said(run.Stage == LocalAiRunStage.Paused ? "local_ai.run_paused" : "local_ai.run_downloading")));
                foreach (string id in LocalAiSetups.FitOf(run.Setup, LocalModelLibrary.Shared, LocalModelLibrary.VramBytes()).MissingCatalogIds)
                {
                    if (LlamaCppModels.Find(id) is { } model && Context.Work.Find(LLAMA_MODEL_WORK + model.Id) is not null)
                        LlamaWorkRow(card, LLAMA_MODEL_WORK + model.Id);
                }
                StackPanel row = LinkActionRow(card);
                if (run.Stage == LocalAiRunStage.Paused)
                    row.Add(LinkButton(Said("local_ai.run_resume"), "play", () =>
                    {
                        run.Started.Clear();
                        run.Stage = LocalAiRunStage.Downloads;
                        RefreshLlamaSetup();
                        AdvanceLocalAiRun();
                    })).Primary = true;
                else
                    row.Add(LinkButton(Said("local_ai.run_pause"), "debug-pause", () => StopLocalAiDownloads(run, discard: false)));
                row.Add(LinkButton(Said("llamacpp.cancel"), "debug-stop", () => StopLocalAiDownloads(run, discard: true)));
                break;
            }
            case LocalAiRunStage.Apply:
            case LocalAiRunStage.Start:
                card.Add(LinkLine("$(loading~spin) " + Said("local_ai.run_applying")));
                break;
            case LocalAiRunStage.Test:
                llamaServerText = card.Add(LinkLine("$(loading~spin) " + Said("local_ai.run_testing") + " " + LlamaServerStateText(LlamaServer)));
                break;
            case LocalAiRunStage.Done:
                DrawLocalAiRunDone(card, run);
                break;
            case LocalAiRunStage.Failed:
            {
                card.Add(LinkLine("$(error) " + (run.Problem ?? Said("local_ai.run_failed"))));
                StackPanel row = LinkActionRow(card);
                row.Add(LinkButton(Said("local_ai.run_again"), "refresh", () =>
                {
                    llamaWorkProblem = null;
                    StartLocalAiRun(run.Setup, confirmed: true);
                })).Primary = true;
                row.Add(LinkButton(Said("local_ai.show_log"), "files", () =>
                {
                    GoToLocalAiPage(LocalAiPage.Diagnostics);
                }));
                row.Add(LinkButton(Said("local_ai.run_close"), "check", () =>
                {
                    EndLocalAiRun();
                    RefreshLlamaSetup();
                }));
                break;
            }
        }
    }

    private void DrawLocalAiRunDone(StackPanel card, LocalAiRun run)
    {
        card.Add(LinkLine("$(pass-filled) " + Said("local_ai.run_done")));
        foreach (var (model, speed) in llamaSpeeds)
            card.Add(LinkLine("  · " + Said("local_ai.test_speed", ("model", model), ("read", speed.PromptPerSecond.ToString("0", CultureInfo.InvariantCulture)),
                ("wrote", speed.AnswerPerSecond.ToString("0.0", CultureInfo.InvariantCulture)))));
        card.Add(new Label { Text = $"[b]{UIRichText.Literal(Said("local_ai.run_try"))}[/b]", TextScale = LINK_TEXT, Align = Origin.Left, Fill = UIFill.Horizontal });
        card.Add(LinkLine("$(arrow-right) " + Said("local_ai.run_try_predict")));
        card.Add(LinkLine("$(arrow-right) " + Said("local_ai.run_try_edit")));
        card.Add(LinkLine("$(arrow-right) " + Said("local_ai.run_try_agent")));
        LinkActionRow(card).Add(LinkButton(Said("local_ai.run_close"), "check", () =>
        {
            EndLocalAiRun();
            RefreshLlamaSetup();
        })).Primary = true;
    }

    // A run over (done, closed or cancelled): its card goes back to the compact form, in Simple the gallery with it
    private void EndLocalAiRun()
    {
        localAiRun = null;
        localAiGallery = false;
    }
}
