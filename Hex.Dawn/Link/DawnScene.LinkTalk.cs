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
    private Button? linkTalkPropertiesTab, linkTalkTab;
    private VirtualStack? linkTalkRows;
    private CodeEditor? linkTalkDraft;
    private Label? linkTalkEmpty, linkTalkPlaceholder;
    private StatusItem? linkTalkStatus;
    private UISkin? linkTalkSkin;
    // Picked on purpose, the properties stay the right side's tab when a device links
    private bool linkTalkOpen, linkTalkExplicit, linkTalkFocus;
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
        BuildLinkTalkTabs();
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
            // The sender's dot in the colour it chose for itself, the one beside the files it's editing
            Text = $"[color:{HexColour(LinkDeviceColour(message.Sender.Id))}]$(circle-filled)[/color] [color:{HexColour(ink)}][b]{LinkTalkLiteral(name)}[/b][/color]  [color:dim]{at}[/color]",
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
        if (!right.Shown)
            ToggleProperties();
        linkTalkOpen = linkTalkExplicit = linkTalkFocus = true;
    }

    // The chat is a tab of the right side beside the properties, its head saying both while a device is linked
    private void BuildLinkTalkTabs()
    {
        Panel head = layout.Get<Panel>("right_head");
        Button Tab(string text, Action pressed)
        {
            float wide = Skin is { } skin ? MathF.Ceiling(skin.Font.MeasureLine(text, 0.13f, Horizon.UI.Drawing.UITextStyle.None, default) + skin.ButtonPadding.Total.X + 12.0f) : 96.0f;
            var tab = head.Add(new Button
            {
                Label = text, Style = "button_flat", Animated = false, Anchor = Origin.Left, Pivot = Origin.Left, LabelScale = 0.13f, Size = new Vector2(wide, 24), Visible = false
            });
            tab.OnPressed = pressed;
            return tab;
        }
        linkTalkPropertiesTab = Tab(Said("properties.title"), () =>
        {
            linkTalkOpen = linkTalkExplicit = false;
            if (linkTalkPanel is { } pane)
                SetLinkTalkVisible(pane, false);
        });
        linkTalkTab = Tab(Said("link.talk.title"), () => linkTalkOpen = linkTalkExplicit = linkTalkFocus = true);
        linkTalkTab.Position = new Vector2(linkTalkPropertiesTab.Size.X + 4, 0);
    }

    private void FitLinkTalkTabs(bool linked, bool chatShown)
    {
        if (linkTalkPropertiesTab is not { } properties || linkTalkTab is not { } chat)
            return;
        layout.Get<Label>("inspector_title").Visible = !linked;
        properties.Visible = chat.Visible = linked;
        properties.Selected = !chatShown;
        chat.Selected = chatShown;
        string label = Said("link.talk.title") + (linkTalkUnread > 0 ? $" ({linkTalkUnread.ToString(CultureInfo.InvariantCulture)})" : string.Empty);
        if (chat.Label != label)
            chat.Label = label;
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

    // The open chat is a tab of the right side, over where the properties are while its tab is picked. It took a column
    // of its own beside the editor, and with the properties out the editor had two sides on the right
    private float FitLinkTalk(float room, float height)
    {
        if (linkTalkPanel is not { } pane)
            return 0;

        // The colours devices picked for themselves, kept with what they last said, this one's from its settings
        foreach (HexLinkPeer peer in LinkPeers)
            LinkPickColour(peer.Device.Id, peer.Colour);
        if (LinkDevice is { } here)
            LinkPickColour(here.Id, settings.LinkColour);

        bool linked = body.Visible && LinkPeers.Count > 0;
        Panel head = layout.Get<Panel>("right_head");
        bool visible = linked && linkTalkOpen && right.Shown && head.IsShownInHierarchy;
        linkTalkDock!.Visible = false;
        SetLinkTalkVisible(pane, visible);
        FitLinkTalkTabs(linked, visible);
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

        if (pane.Parent != chrome.Root)
        {
            pane.Parent?.Remove(pane);
            pane.Anchor = Origin.TopLeft;
            pane.Pivot = Origin.TopLeft;
            chrome.Root.Add(pane);
        }
        var side = right.Bounds;
        float top = head.Bounds.Min.Y - 6, width = MathF.Max(side.Width - 20, LINK_TALK_MIN_WIDTH);
        pane.Position = new Vector2(side.Min.X + 10 - chrome.Root.Bounds.Min.X, top - chrome.Root.Bounds.Max.Y);
        pane.Size = new Vector2(width, MathF.Max(top - side.Min.Y - 10, 120));
        if (linkTalkDraft!.Parent is Panel composer)
            composer.Size = composer.Size with { X = width - 20 };
        linkTalkPlaceholder!.Visible = linkTalkDraft.Text.Length == 0 && compositor.Focus != linkTalkDraft;
        linkTalkSend!.Enabled = !string.IsNullOrWhiteSpace(linkTalkDraft.Text);
        if (linkTalkFocus)
        {
            linkTalkFocus = false;
            linkTalkDraft.Focus();
        }

        return 0;
    }
}
