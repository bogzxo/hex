using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private static string LinkPresetKey(HexLinkPreset preset) => "link.devices_preset_" + preset switch
    {
        HexLinkPreset.Collaborator => "collaborator",
        HexLinkPreset.Worker => "worker",
        HexLinkPreset.Viewer => "viewer",
        _ => "custom"
    };

    private const HexLinkRights LINK_COLLABORATOR = HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit;

    private static HexLinkRights LinkPresetRights(HexLinkPreset preset, bool inviting = true) => preset switch
    {
        HexLinkPreset.Collaborator => LINK_COLLABORATOR,
        HexLinkPreset.Worker => inviting ? HexLinkRights.View | HexLinkRights.Edit : LINK_COLLABORATOR,
        HexLinkPreset.Viewer when inviting => HexLinkRights.View,
        _ => HexLinkRights.None
    };

    private static HexLinkRights LinkPresetRequest(HexLinkPreset preset) =>
        preset is HexLinkPreset.Collaborator or HexLinkPreset.Worker ? LINK_COLLABORATOR : HexLinkRights.None;

    private ToggleButton[] LinkGrantControls(StackPanel column, HexLinkPreset preset, HexLinkRights rights,
        Action<HexLinkPreset, HexLinkRights> changed, bool showBoth = false, HexLinkRights? hostOffer = null, bool allowNone = false,
        string? partnerName = null, HexLinkRights? requested = null)
    {
        StackPanel presets = LinkActionRow(column);
        StackPanel secondary = column.Size.X is > 0 and < 420 ? LinkActionRow(column) : presets;
        StackPanel exact = LinkActionRow(column, 24);
        Label choose = column.Add(LinkLine(Said(allowNone ? "link.devices_grants_zero" : "link.devices_grant_none")));
        Label warning = column.Add(new Label
        {
            Text = $"[color:{HexColour(LinkWarning)}]$(warning) {UIRichText.Literal(Said("link.right_means_steer"))}[/color]",
            TextScale = LINK_SMALL, Wrap = true, Align = Origin.Left, Fill = UIFill.Horizontal
        });
        StackPanel? there = showBoth ? LinkActionRow(column, 24) : null;
        Label description = column.Add(LinkLine(string.Empty));
        StackPanel custom = LinkActionRow(column);
        custom.Spacing = 18;
        StackPanel predict = LinkActionRow(column);
        ToggleButton[] toggles = new ToggleButton[4];
        HexLinkRights[] kinds = [HexLinkRights.View, HexLinkRights.Steer, HexLinkRights.Edit, HexLinkRights.Predict];
        Button[] buttons = new Button[4];
        foreach (HexLinkPreset choice in (ReadOnlySpan<HexLinkPreset>)[HexLinkPreset.Collaborator, HexLinkPreset.Worker, HexLinkPreset.Viewer, HexLinkPreset.Custom])
        {
            string key = LinkPresetKey(choice);
            Button button = LinkButton(Said(key), string.Empty, () =>
            {
                HexLinkRights baseRights = choice is HexLinkPreset.Custom ? rights & ~HexLinkRights.Predict : LinkPresetRights(choice, inviting: hostOffer is null);
                Refresh(choice, baseRights | (rights & HexLinkRights.Predict));
            });
            button.Tooltip = Said(key + "_tip");
            buttons[(int)choice] = (choice is HexLinkPreset.Viewer or HexLinkPreset.Custom ? secondary : presets).Add(button);
        }

        for (int index = 0; index < kinds.Length; index++)
        {
            HexLinkRights kind = kinds[index];
            string name = HexLinkTrust.RightsNames(kind).First();
            bool isPredict = kind == HexLinkRights.Predict;
            ToggleButton toggle = (isPredict ? predict : custom).Add(new ToggleButton(Said("link.right_title_" + name), false)
            {
                Anchor = Origin.Left, Pivot = Origin.Left, LabelScale = LINK_TEXT, Tooltip = Said("link.right_means_" + name)
            });
            toggle.OnPressed = () => Refresh(isPredict ? preset : HexLinkPreset.Custom, LinkRightsToggled(rights, kind, toggle.State));
            toggles[index] = toggle;
        }

        Apply();
        return toggles;

        void Apply()
        {
            for (int index = 0; index < kinds.Length; index++)
                toggles[index].State = (rights & kinds[index]) != 0;
            for (int index = 0; index < buttons.Length; index++)
                buttons[index].Primary = index == (int)preset;
            ClearRows(exact);
            exact.Visible = LinkRightsAnything(rights);
            choose.Visible = !exact.Visible;
            if (exact.Visible)
            {
                exact.Add(new Label { Text = Said("link.devices_grants_here"), TextScale = LINK_SMALL, Dim = true, Anchor = Origin.Left, Pivot = Origin.Left });
                foreach (Badge chip in LinkRightsChips(rights))
                    exact.Add(chip);
            }
            if (there is not null)
            {
                ClearRows(there);
                there.Add(new Label
                {
                    Text = hostOffer is not null ? Said("link.devices_host_offers", ("device", partnerName ?? string.Empty))
                        : partnerName is not null ? Said("link.devices_request_from", ("device", partnerName)) : Said("link.devices_request_there"),
                    TextScale = LINK_SMALL, Dim = true, Anchor = Origin.Left, Pivot = Origin.Left
                });
                HexLinkRights proposed = hostOffer ?? requested ?? LinkPresetRequest(preset);
                if (proposed == HexLinkRights.None)
                    there.Add(new Label { Text = Said("link.devices_request_none"), TextScale = LINK_SMALL, Dim = true, Anchor = Origin.Left, Pivot = Origin.Left });
                else
                    foreach (Badge chip in LinkRightsChips(proposed))
                        there.Add(chip);
            }

            description.Text = UIRichText.Literal(Said(hostOffer is not null && preset == HexLinkPreset.Worker
                ? "link.devices_worker_join_tip" : LinkPresetKey(preset) + "_tip"));
            custom.Visible = preset == HexLinkPreset.Custom;
            warning.Visible = (rights & HexLinkRights.Steer) != 0;
        }

        void Refresh(HexLinkPreset next, HexLinkRights grant)
        {
            (preset, rights) = (next, grant);
            Apply();
            changed(preset, rights);
        }
    }

    private static StackPanel LinkActionRow(StackPanel column, float height = LINK_ROW) =>
        column.Add(new StackPanel { Direction = UIDirection.Horizontal, Fill = UIFill.Horizontal, Spacing = 8, Size = new Vector2(0, height) });
}
