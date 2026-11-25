using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The model picker (docs/local-ai-setup-plan.md, 3.1): the library's models in groups under their headings, searchable,
// each with its size and quant, its badges and a dot for whether it fits the card; those that can't do the role are shown
// dim with the reason, the model in use is marked, and each has a star (favourites sort first in their group). It is Hex's
// chooser (the palette's list), hung from the thing that opened it when there is one, Esc going back to what opened it.
internal sealed partial class DawnScene
{
    private static readonly Vector4 FitGreen = new(0.36f, 0.76f, 0.42f, 1.0f), FitAmber = new(0.93f, 0.70f, 0.24f, 1.0f), FitRed = new(0.88f, 0.34f, 0.32f, 1.0f);

    /// <summary>
    /// Opens the model picker for a role: the library's models, one picked calls <paramref name="onPick"/> with it (the
    /// caller assigns it). <paramref name="anchor"/> is where the popup hangs from, in the window's coordinates, null for
    /// the palette's place at the top. <paramref name="back"/> runs when Esc closes it, to go back to what opened it.
    /// </summary>
    internal void OpenModelPicker(LocalAiRole role, UIRect? anchor, Action<LocalModel> onPick, Action? back = null, string typed = "", string chosen = "")
    {
        if (HexAi.Off)
            return;

        LocalModelLibrary library = LocalModelLibrary.Shared;
        LocalAiRoleModels roles = LocalAiRoles.Resolve(settings, library);

        // The role's own model is the one marked and listed as assigned; the other role's is shown in its place, unmarked
        string? current = (role == LocalAiRole.Predict ? roles.Predict : roles.Chat)?.Id;
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (current is not null)
            assigned.Add(current);
        var favourites = new HashSet<string>(LocalAiFavourites.Parse(settings.LocalAiFavourites), StringComparer.OrdinalIgnoreCase);

        List<ModelPickerRow> rows = ModelPicker.Rows(library.Models, role, assigned, favourites, key => Said("local_ai.cap_" + key));
        long vram = LocalModelLibrary.VramBytes();
        var choices = new List<UIChoice>();
        foreach (ModelPickerGroup group in ModelPicker.GroupsShown(rows))
        {
            // Linked models are headed by the device they run on, "On Beta", one heading for each
            IEnumerable<string?> headings = group == ModelPickerGroup.Linked ? rows.Where(each => each.Group == group).Select(each => each.Model.Device).Distinct() : [null];
            foreach (string? device in headings)
            {
                string name = group == ModelPickerGroup.Linked
                    ? Said("local_ai_picker.group_linked", ("device", device ?? "a linked device"))
                    : Said("local_ai_picker.group_" + group.ToString().ToLowerInvariant());
                choices.Add(new UIChoice(name, () => { }) { Header = true, Id = "￾" + name });
                foreach (ModelPickerRow row in rows.Where(each => each.Group == group && (group != ModelPickerGroup.Linked || each.Model.Device == device)))
                    choices.Add(RowChoice(row, role, current, vram, onPick, () => ToggleFavourite(row.Model.Id, () => ReopenModelPicker(role, anchor, onPick, back))));
            }
        }

        // Always at the end, whatever is typed
        choices.Add(new UIChoice(Said("local_ai_picker.manage"), () => OpenLocalAiWindow("models")) { Id = "￿ " + Said("local_ai_picker.manage"), Icon = "settings-gear", Pinned = true });

        string title = Said(role == LocalAiRole.Predict ? "local_ai_picker.title_predict" : "local_ai_picker.title_chat");
        ChooseAt(title, Said("local_ai_picker.message_keys"), choices, anchor, back, typed, chosen);
    }

    // The picker shown again after a star, with what was typed and the row that was highlighted kept
    private void ReopenModelPicker(LocalAiRole role, UIRect? anchor, Action<LocalModel> onPick, Action? back)
    {
        (string typed, string chosen) = UIChooser.Snapshot?.Invoke() ?? (string.Empty, string.Empty);
        OpenModelPicker(role, anchor, onPick, back, typed, chosen);
    }

