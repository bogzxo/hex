using System.Globalization;
using System.Numerics;

using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// What waits on the person, pinned over the box. A request of the chat on screen to do something, with Allow and Deny,
// or a question it asked, with Answer taking the chat to it, and how many other chats wait too. In the chat it was only
// a row, and a long chat scrolled it out of sight while the agent stood still waiting on it.
internal sealed partial class DawnScene
{
    // How tall the strip is, and the strip itself, made the first time something waits
    private const float WAITING_TALL = 36.0f;
    private Panel? agentsWaitStrip;

    // What the strip was drawn from, the chat, what of it waits and how many others wait
    private (int Chat, HexAgentEntry? Entry, int Others, int Width) agentsWaitStripDrawn = (-1, null, -1, -1);

    // The room the strip takes over the box while it's up
    private float AgentsWaitStripTall => agentsWaitStrip is { Visible: true } ? WAITING_TALL + 6.0f : 0.0f;

    /// <summary>The strip put right for the chat on screen, made again only when what waits changed.</summary>
    private void DrawAgentsWaiting()
    {
        HexAgentChat? chat = agentChat;
        // Plain looks, this runs ten times a second and nothing waits most of the time
        // Another device's chat that may only be watched has nothing here to answer it with (DawnScene.LinkChats)
        HexAgentEntry? waiting = chat is not { HasPending: true } || !LinkMaySteer(chat) ? null
            : HexAgentHub.Approvals(chat).FirstOrDefault() ?? chat.FirstPending(HexAgentEntryKind.Question);
        int others = 0;
        for (int i = 0; i < agentChats.Count; i++)
            if (agentChats[i] != chat && agentChats[i].HasPending)
                others++;
        var now = (chat?.Id ?? -1, waiting, others, (int)agentsView.Size.X);
        if (now == agentsWaitStripDrawn)
            return;
        agentsWaitStripDrawn = now;

        bool was = agentsWaitStrip is { Visible: true };
        if (waiting is null && others == 0)
        {
            if (agentsWaitStrip is not null)
                agentsWaitStrip.Visible = false;
            if (was)
                PlaceAgentsList();
            return;
        }

        UISkin? skin = Skin;
        agentsWaitStrip ??= agentsView.Add(new Panel
        {
            Anchor = Origin.Bottom, Pivot = Origin.Bottom, Fill = UIFill.Horizontal, Size = new Vector2(0, WAITING_TALL), Padding = new UIEdges(10, 4, 6, 4), Radius = 8
        });
        Panel strip = agentsWaitStrip;
        foreach (UIComponent child in strip.Children.ToArray())
            strip.Remove(child);
        Vector4 warning = skin?.Palette.Warning ?? new Vector4(1.0f, 0.78f, 0.3f, 1.0f);
        Vector4 tone = waiting?.Kind == HexAgentEntryKind.Question ? AccentText(skin) : warning;
        strip.Color = tone with { W = 0.14f };
        strip.Outline = tone with { W = 0.45f };
        strip.Visible = true;

        var row = strip.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 6, Fill = UIFill.Both });
        float width = agentsView.Size.X - strip.Padding.Total.X;
        var buttons = new List<Button>();
        if (waiting is not null && chat is not null)
        {
            if (waiting.Kind == HexAgentEntryKind.Approval)
            {
                var (allow, deny) = DecideLabels(waiting);
                bool roomy = width >= 420;
                buttons.Add(new Button { Label = roomy ? allow : Said("agents_panel.allow"), Icon = "check", Primary = true, LabelScale = 0.125f, Animated = false, OnPressed = () => chat.Decide(waiting, true) });
                buttons.Add(new Button { Label = roomy ? deny : Said("agents_panel.deny"), Icon = "close", LabelScale = 0.125f, Animated = false, OnPressed = () => chat.Decide(waiting, false) });
            }
            else
                buttons.Add(new Button { Label = Said("agents_panel.waiting_answer"), Icon = "comment", Primary = true, LabelScale = 0.125f, Animated = false, OnPressed = () => RevealAgentEntry(waiting) });
        }
        if (others > 0)
            buttons.Add(new Button
            {
                Label = Said("agents_panel.waiting_others", ("count", others.ToString(CultureInfo.InvariantCulture))), Icon = "bell", Style = "button_flat", LabelScale = 0.125f, Animated = false,
                OnPressed = () =>
                {
                    if (Face == Hex.Shell.HexFace.Noon)
                        ShowStage(NoonStage.Inbox);
                    else
                        NextNeedingMe();
                }
            });

        float buttonsWide = 0;
        foreach (Button button in buttons)
        {
            float wide = skin is null ? 96.0f : MathF.Ceiling(skin.Font.MeasureLine($"$({button.Icon}) {button.Label}", 0.125f, UITextStyle.None, default) + skin.ButtonPadding.Total.X + 10.0f);
            button.Size = new Vector2(wide, 26);
            button.Anchor = Origin.Left;
            button.Pivot = Origin.Left;
            buttonsWide += wide + row.Spacing;
        }

        // What it waits for, as much of it as fits on one line before the buttons
        if (waiting is not null)
        {
            string icon = waiting.Kind == HexAgentEntryKind.Question ? "question" : "shield";
            string said = Said(waiting.Kind == HexAgentEntryKind.Question ? "agents_panel.waiting_question" : "agents_panel.waiting_approval", ("what", OneLine(waiting.Text, 400)));
            float room = MathF.Max(40, width - buttonsWide - 8);
            string shown = said;
            if (skin is not null)
            {
                while (shown.Length > 4 && skin.Font.MeasureLine($"$({icon}) {shown}", 0.13f, UITextStyle.None, default) > room)
                    shown = said[..(shown.EndsWith('…') ? shown.Length - 2 : shown.Length - 1)] + "…";
            }
            row.Add(new Label
            {
                Text = $"[color:{HexColour(tone)}]$({icon})[/color] {UIRichText.Literal(shown)}", TextScale = 0.13f, Align = Origin.Left,
                Size = new Vector2(room, 26), Anchor = Origin.Left, Pivot = Origin.Left, Tooltip = waiting.Text
            });
        }
        foreach (Button button in buttons)
            row.Add(button);

        if (!was)
            PlaceAgentsList();
    }

    // The way back to the end of a chat scrolled up more than a screen of it, at the right over the box
    private Button? agentsLatest;

    /// <summary>Jump to latest shown while the chat is read more than a screen up from its end, and kept over the box.</summary>
    private void DrawAgentsLatest()
    {
        bool away = agentChat is not null && agentsScroll.MaxOffset - agentsScroll.Offset > MathF.Max(120.0f, agentsScroll.Bounds.Height);
        if (agentsLatest is null)
        {
            if (!away)
                return;
            agentsLatest = agentsView.Add(new Button
            {
                Icon = "arrow-down", Style = "button", Size = new Vector2(32, 32), Anchor = Origin.BottomRight, Pivot = Origin.BottomRight,
                Animated = false, Tooltip = Said("agents_panel.latest"),
                OnPressed = () => agentsScroll.ScrollToEnd()
            });
        }

        agentsLatest.Visible = away;
        agentsLatest.Position = new Vector2(-18, agentsComposeTall + AgentsWaitStripTall + LinkBannerTall + 10.0f);
    }

    /// <summary>The chat on screen scrolled so the row of one of its entries is at the top of what's in sight.</summary>
    private void RevealAgentEntry(HexAgentEntry entry)
    {
        int at = agentsRows.IndexOfThing(entry);
        if (at >= 0)
            agentsScroll.ScrollTo(at);
    }
}
