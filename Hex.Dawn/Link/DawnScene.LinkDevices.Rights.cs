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

    // Rights a preset leaves as they are: autocomplete and shared ports are ticked on their own
    private const HexLinkRights LINK_EXTRAS = HexLinkRights.Predict | HexLinkRights.Ports | HexLinkRights.Ai;

    // Whether the choices behind Change are open in the invite or pairing dialog, shut again for each new dialog
    private bool linkGrantChoicesOpen;

    private const HexLinkRights LINK_COLLABORATOR = HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit;

    // What an invite grants by default, Work together: see and change files. Using the AI agents is a tick of its own.
    private const HexLinkRights LINK_WORK_TOGETHER = HexLinkRights.View | HexLinkRights.Edit;

    private HexLinkRights LinkInviteDefaultRights() =>
        LINK_WORK_TOGETHER | (settings.LinkInviteRights == "use_agents" ? HexLinkRights.Steer : HexLinkRights.None);

    private static HexLinkRights LinkPresetRights(HexLinkPreset preset, bool inviting = true) => preset switch
    {
        HexLinkPreset.Collaborator => LINK_WORK_TOGETHER,
        HexLinkPreset.Worker => inviting ? LINK_WORK_TOGETHER : LINK_COLLABORATOR,
        HexLinkPreset.Viewer when inviting => HexLinkRights.View,
        _ => HexLinkRights.None
    };

    /// <summary>
    /// What the inviting device asks of the joiner in return. Work together asks to view and edit only; using the joiner's AI
    /// agents is asked for only by the Worker preset, or when the invitation's own "Use my AI agents" tick is on.
    /// </summary>
    internal static HexLinkRights LinkPresetRequest(HexLinkPreset preset, HexLinkRights grants = HexLinkRights.None) => preset switch
    {
        HexLinkPreset.Worker => LINK_COLLABORATOR,
        HexLinkPreset.Collaborator => LINK_WORK_TOGETHER | (grants & HexLinkRights.Steer),
        _ => HexLinkRights.None
    };

    private ToggleButton[] LinkGrantControls(StackPanel column, HexLinkPreset preset, HexLinkRights rights,
        Action<HexLinkPreset, HexLinkRights> changed, bool showBoth = false, HexLinkRights? hostOffer = null, bool allowNone = false,
        string? partnerName = null, HexLinkRights? requested = null, bool collapse = false, bool agentsTick = false)
    {
        // In an invite using the AI agents is a tick beside the other extras, kept whichever preset is picked
        HexLinkRights extras = LINK_EXTRAS | (agentsTick ? HexLinkRights.Steer : HexLinkRights.None);
        // One plain sentence of what is allowed; with collapse the choices behind it open with Change
        Label summary = column.Add(new Label { TextScale = LINK_TEXT, Wrap = true, Align = Origin.TopLeft, Fill = UIFill.Horizontal });
        StackPanel detail = column;
        if (collapse)
        {
            // Kept as it was when the dialog is made again around a change of state
            Button change = LinkActionRow(column).Add(LinkButton(Said(linkGrantChoicesOpen ? "link.devices_hide_choices" : "link.devices_change"), string.Empty, () => { }));
            detail = column.Add(new StackPanel { Spacing = 7, Fill = UIFill.Horizontal, Visible = linkGrantChoicesOpen });
            change.OnPressed = () =>
            {
                detail.Visible = linkGrantChoicesOpen = !detail.Visible;
                change.Label = Said(detail.Visible ? "link.devices_hide_choices" : "link.devices_change");
            };
        }

        column = detail;
        StackPanel presets = LinkActionRow(column);
        StackPanel secondary = column.Size.X is > 0 and < 420 ? LinkActionRow(column) : presets;
        Label warning = column.Add(new Label
        {
            Text = $"[color:{HexColour(LinkWarning)}]$(warning) {UIRichText.Literal(Said("link.devices_steer_short"))}[/color]",
            TextScale = LINK_SMALL, Wrap = true, Align = Origin.Left, Fill = UIFill.Horizontal
        });
        Label description = column.Add(LinkLine(string.Empty));
        // Stacked, one under the other: side by side their labels ran into the next switch
        StackPanel custom = column.Add(new StackPanel { Spacing = 4, Fill = UIFill.Horizontal });
        StackPanel predict = column.Add(new StackPanel { Spacing = 4, Fill = UIFill.Horizontal });
        ToggleButton[] toggles = new ToggleButton[6];
        HexLinkRights[] kinds = [HexLinkRights.View, HexLinkRights.Steer, HexLinkRights.Edit, HexLinkRights.Predict, HexLinkRights.Ports, HexLinkRights.Ai];
        Button[] buttons = new Button[4];
        foreach (HexLinkPreset choice in (ReadOnlySpan<HexLinkPreset>)[HexLinkPreset.Collaborator, HexLinkPreset.Worker, HexLinkPreset.Viewer, HexLinkPreset.Custom])
        {
            string key = LinkPresetKey(choice);
            Button button = LinkButton(Said(key), string.Empty, () =>
            {
                HexLinkRights baseRights = choice is HexLinkPreset.Custom ? rights & ~extras : LinkPresetRights(choice, inviting: hostOffer is null);
                Refresh(choice, baseRights | (rights & extras));
            });
            button.Tooltip = Said(key + "_tip");
            buttons[(int)choice] = (choice is HexLinkPreset.Viewer or HexLinkPreset.Custom ? secondary : presets).Add(button);
        }

        for (int index = 0; index < kinds.Length; index++)
        {
            HexLinkRights kind = kinds[index];
            string name = HexLinkTrust.RightsNames(kind).First();
            bool isPredict = kind is HexLinkRights.Predict or HexLinkRights.Ports or HexLinkRights.Ai || agentsTick && kind == HexLinkRights.Steer;
            ToggleButton toggle = (isPredict ? predict : custom).Add(new ToggleButton(Said("link.right_title_" + name), false)
            {
                Anchor = Origin.Left, Pivot = Origin.Left, LabelScale = LINK_TEXT, Tooltip = Said("link.right_means_" + name)
            });
            // Only AI completions ride along with a preset; ports and Serve AI make it a custom set
            toggle.OnPressed = () => Refresh(kind == HexLinkRights.Predict || agentsTick && kind == HexLinkRights.Steer ? preset : HexLinkPreset.Custom, LinkRightsToggled(rights, kind, toggle.State));
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
            string who = partnerName ?? Said("link.plain_other_device");
            string said = LinkRightsAnything(rights)
                ? Said("link.plain_they_can", ("device", who), ("things", LinkPlainRights(rights, yours: true)))
                : Said(allowNone ? "link.devices_grants_zero" : "link.devices_grant_none");
            if (showBoth && LinkRightsAnything(rights))
            {
                HexLinkRights proposed = hostOffer ?? requested ?? LinkPresetRequest(preset, rights);
                if (proposed != HexLinkRights.None)
                    said += " " + Said("link.plain_you_can", ("things", LinkPlainRights(proposed, yours: false)));
            }
            summary.Text = UIRichText.Literal(said);

            description.Text = UIRichText.Literal((preset, hostOffer, partnerName) switch
            {
                (HexLinkPreset.Collaborator, null, _) when agentsTick => Said("link.devices_preset_collaborator_invite_tip"),
                (HexLinkPreset.Worker, not null, _) => Said("link.devices_worker_join_tip"),
                (HexLinkPreset.Viewer, not null, { } partner) => Said("link.devices_viewer_join_tip", ("device", partner)),
                (HexLinkPreset.Viewer, null, { } partner) => Said("link.devices_viewer_host_tip", ("device", partner)),
                _ => Said(LinkPresetKey(preset) + "_tip")
            });
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
