using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// The pieces the agents' windows are made of, the family's conversation and what the harness saved, so both read the
// same. A summary at the top in large type with what it's made of under it, chips to filter by, cards with a stripe in
// the colour of whoever they're about, a bar for a share, and an empty state that says what to do. Before, both were a
// table or a column of text in the window's default type, and read as a dump of data.
internal sealed partial class DawnScene
{
    private const float CHIP_TALL = 24.0f, WINDOW_CARD_ROUND = 8.0f;

    /// <summary>A summary over a window, a large figure and its words beside it, what it's made of in a dim line under.</summary>
    private static StackPanel WindowSummary(string figure, string words, string under, Vector4? tint = null)
    {
        var summary = new StackPanel { Fill = UIFill.Horizontal, Spacing = 2 };
        string coloured = tint is { } colour ? $"[color:{HexColour(colour)}]{UIRichText.Literal(figure)}[/color]" : UIRichText.Literal(figure);
        summary.Add(new Label { Text = $"{coloured}  {UIRichText.Literal(words)}", TextScale = 0.2f, Bold = true, Align = Origin.Left, Fill = UIFill.Horizontal });
        if (under.Length > 0)
            summary.Add(new Label { Text = UIRichText.Literal(under), TextScale = 0.125f, Dim = true, Align = Origin.Left, Fill = UIFill.Horizontal, Wrap = true });
        return summary;
    }

    /// <summary>
    /// A chip to filter by, rounded, filled with the accent when it's the one picked, a dot of a colour in front when it
    /// stands for an agent, how many it lets through after its name.
    /// </summary>
    private Panel Chip(StackPanel row, string label, int? count, bool picked, Action pick, Vector4? dot = null)
    {
        UISkin? skin = Skin;
        Vector4 accent = skin?.Palette.Accent ?? new Vector4(0.85f, 0.42f, 0.3f, 1.0f);
        Vector4 raised = skin?.Palette.Raised ?? new Vector4(1, 1, 1, 0.08f);
        string text = count is { } many ? $"{label}  {many}" : label;
        string written = (dot is { } colour ? $"[color:{HexColour(colour)}]●[/color] " : string.Empty) + UIRichText.Literal(text);
        float width = MathF.Ceiling(text.Length * 5.7f) + (dot is null ? 22 : 32);

        // One not picked has the border's outline, filled with the raised colour alone it was lost on a window's own
        var chip = row.Add(new Panel
        {
            Size = new Vector2(width, CHIP_TALL), Radius = CHIP_TALL / 2,
            Color = picked ? accent with { W = 0.28f } : raised with { W = raised.W * 0.35f },
            Outline = picked ? accent with { W = 0.7f } : skin?.BorderColor ?? new Vector4(1, 1, 1, 0.12f)
        });
        chip.Add(new Button { Style = "button_flat", Fill = UIFill.Both, Animated = false, Selected = false, OnPressed = pick });
        chip.Add(new Label { Text = written, TextScale = 0.12f, Bold = picked, Anchor = Origin.Center, Pivot = Origin.Center });
        return chip;
    }

    /// <summary>A card with a stripe down its left in a colour, what a message or a row of a window sits on.</summary>
    private Panel StripedCard(Vector4 stripe, float pad = 10.0f)
    {
        UISkin? skin = Skin;
        Vector4 field = skin?.Palette.Field ?? new Vector4(0, 0, 0, 0.3f);
        var card = new Panel
        {
            Fill = UIFill.Horizontal, Radius = WINDOW_CARD_ROUND, Padding = new UIEdges(pad + 6, pad - 2, pad, pad),
            Color = field with { W = MathF.Max(field.W, 0.45f) }, Outline = skin?.BorderColor ?? Vector4.Zero
        };
        card.Add(new Panel { Anchor = Origin.Left, Pivot = Origin.Left, Fill = UIFill.Vertical, Size = new Vector2(3, 0), Position = new Vector2(-pad - 3, 0), Radius = 1.5f, Color = stripe });
        return card;
    }

    /// <summary>A bar of a share, its track and how much of it is filled, in a colour.</summary>
    private HexShareBar ShareBar(double share, Vector4 colour, float tall = 6.0f)
    {
        Vector4 raised = Skin?.Palette.Raised ?? new Vector4(1, 1, 1, 0.08f);
        return new HexShareBar { Share = (float)Math.Clamp(share, 0.0, 1.0), Colour = colour, Track = raised, Fill = UIFill.Horizontal, Size = new Vector2(0, tall) };
    }

    /// <summary>A window's list with nothing in it, an icon, what's missing and a button to the way out.</summary>
    private static StackPanel WindowEmpty(string icon, string text, string? action = null, Action? then = null)
    {
        var empty = new StackPanel { Fill = UIFill.Horizontal, Spacing = 8, Padding = new UIEdges(0, 40, 0, 0) };
        empty.Add(new Label { Text = $"$({icon})", TextScale = 0.36f, Dim = true, Anchor = Origin.Center, Pivot = Origin.Center });
        empty.Add(new Label { Text = UIRichText.Literal(text), TextScale = 0.14f, Dim = true, Anchor = Origin.Center, Pivot = Origin.Center });
        if (action is not null && then is not null)
            empty.Add(new Button { Label = action, Style = "button_flat", Size = new Vector2(MathF.Ceiling(action.Length * 6.6f) + 30, 26), LabelScale = 0.125f, Animated = false, Anchor = Origin.Center, Pivot = Origin.Center, OnPressed = then });
        return empty;
    }

    /// <summary>The colour of whoever sent or was sent something, an agent's system's, the person's the accent.</summary>
    private Vector4 WhoTint(int? chat) => chat is { } id && ChatById(id) is { } found
        ? AgentTint(found.Agent, Skin)
        : Skin?.Palette.Accent ?? new Vector4(0.85f, 0.42f, 0.3f, 1.0f);
}
