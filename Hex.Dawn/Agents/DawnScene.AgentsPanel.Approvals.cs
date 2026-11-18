using System.Numerics;

using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// An agent asking leave to do something (Claude Code's can_use_tool, before a command or a change Hex's guard has no
// word on), a card in its chat with what it wants to do and Allow and Deny. The agent waits on it, so it stands out in
// the warning colour and counts as an agent that needs looking at. Answered, it says what was decided and keeps its
// place in the chat.
internal sealed partial class DawnScene
{
    private UIComponent ApprovalRow(HexAgentChat chat, HexAgentEntry entry, UISkin? skin)
    {
        Vector4 warning = skin?.Palette.Warning ?? new Vector4(1.0f, 0.78f, 0.3f, 1.0f);
        bool waiting = entry.Answer is not null;
        var card = new Panel
        {
            Fill = UIFill.Horizontal, Radius = 8, Padding = new UIEdges(12, 8, 12, 8),
            Color = warning with { W = waiting ? 0.12f : 0.05f }
        };
        var stack = card.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 4 });
        stack.Add(Text($"[color:{HexColour(warning)}]$(shield)[/color] {UIRichText.Literal(entry.Text)}", 0.14f));
        if (!string.IsNullOrWhiteSpace(entry.Input))
            stack.Add(Text(UIRichText.Literal(Lines(entry.Input!, 8)), 0.13f, dim: true).Also(label => label.CodeFont = code.Font));

        if (waiting)
        {
            var buttons = stack.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 6, Size = new Vector2(0, 26) });
            // Their keys said on them, answered from anywhere in Hex without the mouse (DecideWaiting)
            buttons.Add(new Button { Label = Said("noon.allow_key", ("key", ALLOW_KEY)), Icon = "check", Primary = true, Size = new Vector2(150, 24), LabelScale = 0.125f, Animated = false, OnPressed = () => chat.Decide(entry, true) });
            buttons.Add(new Button { Label = Said("noon.deny_key", ("key", DENY_KEY)), Icon = "close", Danger = true, Size = new Vector2(140, 24), LabelScale = 0.125f, Animated = false, OnPressed = () => chat.Decide(entry, false) });
        }
        else
            stack.Add(Text(entry.State == HexAgentToolState.Done ? $"$(check) {Said("agents_panel.allowed")}" : $"$(close) {Said("agents_panel.denied")}", 0.125f, dim: true));

        return card;
    }
}
