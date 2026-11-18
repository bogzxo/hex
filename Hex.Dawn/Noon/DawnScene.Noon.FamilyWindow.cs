using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The whole of a family's conversation in a window of its own, every message of its log (up to the 2000 it keeps) where
// Details has the latest dozen. A summary over it says how many messages between how many agents and of what kinds,
// chips under it filter by kind and by member (what a member sent or was sent), each with how many it lets through.
// Each message is a card with a stripe in the colour of the agent system that sent it, who to whom along its top (an
// agent's number opening its chat) and what it said under them. A virtual list makes only the cards in sight and it
// follows new messages while it's at its end. Filtered to nothing, it says so with a way back to all of them.
internal sealed partial class DawnScene
{
    // The most members the window has a chip for, the family's top and the first it started
    private const int FAMILY_MEMBERS_SHOWN = 6;

    // How tall the summary and the chips over the list are, the list starting under them
    private const float FAMILY_HEAD = 126.0f;

    private WindowPanel? familyWindow;
    private VirtualStack? familyList;
    private StackPanel? familyFilters;
    private StackPanel? familyEmpty;
    private HexAgentChat? familyOf;
    private readonly List<HexMailEntry> familyShown = [];
    private string familyKind = string.Empty;
    private int? familyMember;

    // What the list was filled from, the log's length and the filters, filled again when any moves
    private (int Count, string Kind, int? Member) familyFilled = (-1, string.Empty, null);
    private HexMailEntry? familyLast;

    /// <summary>The family's whole conversation in its window, the one up already brought to the family of this chat.</summary>
    private void ShowFamilyTalk(HexAgentChat chat)
    {
        familyWindow?.Parent?.Remove(familyWindow);
        familyOf = hub.RootOf(chat);
        familyKind = string.Empty;
        familyMember = null;
        familyFilled = (-1, string.Empty, null);
        familyShown.Clear();

        var window = new WindowPanel(Said("noon.family_window", ("name", OneLine(familyOf.Shown, 40))))
        {
            Icon = "mail",
            Size = new Vector2(720, 600),
            MinSize = new Vector2(420, 300)
        };
        DressWindow(window);
        // Solid, a page of messages read through glass had the chat behind it showing through its words
        window.Glass = null;
        window.OnClosed = () =>
        {
            window.Parent?.Remove(window);
            familyWindow = null;
            familyList = null;
            familyFilters = null;
            familyEmpty = null;
            familyOf = null;
        };

        // The summary and the chips along the top and the list under them, the list's own room starting below them
        familyFilters = window.Add(new StackPanel
        {
            Spacing = 8, Anchor = Origin.Top, Pivot = Origin.Top, Fill = UIFill.Horizontal,
            Size = new Vector2(0, FAMILY_HEAD), Padding = new UIEdges(16, 12, 16, 0)
        });
        familyList = window.Add(new VirtualStack
        {
            Fill = UIFill.Both, Spacing = 8, Estimate = 64, StickToEnd = true, Padding = new UIEdges(16, FAMILY_HEAD + 4, 10, 12),
            Make = index => FamilyRow(familyShown[index]),
            KeyOf = index => familyShown[index].Mail.Seq
        });
        familyEmpty = window.Add(WindowEmpty("mail", Said("noon.family_none"), Said("noon.family_show_all"), () =>
        {
            familyKind = string.Empty;
            familyMember = null;
            familyFilled = (-1, string.Empty, null);
            familyFiltersStale = true;
        }));
        familyEmpty.Anchor = Origin.Top;
        familyEmpty.Pivot = Origin.Top;
        familyEmpty.Position = new Vector2(0, -FAMILY_HEAD);
        familyEmpty.Visible = false;

        // The summary and the chips over the list rather than under it. The list fills the window and starts under them by
        // its padding, and laid over them it took every press on a chip, none of them could be pressed
        window.Remove(familyFilters);
        window.Add(familyFilters);

        familyWindow = window;
        chrome.Root.Add(window);
        DrawFamilyFilters();
        FillFamilyTalk();
        familyList.ScrollToEnd();
    }