    // One model's row: its name, size and quant, the badges or why it can't, "current" for the one in use, a fit dot and a star
    private UIChoice RowChoice(ModelPickerRow row, LocalAiRole role, string? current, long vram, Action<LocalModel> onPick, Action star)
    {
        LocalModel model = row.Model;
        bool dim = row.Unsuitable is not null;
        string reason = row.Unsuitable is null ? string.Empty : Said("local_ai_picker.reason_" + row.Unsuitable);
        // The name, then the marker of the model in use (text: the font has no tick), so the rest truncates before them; the
        // star is at the row's own right edge
        var pieces = new List<(string Text, UIChoiceTone Tone)> { (model.Name, dim ? UIChoiceTone.Dim : UIChoiceTone.Primary) };
        bool isCurrent = current is not null && string.Equals(current, model.Id, StringComparison.OrdinalIgnoreCase);
        if (isCurrent)
            pieces.Add((Said("local_ai_picker.current"), UIChoiceTone.Hash));
        if (row.Detail.Length > 0)
            pieces.Add((row.Detail, UIChoiceTone.Dim));
        // The badges are always there, and the reason it can't do this role after them
        if (row.Badges.Length > 0)
            pieces.Add((string.Join(" · ", row.Badges), dim ? UIChoiceTone.Dim : UIChoiceTone.Accent));
        if (dim)
            pieces.Add((reason, UIChoiceTone.Dim));

        // The figures behind the fit dot, under the list while the row is highlighted
        int context = Math.Min(Math.Max(settings.LlamaCppContext, 2048), model.ContextMax ?? int.MaxValue);
        string preview = LocalModelLibrary.FitDetail(model, context, vram);
        bool download = row.Unsuitable == "not_downloaded";
        return new UIChoice(model.Name, () =>
        {
            // Not on the disk: never assigned, the Models page opens on it with its download
            if (download)
            {
                localAiSelected = model.Id;
                localAiDraftOf = null;
                // After the chooser has finished closing, so the window it opens isn't the thing its closing hands the keyboard back from
                PostLink(() => OpenLocalAiWindow("models"));
                return;
            }

            if (reason.Length > 0)
            {
                Say(Said("local_ai_picker.unsuitable", ("model", model.Name), ("why", reason)));
                return;
            }

            onPick(model);
        })
        {
            Demoted = download,
            PickedAlso = reason.Length > 0 ? null : () =>
            {
                localAiSelected = model.Id;
                localAiDraftOf = null;
                OpenLocalAiWindow("presets");
            },
            Id = row.SearchText,
            Segments = pieces,
            // Not on the disk: a download icon in the dim colour, not a fit dot like the installed ones
            Icon = download ? "cloud-download" : "circle-filled",
            IconColor = download ? null : model.Fit switch { LocalModelFit.Fits => FitGreen, LocalModelFit.Tight => FitAmber, LocalModelFit.TooBig => FitRed, _ => null },
            Preview = preview,
            Starred = row.Favourite,
            OnStar = star,
        };
    }

    // A model starred or unstarred, saved, the settings page told, and the picker opened again so the order shows it
    private void ToggleFavourite(string id, Action reopen)
    {
        settings.LocalAiFavourites = LocalAiFavourites.Toggle(settings.LocalAiFavourites, id);
        SaveSettings();
        SettingsPage?.Refresh("local_ai_favourites");
        reopen();
    }

    /// <summary>The next favourite chat model after the current one, set as the chat model: Zed's cycle of favourites.</summary>
    internal void CycleFavouriteChatModel()
    {
        if (HexAi.Off)
            return;

        LocalModelLibrary library = LocalModelLibrary.Shared;
        string? current = LocalAiRoles.Resolve(settings, library).Chat?.Id;
        if (LocalAiFavourites.Next(settings.LocalAiFavourites, library.Models, current) is not { } next)
        {
            Say(Said("local_ai_picker.cycle_none"));
            return;
        }

        if (string.Equals(next.Id, current, StringComparison.OrdinalIgnoreCase))
        {
            Say(Said("local_ai_picker.cycle_one", ("model", next.Name)));
            return;
        }

        LocalAiRoles.Set(settings, LocalAiRole.Chat, next.Id);
        SaveSettings();
        SettingsPage?.Refresh("local_ai_chat_model");
        Say(Said("local_ai_picker.cycle_set", ("model", next.Name)));
    }

    /// <summary>The command that cycles the favourites, in the palette and the Edit menu, its key rebindable.</summary>
    private void AddPickerCommands() =>
        commands.Add(PREDICTION_MENU, Said("local_ai_picker.cycle_command"), CycleFavouriteChatModel, "Ctrl+Alt+M", "star-full", UIKeyUse.BoundWhileTyping);

    /// <summary>Where the Predictions item is in the status bar, null while it isn't shown: what a menu or the picker hangs from.</summary>
    internal UIRect? PredictionItemAnchor() => statusBar.ItemBounds(predictionItem) is { IsEmpty: false } bounds ? bounds : null;

    // The status bar's menu and the settings rows set a role by the picker; the setting is saved and the page told
    internal void PickModelFor(LocalAiRole role, UIRect? anchor = null, Action? back = null)
    {
        OpenModelPicker(role, anchor, model =>
        {
            LocalAiRoles.Set(settings, role, model.Id);
            SaveSettings();
            SettingsPage?.Refresh(role == LocalAiRole.Predict ? "local_ai_predict_model" : "local_ai_chat_model");
            Say(Said("local_ai_picker.set_" + (role == LocalAiRole.Predict ? "predict" : "chat"), ("model", model.Name)));
        }, back);
    }

    // The role's model's name for a chip or a menu row, or "none"
    private string RoleModelName(LocalAiRole role)
    {
        LocalAiRoleModels roles = LocalAiRoles.Resolve(settings, LocalModelLibrary.Shared);
        return (role == LocalAiRole.Predict ? roles.Predict : roles.Chat)?.Name ?? Said("local_ai_picker.none");
    }
}
