using System.Globalization;
using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private const float LINK_TALK_WIDTH = 260, LINK_TALK_MIN_WIDTH = 200, LINK_TALK_EDITOR_ROOM = 320, LINK_TALK_GAP = 8;

    private readonly List<HexLinkTalk> linkTalkMessages = [];
    private Panel? linkTalkDock, linkTalkPanel;
    private Button? linkTalkSend;
    private VirtualStack? linkTalkRows;
    private CodeEditor? linkTalkDraft;
    private Label? linkTalkEmpty, linkTalkPlaceholder;
    private StatusItem? linkTalkStatus;
    private UISkin? linkTalkSkin;
    private bool linkTalkOpen = true, linkTalkExplicit, linkTalkFocus;
    private int linkTalkUnread;

    private void BuildLinkTalk()
    {
        var dock = linkTalkDock = body.Insert(IndexIn(body, center) + 1, new Panel
        {
            Visible = false, Anchor = Origin.Top, Pivot = Origin.Top, Padding = new UIEdges(LINK_TALK_GAP, 0, 0, 0)
        });
        UILayout made = layout.Instantiate("shell/link_talk.hor", dock);
        Panel pane = linkTalkPanel = made.Get<Panel>("link_talk");
        pane.Visible = false;
        linkTalkRows = pane.Insert(0, new VirtualStack
        {
            Fill = UIFill.Both, Padding = new UIEdges(12, 42, 5, 103),
            Spacing = 12, Estimate = 46, StickToEnd = true, Make = index => DrawLinkTalk(linkTalkMessages[index])
        });
        Button close = made.Get<Button>("link_talk_close");
        Button send = linkTalkSend = made.Get<Button>("link_talk_send");
        close.OnPressed = HideLinkTalk;
        send.OnPressed = SendLinkTalk;
        linkTalkEmpty = made.Get<Label>("link_talk_empty");
        linkTalkPlaceholder = made.Get<Label>("link_talk_placeholder");

        Panel field = made.Get<Panel>("link_talk_compose");
        linkTalkDraft = field.Insert(0, new CodeEditor
        {
            Fill = UIFill.Both, WordWrap = true, TextScale = 0.14f, Padding = new UIEdges(6, 4),
            LineNumbers = false, IndentGuides = false, StickyScroll = false, CaretBand = false,
            CheckSyntax = false, Minimap = false, Color = Vector4.Zero,
            Tooltip = Said("link.talk.compose_tip"), CompletionSource = _ => null
        });
        linkTalkDraft.OnEnter = () =>
        {
            if (Engine.Input.Keyboard.Shift)
                return false;
            SendLinkTalk();
            return true;
        };
        linkTalkDraft.OnTab = () =>
        {
            (send.Enabled ? send : close).Focus();
            return true;
        };
        send.TabTo = by => by > 0 ? close : linkTalkDraft;
        close.TabTo = by => by > 0 && code.IsShownInHierarchy ? code : linkTalkDraft;

        linkTalkStatus = statusBar.Add("$(comment-discussion)", right: true, pressed: ToggleLinkTalk, tooltip: Said("link.talk.open"));
        linkTalkStatus.IsVisible = () => LinkPeers.Count > 0 && !pane.IsShownInHierarchy;
    }

    private UIComponent DrawLinkTalk(HexLinkTalk message)
    {
        var row = new StackPanel { Spacing = 2 };
        string name = LinkNameOf(message.Sender.Id, message.Sender.Name);
        if (message.Sender.Id == LinkDevice?.Id)
            name = Said("link.talk.you", ("device", name));
        Vector4 ink = LinkChatReadableInk(LinkDeviceColour(message.Sender.Id));
        string at = message.SentAt.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        row.Add(new Label
        {
            Text = $"[color:{HexColour(ink)}][b]{LinkTalkLiteral(name)}[/b][/color]  [color:dim]{at}[/color]",
            Align = Origin.TopLeft, Wrap = true, Fill = UIFill.Horizontal, TextScale = 0.125f
        });
        row.Add(new Label
        {
            Text = LinkTalkLiteral(message.Text), Align = Origin.TopLeft, Wrap = true, Fill = UIFill.Horizontal, TextScale = 0.145f
        });
        return row;
    }

    // The font also reads $(...) as an icon, even in a label that doesn't read Markdown.
    private static string LinkTalkLiteral(string text) => UIRichText.Literal(text).Replace("$(", "$\u200b(", StringComparison.Ordinal);

    private void HearLinkTalk(HexLinkTalk message)
    {
        linkTalkMessages.Add(message);
        if (linkTalkRows is not { } rows)
            return;
        linkTalkEmpty!.Visible = false;
        rows.ItemCount = linkTalkMessages.Count;
        rows.ItemsChanged();
        rows.ScrollToEnd();
        if (!linkTalkPanel!.IsShownInHierarchy && message.Sender.Id != LinkDevice?.Id)
            linkTalkUnread++;
        UpdateLinkTalkStatus();
    }

    private void SendLinkTalk()
    {
        if (linkTalkDraft is not { } draft || link is not { } current)
            return;
        string text = draft.Text.Trim();
        if (text.Length == 0)
            return;

        switch (current.SayTalk(text))
        {
            case HexLinkTalkResult.Sent:
                draft.Text = string.Empty;
                draft.Focus();
                break;
            case HexLinkTalkResult.Invalid:
                Say(Said("link.talk.invalid", ("count", HexLinkTalk.MOST_TEXT.ToString(CultureInfo.InvariantCulture))), error: true);
                break;
            case HexLinkTalkResult.TooFast:
                Say(Said("link.talk.slow"), error: true);
                break;
            case HexLinkTalkResult.NoView:
                Say(Said("link.talk.no_view"), error: true);
                break;
            case HexLinkTalkResult.Full:
                Say(Said("link.talk.full"), error: true);
                break;
        }
    }

    private void ResetLinkTalk()
    {
        if (linkTalkPanel is null)
            return;
        HideLinkTalk();
        linkTalkOpen = true;
        linkTalkMessages.Clear();
        linkTalkUnread = 0;
        linkTalkEmpty!.Visible = true;
        linkTalkDraft!.Text = string.Empty;
        linkTalkRows!.ItemCount = 0;
        linkTalkRows.Reset();
        UpdateLinkTalkStatus();
    }

    private void AddLinkTalkCommand(List<(string Text, Action Picked)> commands)
    {
        if (LinkPeers.Count > 0)
            commands.Add((Said("link.talk.command"), ToggleLinkTalk));
    }

    private void ToggleLinkTalk()
    {
        if (linkTalkPanel?.IsShownInHierarchy == true)
        {
            HideLinkTalk();
            return;
        }

        if (!body.Visible)
            SwitchFace(HexFace.Dawn);
        linkTalkOpen = linkTalkExplicit = linkTalkFocus = true;
    }

    private void HideLinkTalk()
    {
        linkTalkOpen = linkTalkExplicit = linkTalkFocus = false;
        if (linkTalkPanel is { } pane)
        {
            SetLinkTalkVisible(pane, false);
            linkTalkDock!.Visible = false;
        }
    }

    private void SetLinkTalkVisible(Panel pane, bool visible)
    {
        if (visible == pane.Visible)
            return;
        if (visible)
        {
            linkTalkUnread = 0;
            linkTalkRows!.ScrollToEnd();
            UpdateLinkTalkStatus();
        }
        else
        {
            for (UIComponent? focused = compositor.Focus; focused is not null; focused = focused.Parent)
            {
                if (focused != pane)
                    continue;
                compositor.Focus = null;
                if (code.IsShownInHierarchy)
                    code.Focus();
                break;
            }
        }
        pane.Visible = visible;
    }

    private void UpdateLinkTalkStatus()
    {
        if (linkTalkStatus is not { } status)
            return;
        status.Text = "$(comment-discussion)";
        if (linkTalkUnread > 0)
            status.Text += " " + Said("link.talk.unread", ("count", linkTalkUnread.ToString(CultureInfo.InvariantCulture)));
    }

    // The open chat takes a column while the editor has room, a chat opened on purpose in a narrow window floats over its right edge.
    private float FitLinkTalk(float room, float height)
    {
        if (linkTalkPanel is not { } pane)
            return 0;

        bool dock = room >= LINK_TALK_EDITOR_ROOM + LINK_TALK_MIN_WIDTH + LINK_TALK_GAP;
        bool visible = body.Visible && linkTalkOpen && LinkPeers.Count > 0 && (dock || linkTalkExplicit);
        linkTalkDock!.Visible = visible && dock;
        SetLinkTalkVisible(pane, visible);
        if (!visible)
            return 0;

        if (Skin is { } skin && linkTalkSkin != skin)
        {
            linkTalkSkin = skin;
            pane.Color = skin.Palette.Panel with { W = 1 };
            pane.Outline = skin.BorderColor;
            if (linkTalkDraft!.Parent is Panel field)
            {
                field.Color = skin.FieldColor with { W = 1 };
                field.Outline = skin.BorderColor;
            }
            linkTalkRows!.Rebind();
        }

        float width = dock ? MathF.Min(LINK_TALK_WIDTH, room - LINK_TALK_EDITOR_ROOM - LINK_TALK_GAP) : MathF.Min(LINK_TALK_WIDTH, room);
        if (dock)
        {
            if (pane.Parent != linkTalkDock)
            {
                pane.Parent?.Remove(pane);
                pane.Anchor = Origin.Center;
                pane.Pivot = Origin.Center;
                pane.Position = Vector2.Zero;
                linkTalkDock.Add(pane);
            }
            linkTalkDock.Size = new Vector2(width + LINK_TALK_GAP, height);
        }
        else
        {
            if (pane.Parent != chrome.Root)
            {
                pane.Parent?.Remove(pane);
                pane.Anchor = Origin.TopLeft;
                pane.Pivot = Origin.TopLeft;
                chrome.Root.Add(pane);
            }
            pane.Position = new Vector2(center.Bounds.Max.X - chrome.Root.Bounds.Min.X - width, body.Bounds.Max.Y - chrome.Root.Bounds.Max.Y);
        }

        pane.Size = new Vector2(width, height);
        if (linkTalkDraft!.Parent is Panel composer)
            composer.Size = composer.Size with { X = width - 20 };
        linkTalkPlaceholder!.Visible = linkTalkDraft.Text.Length == 0 && compositor.Focus != linkTalkDraft;
        linkTalkSend!.Enabled = !string.IsNullOrWhiteSpace(linkTalkDraft.Text);
        if (linkTalkFocus)
        {
            linkTalkFocus = false;
            linkTalkDraft.Focus();
        }

        return dock ? width + LINK_TALK_GAP : 0;
    }
}