    // The summary, then the kinds and the members to filter by as chips, the ones picked filled
    private void DrawFamilyFilters()
    {
        if (familyFilters is null || familyOf is null)
            return;

        ClearRows(familyFilters);
        IReadOnlyList<HexMailEntry> all = hub.MailOf(familyOf).All;
        List<HexAgentChat> members = FamilyOf(familyOf);
        string count = all.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string kinds = string.Join(" · ", all.GroupBy(entry => entry.Mail.Kind).OrderByDescending(kind => kind.Count()).Take(4).Select(kind => $"{kind.Count()} {kind.Key}"));
        familyFilters.Add(WindowSummary(count, Said("noon.family_messages"),
            Said("noon.family_between", ("count", members.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("kinds", kinds))));

        var byKind = familyFilters.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 6, Size = new Vector2(0, CHIP_TALL), Anchor = Origin.Left, Pivot = Origin.Left });
        foreach (string kind in FAMILY_FILTERS)
        {
            string label = Said(kind.Length == 0 ? "noon.family_all" : $"noon.family_{kind}");
            int fits = kind.Length == 0 ? all.Count : all.Count(entry => entry.Mail.Kind == kind);
            FamilyFilter(byKind, label, fits, familyKind == kind, () => familyKind = kind, null);
        }

