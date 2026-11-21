using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal enum LinkRightsPreset { Collaborator, Worker, Viewer, Custom }

internal sealed partial class DawnScene
{
    private static string LinkPresetKey(LinkRightsPreset preset) => "link.devices_preset_" + preset switch
    {
        LinkRightsPreset.Collaborator => "collaborator",
        LinkRightsPreset.Worker => "worker",
        LinkRightsPreset.Viewer => "viewer",
        _ => "custom"
    };

    private static HexLinkRights LinkPresetRights(LinkRightsPreset preset) => preset switch
    {
        LinkRightsPreset.Collaborator or LinkRightsPreset.Worker => HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit,
        LinkRightsPreset.Viewer => HexLinkRights.View,
        _ => HexLinkRights.None
    };

    private ToggleButton[] LinkGrantControls(StackPanel column, LinkRightsPreset preset, HexLinkRights rights,
        Action<LinkRightsPreset, HexLinkRights> changed)
    {
        StackPanel presets = LinkActionRow(column);
        StackPanel secondary = column.Size.X is > 0 and < 420 ? LinkActionRow(column) : presets;
        Label exact = column.Add(LinkLine(string.Empty));
        Label description = column.Add(LinkLine(string.Empty));
        StackPanel custom = LinkActionRow(column);
        StackPanel predict = LinkActionRow(column);
        Label warning = column.Add(new Label
        {
            Text = $"[color:{HexColour(LinkWarning)}]$(warning) {UIRichText.Literal(Said("link.right_means_steer"))}[/color]",
            TextScale = LINK_SMALL, Wrap = true, Align = Origin.Left, Fill = UIFill.Horizontal
        });
        ToggleButton[] toggles = new ToggleButton[4];
        HexLinkRights[] kinds = [HexLinkRights.View, HexLinkRights.Steer, HexLinkRights.Edit, HexLinkRights.Predict];
        Button[] buttons = new Button[4];
        foreach (LinkRightsPreset choice in (ReadOnlySpan<LinkRightsPreset>)[LinkRightsPreset.Collaborator, LinkRightsPreset.Worker, LinkRightsPreset.Viewer, LinkRightsPreset.Custom])
        {
            string key = LinkPresetKey(choice);
            Button button = LinkButton(Said(key), string.Empty, () =>
            {
                HexLinkRights baseRights = choice is LinkRightsPreset.Custom ? rights & ~HexLinkRights.Predict : LinkPresetRights(choice);
                Refresh(choice, baseRights | (rights & HexLinkRights.Predict));
            });
            button.Tooltip = Said(key + "_tip");
            buttons[(int)choice] = (choice is LinkRightsPreset.Viewer or LinkRightsPreset.Custom ? secondary : presets).Add(button);
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
            toggle.OnPressed = () => Refresh(isPredict ? preset : LinkRightsPreset.Custom, LinkRightsToggled(rights, kind, toggle.State));
            toggles[index] = toggle;
        }

        Apply();
        return toggles;

        void Apply()
        {
            for (int index = 0; index < kinds.Length; index++)
                toggles[index].State = (rights & kinds[index]) != 0;
            for (int index = 0; index < buttons.Length; index++)
                buttons[index].Selected = index == (int)preset;
            exact.Text = UIRichText.Literal(LinkRightsAnything(rights)
                ? Said("link.devices_grant_summary", ("rights", LinkRightsSaid(rights)))
                : Said("link.devices_grant_none"));
            description.Text = UIRichText.Literal(Said(LinkPresetKey(preset) + "_tip"));
            custom.Visible = preset == LinkRightsPreset.Custom;
            warning.Visible = custom.Visible || (rights & HexLinkRights.Steer) != 0;
        }

        void Refresh(LinkRightsPreset next, HexLinkRights grant)
        {
            (preset, rights) = (next, grant);
            Apply();
            changed(preset, rights);
        }
    }

    private static StackPanel LinkActionRow(StackPanel column, float height = LINK_ROW) =>
        column.Add(new StackPanel { Direction = UIDirection.Horizontal, Fill = UIFill.Horizontal, Spacing = 8, Size = new Vector2(0, height) });
}
