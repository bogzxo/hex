using System.Diagnostics;
using System.Globalization;
using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The Local AI window's Models page: the library with a details pane and what can be done with a model, and its Presets page.
internal sealed partial class DawnScene
{
    private string? localAiSelected;

    // The preset being edited, for the model it's the draft of
    private string? localAiDraftOf, localAiDraftSource;
    private LocalModelPreset? localAiDraft;

    private LocalModel? LocalAiSelectedModel(LocalModelLibrary library)
    {
        if (localAiSelected is { } id && library.Find(id) is { } found)
            return found;
        LocalAiRoleModels roles = LocalAiRoles.Resolve(settings, library);
        return roles.Predict ?? roles.Chat ?? library.Models.FirstOrDefault();
    }

    private void DrawLocalAiModels(StackPanel body)
    {
        LocalModelLibrary library = LocalModelLibrary.Shared;
        LocalAiRoleModels roles = LocalAiRoles.Resolve(settings, library);
        LocalModel? selected = LocalAiSelectedModel(library);

        body.Add(LinkHeading(Said("local_ai.models_heading")));
        StackPanel bar = LinkActionRow(body);
        bar.Add(LinkButton(Said("local_ai.add_folder"), "folder-opened", PickLocalAiFolder)).Tooltip = Said("local_ai.add_folder_help");
        bar.Add(LinkButton(Said("local_ai.refresh"), "refresh", () => _ = library.Refresh())).Tooltip = Said("local_ai.refresh_help");
        body.Add(LinkLine(Said("local_ai.models_hint")));
        body.Add(LinkLine(Said("local_ai.models_in", ("folders", string.Join("  ·  ", library.Folders.Select(LocalAiPathText))))));
        if (library.Models.Count == 0)
            body.Add(LinkLine(Said("local_ai.models_none")));

        foreach (LocalModel model in library.Models)
        {
            bool assigned = roles.Predict?.Id == model.Id || roles.Chat?.Id == model.Id;
            StackPanel card = LocalAiCard(body, model.Id == selected?.Id);
            StackPanel head = card.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8, Fill = UIFill.Horizontal, Size = new Vector2(0, 26) });
            Button pick = head.Add(LinkButton(model.Name, assigned ? "pass-filled" : "circle-large-outline", () =>
            {
                localAiSelected = model.Id;
                RefreshLlamaSetup();
            }));
            pick.Style = "button_flat";
            string detail = LocalAiModelDetail(model);
            head.Add(new Label { Text = UIRichText.Literal(detail), TextScale = LINK_SMALL, Dim = true, Anchor = Origin.Left, Pivot = Origin.Left });
            var chips = new List<Badge>();
            if (model.Source == LocalModelSource.Catalog)
                chips.Add(LocalAiBadge(Said("local_ai.to_download"), UIBadgeTone.Warning));
            // Two plain abilities at most; FIM, tools, thinking and the compression are in the details below
            if ((model.Caps & (LocalModelCaps.Predict | LocalModelCaps.Fim | LocalModelCaps.EditModel)) != 0)
                chips.Add(LocalAiBadge(Said("local_ai.cap_suggests"), UIBadgeTone.Neutral, Said("local_ai.cap_suggests_help")));
            if ((model.Caps & LocalModelCaps.Chat) != 0)
                chips.Add(LocalAiBadge(Said("local_ai.cap_chats"), UIBadgeTone.Neutral, Said("local_ai.cap_chat_help")));
            if (LocalAiFitBadge(model) is { } fit)
                chips.Add(fit);
            LocalAiAddBadges(head, chips, LocalAiTextWidth(model.Name, 7.5f, 50) + LocalAiTextWidth(detail, 6.0f, 8) + 16);
        }

        if (selected is not null)
            DrawLocalAiModelDetails(body, selected, roles);
    }

    private void DrawLocalAiModelDetails(StackPanel body, LocalModel model, LocalAiRoleModels roles)
    {
        body.Add(LinkHeading(model.Name));
        // The actions come first, so they're in view whatever the details below them run to
        bool unknownCaps = model.Caps == LocalModelCaps.None;
        bool canPredict = unknownCaps || (model.Caps & (LocalModelCaps.Predict | LocalModelCaps.Fim | LocalModelCaps.EditModel)) != 0;
        bool canChat = unknownCaps || (model.Caps & LocalModelCaps.Chat) != 0;
        bool onDisk = model.Source != LocalModelSource.Catalog;

        StackPanel assign = LinkActionRow(body);
        // The tick is the button's own role's, "pass-filled" on a button for a role this model doesn't have would say it does
        bool isPredict = roles.Predict?.Id == model.Id, isChat = roles.Chat?.Id == model.Id;
        Button predict = assign.Add(LinkButton(Said("local_ai.set_predict"), isPredict ? "pass-filled" : "circle-large-outline", () => LocalAiAssign(LocalAiRole.Predict, model)));
        predict.Enabled = canPredict && onDisk;
        predict.Selected = roles.Predict?.Id == model.Id;
        predict.Tooltip = canPredict ? string.Empty : Said("local_ai.cant_predict");
        Button chat = assign.Add(LinkButton(Said("local_ai.set_chat"), isChat ? "pass-filled" : "circle-large-outline", () => LocalAiAssign(LocalAiRole.Chat, model)));
        chat.Enabled = canChat && onDisk;
        chat.Selected = roles.Chat?.Id == model.Id;
        chat.Tooltip = canChat ? string.Empty : Said("local_ai.cant_chat");

        StackPanel other = LinkActionRow(body);
        other.Add(LinkButton(Said("local_ai.edit_preset"), "settings-gear", () =>
        {
            localAiPage = LocalAiPage.Presets;
            localAiDraftOf = null;
            localAiSelected = model.Id;
            RefreshLlamaSetup();
        }));
        if (model.Path is { } file)
        {
            other.Add(LinkButton(Said("local_ai.reveal"), "folder-opened", () => RevealLocalAiFile(file)));
            Button delete = other.Add(LinkButton(Said("local_ai.delete"), "trash", () => ConfirmDeleteLocalAiModel(model)));
            delete.Enabled = model.Source is LocalModelSource.File or LocalModelSource.LmStudio && File.Exists(file);
        }

        // A recommended model not on the disk: its download, as the Work panel's item too
        if (model.Source == LocalModelSource.Catalog && model.CatalogId is { } catalogId && LlamaCppModels.Find(catalogId) is { } catalog)
        {
            string target = Path.Combine(settings.LlamaCppModelsInForce, catalog.File);
            bool going = Context.Work.Find(LLAMA_MODEL_WORK + catalog.Id) is not null;
            StackPanel get = LinkActionRow(body);
            Button download = get.Add(LinkButton(Said(File.Exists(target + ".part") ? "llamacpp.model_resume" : "llamacpp.model_download", ("size", (catalog.Size / 1e9).ToString("0.0", CultureInfo.InvariantCulture))),
                "cloud-download", () => DownloadCatalogModel(catalog)));
            download.Primary = true;
            download.Enabled = !going;
            body.Add(LinkLine(Said("llamacpp.model_where", ("path", LocalAiPathText(target)), ("free", LlamaFree(settings.LlamaCppModelsInForce)))));
            LlamaWorkRow(body, LLAMA_MODEL_WORK + catalog.Id);
        }

        void Fact(string what, string said) => body.Add(LocalAiFact(Said("local_ai.fact_" + what), said));
        Fact("source", model.Source == LocalModelSource.Linked && model.Device is { Length: > 0 } device
            ? Said("local_ai.source_linked_on", ("device", device))
            : Said("local_ai.source_" + model.Source.ToString().ToLowerInvariant()));
        if (model.Path is { } path)
            Fact("path", LocalAiPathText(path));
        // A fact nobody knows is left out rather than shown as "unknown"
        if ((model.Architecture ?? model.Gguf?.Architecture) is { Length: > 0 } architecture)
            Fact("architecture", architecture);
        if (model.ContextMax is { } ctx)
            Fact("context", ctx.ToString("N0", CultureInfo.InvariantCulture));
        if (model.Quant is { Length: > 0 } quant)
            Fact("quant", quant);
        // Everything it can do, which the list's row leaves to this pane
        if (model.Caps != LocalModelCaps.None)
            Fact("abilities", string.Join(", ", LocalAiCapBadges(model.Caps).Select(chip => chip.Text)));
        if (model.Bytes > 0)
            Fact("size", LocalAiSetupFlow.SizeText(model.Bytes));
        Fact("fit", Said("local_ai.fit_" + model.Fit.ToString().ToLowerInvariant()));
        if (model.Gguf?.ChatTemplate is { Length: > 0 } template)
        {
            string head = string.Join("\n", template.Split('\n').Take(4).Select(line => line.Length > 110 ? line[..110] + "…" : line));
            body.Add(new Label { Text = UIRichText.Literal(Said("local_ai.fact_template")), TextScale = LINK_SMALL, Dim = true, Align = Origin.Left, Fill = UIFill.Horizontal });
            var box = body.Add(new Panel { Fill = UIFill.Horizontal, Radius = 6, Padding = new UIEdges(8, 6, 8, 6), Color = Skin?.Palette.Field });
            box.Add(new Label { Text = UIRichText.Literal(head), CodeFont = code.Font, TextScale = 0.11f, Wrap = true, Fill = UIFill.Horizontal, Align = Origin.TopLeft, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
        }
    }

    // What a thing is, then what it says
    private StackPanel LocalAiFact(string what, string said)
    {
        var row = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8, Fill = UIFill.Horizontal, Size = new Vector2(0, 20) };
        row.Add(new Label { Text = UIRichText.Literal(what), TextScale = LINK_SMALL, Dim = true, Size = new Vector2(150, 20), Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left });
        row.Add(new Label { Text = UIRichText.Literal(said), TextScale = LINK_TEXT, Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left });
        return row;
    }

    // The folder dialog starting in the models folder (else the home folder), not where Hex's own files are
    private void PickLocalAiFolder() =>
        Context.Windows.Pick(HexDialogs.PickFolder(LocalAiSetupFlow.ModelsDialogStart(settings.LlamaCppModelsInForce, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), Context.Windows.DialogHost), AddLocalAiFolder);

    // A folder added to the ones scanned (the setting keeps them apart by semicolons)
    private void AddLocalAiFolder(string folder)
    {
        string[] have = settings.LocalAiModelFolders.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!have.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            settings.LocalAiModelFolders = string.Join(";", [.. have, folder]);
            SaveSettings();
        }
        _ = LocalModelLibrary.Shared.Refresh();
        RefreshLlamaSetup();
    }

    private void RevealLocalAiFile(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", $"-R \"{path}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("xdg-open", Path.GetDirectoryName(path) ?? path) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Say(e.Message, error: true);
        }
    }

    // Deleting a model's file is asked first, and the roles that had it are let go
    private void ConfirmDeleteLocalAiModel(LocalModel model)
    {
        if (model.Path is not { } path)
            return;
        UIDialog.Show(chrome, Said("local_ai.delete_title", ("model", model.Name)), Said("local_ai.delete_text", ("path", path), ("size", LocalAiSetupFlow.SizeText(model.Bytes))),
            new DialogChoice(Said("local_ai.delete"), () =>
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Say(e.Message, error: true);
                    return;
                }
                LocalAiRoleModels roles = LocalAiRoles.Resolve(settings, LocalModelLibrary.Shared);
                if (roles.Predict?.Id == model.Id)
                    LocalAiRoles.Set(settings, LocalAiRole.Predict, string.Empty);
                if (roles.Chat?.Id == model.Id)
                    LocalAiRoles.Set(settings, LocalAiRole.Chat, string.Empty);
                SaveSettings();
                localAiSelected = null;
                Say(Said("local_ai.deleted", ("model", model.Name)));
                _ = LocalModelLibrary.Shared.Refresh();
                RefreshLlamaSetup();
            }, Risky: true),
            new DialogChoice(Said("llamacpp.cancel")));
    }

    /* Model presets */

    private void DrawLocalAiPresets(StackPanel body)
    {
        LocalModelLibrary library = LocalModelLibrary.Shared;
        LocalAiHeading(body, Said("local_ai.presets_heading"));
        if (LocalAiSelectedModel(library) is not { } model)
        {
            // Said even while searching, so a match on a preset field is never an empty page
            body.Add(LinkLine(Said("local_ai.models_none")));
            return;
        }

        IReadOnlyList<LocalModelProfile> profiles = LocalModelPresets.Profiles(model);
        LocalModelProfile profile = LocalModelPresets.ProfileOf(model, settings);
        LocalModelPreset defaults = profile.Preset;
        // The draft is made again when the saved presets moved underneath it (edited in the settings, restored), unless a box is being typed in
        if (localAiDraftOf != model.Id || localAiDraft is null || (LocalAiSetupFlow.DraftStale(localAiDraftSource, settings.LocalAiPresets) && !LocalAiEditing()))
        {
            // The saved presets moving after an Apply is the Apply itself: its confirmation stays until something is edited or another model chosen
            if (localAiDraftOf != model.Id)
                localAiApplied = null;
            localAiDraftOf = model.Id;
            localAiDraft = LocalModelPresets.For(model, settings);
        }
        localAiDraftSource = settings.LocalAiPresets;
        LocalModelPreset draft = localAiDraft;

        if (!LocalAiSearching)
        {
            body.Add(LinkLine(Said("local_ai.presets_intro")));
            StackPanel chooser = LinkActionRow(body);
            chooser.Add(new Label { Text = $"[b]{UIRichText.Literal(model.Name)}[/b]", TextScale = LINK_TEXT, Anchor = Origin.Left, Pivot = Origin.Left });
            chooser.Add(LinkButton(Said("local_ai.pick_model"), "arrow-swap", () =>
                ChooseFrom(Said("local_ai.pick_model"), string.Empty, [.. library.Models.Select(each => (each.Name + (LocalAiModelDetail(each) is { Length: > 0 } d ? "  ·  " + d : string.Empty), (Action)(() =>
                {
                    localAiSelected = each.Id;
                    RefreshLlamaSetup();
                })))])));
        }

        // The profile: named values from the model's card, picked and then fine-tuned below
        LocalAiChoicesRow(body, "profile", [.. profiles.Select(each => each.Id)], profile.Id, id => profiles.First(each => each.Id == id).TitleKey, id =>
        {
            LocalModelPreset before = LocalModelPresets.For(model, settings);
            LocalModelPresets.UseProfile(settings, model.Id, id);
            SaveSettings();
            localAiDraft = LocalModelPresets.For(model, settings);
            bool restarted = RestartForPreset(model, before, localAiDraft);
            localAiApplied = (model.Id, LocalAiSetupFlow.AppliedKey(restarted, NeedsServerRestart(model, before, localAiDraft)));
            RefreshLlamaSetup();
        }, model, tipKey: id => profiles.First(each => each.Id == id).DescriptionKey);
        if (!LocalAiSearching)
        {
            if (Uri.TryCreate(profile.Source, UriKind.Absolute, out Uri? address) && address.Scheme is "http" or "https")
            {
                // A web address is a link, opened in the browser
                StackPanel source = LinkActionRow(body);
                source.Add(new Label { Text = UIRichText.Literal(Said("local_ai.profile_source_from")), TextScale = LINK_SMALL, Dim = true, Anchor = Origin.Left, Pivot = Origin.Left });
                Button link = source.Add(LinkButton(profile.Source, "link-external", () =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
                    }
                    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        Say(e.Message, error: true);
                    }
                }));
                link.Style = "button_flat";
                link.Tooltip = address.AbsoluteUri;
            }
            else
                body.Add(LinkLine(Said("local_ai.profile_source", ("source", profile.Source))));
        }

        string profileName = Said(profile.TitleKey);
        Field("context", "context", draft.Context.ToString(CultureInfo.InvariantCulture), defaults.Context.ToString(CultureInfo.InvariantCulture));
        Field("temperature", "temperature", LocalAiNum(draft.Temperature), LocalAiNum(defaults.Temperature));
        Field("top_p", "top_p", LocalAiNum(draft.TopP), LocalAiNum(defaults.TopP));
        Field("top_k", "top_k", draft.TopK.ToString(CultureInfo.InvariantCulture), defaults.TopK.ToString(CultureInfo.InvariantCulture));
        Field("min_p", "min_p", LocalAiNum(draft.MinP), LocalAiNum(defaults.MinP));
        Field("repeat_penalty", "repeat_penalty", LocalAiNum(draft.RepeatPenalty), LocalAiNum(defaults.RepeatPenalty));

        LocalAiChoicesRow(body, "thinking", ["model", "on", "off"], draft.Thinking, value => "local_ai.thinking_" + value, value =>
        {
            localAiDraft = draft with { Thinking = value };
            RefreshLlamaSetup();
        }, model);
        LocalAiChoicesRow(body, "offload", LlamaCppServerArgs.Offloads, draft.Offload, value => "local_ai.offload_" + value, value =>
        {
            localAiDraft = draft with { Offload = value };
            RefreshLlamaSetup();
        }, model);
        Field("extra", "extra_args", draft.Extra, defaults.Extra, wide: true);

        if (LocalAiSearching)
            return;
        StackPanel actions = LinkActionRow(body);
        Button apply = actions.Add(LinkButton(Said("local_ai.apply"), "check", () => ApplyLocalAiPreset(model)));
        apply.Primary = true;
        actions.Add(LinkButton(Said("local_ai.reset_to_profile", ("profile", profileName)), "discard", () =>
        {
            LocalModelPreset before = LocalModelPresets.For(model, settings);
            LocalModelPresets.UseProfile(settings, model.Id, profile.Id);
            SaveSettings();
            localAiDraft = LocalModelPresets.For(model, settings);
            bool restarted = RestartForPreset(model, before, localAiDraft);
            localAiApplied = (model.Id, LocalAiSetupFlow.AppliedKey(restarted, NeedsServerRestart(model, before, localAiDraft)));
            RefreshLlamaSetup();
        }));
        if (localAiApplied is { } done && done.Model == model.Id)
            body.Add(LinkLine("$(pass-filled) " + Said(done.Key)));

        // A labelled box, empty meaning the profile's value shown in grey; a value changed by hand says what it was changed from
        void Field(string field, string helpKey, string current, string fromProfile, bool wide = false)
        {
            bool changed = current != fromProfile;
            var control = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8, Size = new Vector2(wide ? 450 : 260, LINK_ROW) };
            var box = control.Add(new TextBox { Text = changed ? current : string.Empty, Placeholder = fromProfile, Size = new Vector2(wide ? 320 : 110, LINK_ROW), Anchor = Origin.Left, Pivot = Origin.Left });
            // The marker is always there and shown by what is in the box, so typing a value (not only a redraw) says what it was changed from
            var marker = control.Add(new Label { Text = UIRichText.Literal(Said("local_ai.changed_from", ("profile", profileName))), TextScale = LINK_SMALL, Dim = true, Anchor = Origin.Left, Pivot = Origin.Left, Visible = changed });
            box.OnChanged = typed =>
            {
                localAiDraft = LocalAiSetupFlow.WithField(localAiDraft!, defaults, field, typed);
                localAiApplied = null;
                marker.Visible = LocalAiSetupFlow.ChangedFromProfile(typed, fromProfile);
            };
            LocalAiExplainedRow(body, helpKey, control, LINK_ROW, current, model);
        }
    }

    private void ApplyLocalAiPreset(LocalModel model)
    {
        if (localAiDraft is not { } draft)
            return;
        LocalModelPreset before = LocalModelPresets.For(model, settings);
        LocalModelPresets.Save(settings, model.Id, draft);
        SaveSettings();
        bool restarted = RestartForPreset(model, before, draft);
        Say(Said("local_ai.applied", ("model", model.Name)));
        // Said on the page too, under the button: the toast alone is easy to miss
        localAiApplied = (model.Id, LocalAiSetupFlow.AppliedKey(restarted, NeedsServerRestart(model, before, draft)));
        RefreshLlamaSetup();
    }

    private (string Model, string Key)? localAiApplied;

    // The router reads its models preset as it starts, so only a server-side field of a model it holds (a role) costs a restart
    private bool NeedsServerRestart(LocalModel model, LocalModelPreset before, LocalModelPreset after)
    {
        LocalAiRoleModels roles = LocalAiRoles.Resolve(settings, LocalModelLibrary.Shared);
        return LocalAiSetupFlow.RestartsServer(model.Id, roles.Predict?.Id, roles.Chat?.Id, before, after);
    }

    // Only a field the server was started with makes it start again, and only when it's running; whether it did
    private bool RestartForPreset(LocalModel model, LocalModelPreset before, LocalModelPreset after)
    {
        if (!NeedsServerRestart(model, before, after) || llamaServer is not { State: LlamaCppServerState.Ready or LlamaCppServerState.Starting, Adopted: false })
            return false;
        StopLlamaServer();
        StartLlamaServer();
        return true;
    }
}