        var byMember = familyFilters.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 6, Size = new Vector2(0, CHIP_TALL), Anchor = Origin.Left, Pivot = Origin.Left });
        FamilyFilter(byMember, Said("noon.family_everyone"), null, familyMember is null, () => familyMember = null, null);
        foreach (HexAgentChat member in members.Take(FAMILY_MEMBERS_SHOWN))
        {
            int id = member.Id;
            FamilyFilter(byMember, $"#{id} {OneLine(member.Shown, 14)}", null, familyMember == id, () => familyMember = id, AgentTint(member.Agent, Skin));
        }
    }

    private void FamilyFilter(StackPanel row, string label, int? count, bool picked, Action pick, Vector4? dot)
    {
        // Drawn again on the next tick, never under the press
        Chip(row, label, count, picked, () =>
        {
            pick();
            familyFilled = (-1, string.Empty, null);
            familyFiltersStale = true;
        }, dot);
    }

    private bool familyFiltersStale;

    // The top of a family and every chat under it, in the order they were started
    private List<HexAgentChat> FamilyOf(HexAgentChat root)
    {
        var all = new List<HexAgentChat>();
        void Under(HexAgentChat chat, int depth)
        {
            all.Add(chat);
            if (depth < 16)
            {
                foreach (HexAgentChat started in hub.Children(chat))
                    Under(started, depth + 1);
            }
        }

        Under(root, 0);
        return all;
    }

    /// <summary>Every tick while the window's up, the list filled again when the log grew or a filter moved.</summary>
    private void UpdateFamilyWindow()
    {
        if (familyWindow is null)
            return;
        if (familyOf is null || ChatById(familyOf.Id) is null)
        {
            familyWindow.OnClosed?.Invoke();
            return;
        }

        if (familyFiltersStale)
        {
            familyFiltersStale = false;
            DrawFamilyFilters();
        }

        FillFamilyTalk();
    }

    // The messages the filters let through, put on the end when only more came, all of them again otherwise
    private void FillFamilyTalk()
    {
        if (familyList is null || familyOf is null)
            return;

        IReadOnlyList<HexMailEntry> all = hub.MailOf(familyOf).All;
        var wanted = (all.Count, familyKind, familyMember);
        if (wanted == familyFilled)
            return;

        // Only more at the end, the same filters and the last message gone through still where it was (the log lets its
        // oldest go past the most it keeps, which moves every place), the rows already shown stay as they are
        bool more = familyFilled.Count > 0 && familyFilled.Kind == familyKind && familyFilled.Member == familyMember
            && all.Count > familyFilled.Count && ReferenceEquals(all[familyFilled.Count - 1], familyLast);
        int from = more ? familyFilled.Count : 0;
        if (!more)
            familyShown.Clear();
        for (int i = from; i < all.Count; i++)
        {
            if (Lets(all[i]))
                familyShown.Add(all[i]);
        }

        // The counts on the chips move with the log
        if (all.Count != familyFilled.Count)
            familyFiltersStale = true;
        familyFilled = wanted;
        familyLast = all.Count > 0 ? all[^1] : null;
        familyList.ItemCount = familyShown.Count;
        if (more)
            familyList.ItemsChanged();
        else
            familyList.Reset();
        if (familyEmpty is not null)
            familyEmpty.Visible = familyShown.Count == 0;
    }

    // Whether a message gets past the filters
    private bool Lets(HexMailEntry entry) =>
        (familyKind.Length == 0 || entry.Mail.Kind == familyKind)
        && (familyMember is not { } member || entry.Mail.From == member || entry.Mail.To == member);

    // A message as a card, who to whom, its kind, how it got there and all of what it said
    private UIComponent FamilyRow(HexMailEntry entry) => FamilyMessage(entry, int.MaxValue);

    /// <summary>
    /// A message of the family as a card, a stripe in the colour of whoever sent it, who to whom along its top with each
    /// agent's number a button opening its chat, its kind and how it got there at the right, what it said under them. The
    /// words cut to <paramref name="most"/> characters.
    /// </summary>
    private UIComponent FamilyMessage(HexAgentMail mail, HexMailRoute route, int most)
    {
        bool compact = most != int.MaxValue;
        Panel card = StripedCard(WhoTint(mail.From), compact ? 8.0f : 10.0f);
        var stack = card.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = compact ? 3 : 5 });

        var head = stack.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 20), Color = Vector4.Zero });
        var who = head.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 4, Anchor = Origin.Left, Pivot = Origin.Left, Size = new Vector2(0, 20) });
        FamilyWho(who, mail.From, compact);
        who.Add(new Label { Text = "$(arrow-right)", TextScale = 0.11f, Dim = true, Size = new Vector2(14, 20) });
        FamilyWho(who, mail.To == HexMailLog.USER ? null : mail.To, compact);
        string about = compact ? mail.Kind : $"{mail.Kind} · {RouteWord(route)}";
        head.Add(new Label
        {
            Text = $"$({MailIcon(mail.Kind)}) {UIRichText.Literal(about)}", TextScale = 0.115f, Dim = true,
            Anchor = Origin.Right, Pivot = Origin.Right
        });

        stack.Add(new Label
        {
            Text = UIRichText.Literal(compact ? OneLine(mail.Text, most) : mail.Text), TextScale = compact ? 0.13f : 0.145f,
            Align = Origin.Left, Wrap = true, Fill = UIFill.Horizontal
        });
        return card;
    }

    private UIComponent FamilyMessage(HexMailEntry entry, int most) => FamilyMessage(entry.Mail, entry.Route, most);

    // Who sent or was sent a message, the person in the accent, an agent its system's badge and its number and name as a
    // button to its chat
    private void FamilyWho(StackPanel row, int? id, bool compact)
    {
        Vector4 tint = WhoTint(id);
        if (id is not { } chat || ChatById(chat) is not { } found)
        {
            string name = id is { } gone ? $"#{gone}" : Said("noon.family_you");
            row.Add(new Label
            {
                Text = $"[color:{HexColour(tint)}]{UIRichText.Literal(name)}[/color]", TextScale = 0.125f, Bold = true,
                Size = new Vector2(MathF.Ceiling(name.Length * 6.6f) + 4, 20)
            });
            return;
        }

        HexAgentProfile? profile = HexAgentProfiles.All.FirstOrDefault(each => each.Id == found.Agent);
        var badge = row.Add(new Panel { Size = new Vector2(22, 14), Radius = 3, Color = tint with { W = 0.22f } });
        badge.Add(new Label { Text = $"[color:{HexColour(tint)}]{Initials(profile?.Name ?? found.Agent)}[/color]", TextScale = 0.085f, Bold = true, Anchor = Origin.Center, Pivot = Origin.Center });
        string label = compact ? $"#{chat}" : $"#{chat} {OneLine(found.Shown, 22)}";
        row.Add(new Button
        {
            Label = UIRichText.Literal(label), Style = "button_flat", LabelScale = 0.12f, Animated = false,
            Size = new Vector2(MathF.Ceiling(label.Length * 5.9f) + 10, 20),
            Tooltip = Said("noon.family_open", ("name", OneLine(found.Shown, 40))),
            OnPressed = () =>
            {
                if (ChatById(chat) is { } open)
                {
                    OpenAgentChat(open);
                    ShowStage(NoonStage.Chat);
                }
            }
        });
    }
}
