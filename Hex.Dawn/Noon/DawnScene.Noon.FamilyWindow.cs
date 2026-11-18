using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The whole of a family's conversation in a window of its own, every message of its log (up to the 2000 it keeps) where
// Details has the latest dozen. A virtual list makes only the rows in sight, filtered by kind and by member (what a
// member sent or was sent), and it follows new messages while it's at its end.
internal sealed partial class DawnScene
{
    // The most members the window has a filter for, the family's top and the first it started
    private const int FAMILY_MEMBERS_SHOWN = 6;

    private WindowPanel? familyWindow;
    private VirtualStack? familyList;
    private StackPanel? familyFilters;
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
            Size = new Vector2(600, 520),
            MinSize = new Vector2(360, 240)
        };
        DressWindow(window);
        window.OnClosed = () =>
        {
            window.Parent?.Remove(window);
            familyWindow = null;
            familyList = null;
            familyFilters = null;
            familyOf = null;
        };

        // The filters along the top and the list under them, the list's own room starting below them
        familyFilters = window.Add(new StackPanel
        {
            Direction = UIDirection.Horizontal, Spacing = 2, Anchor = Origin.Top, Pivot = Origin.Top, Fill = UIFill.Horizontal,
            Size = new Vector2(0, 52), Padding = new UIEdges(8, 4, 8, 0)
        });
        familyList = window.Add(new VirtualStack
        {
            Fill = UIFill.Both, Spacing = 6, Estimate = 22, StickToEnd = true, Padding = new UIEdges(10, 60, 6, 8),
            Make = index => FamilyRow(familyShown[index]),
            KeyOf = index => familyShown[index].Mail.Seq
        });
        familyWindow = window;
        chrome.Root.Add(window);
        DrawFamilyFilters();
        FillFamilyTalk();
        familyList.ScrollToEnd();
    }

    // The kinds and the members to filter by, two lines of buttons, the one picked marked
    private void DrawFamilyFilters()
    {
        if (familyFilters is null || familyOf is null)
            return;

        ClearRows(familyFilters);
        familyFilters.Direction = UIDirection.Vertical;
        var kinds = familyFilters.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 2, Size = new Vector2(0, 22), Anchor = Origin.Left, Pivot = Origin.Left });
        foreach (string kind in FAMILY_FILTERS)
        {
            string label = Said(kind.Length == 0 ? "noon.family_all" : $"noon.family_{kind}");
            FamilyFilter(kinds, label, familyKind == kind, () => familyKind = kind);
        }

        var members = familyFilters.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 2, Size = new Vector2(0, 22), Anchor = Origin.Left, Pivot = Origin.Left });
        FamilyFilter(members, Said("noon.family_everyone"), familyMember is null, () => familyMember = null);
        foreach (HexAgentChat member in FamilyOf(familyOf).Take(FAMILY_MEMBERS_SHOWN))
        {
            int id = member.Id;
            FamilyFilter(members, $"#{id} {OneLine(member.Shown, 14)}", familyMember == id, () => familyMember = id);
        }
    }

    private void FamilyFilter(StackPanel row, string label, bool picked, Action pick)
    {
        var button = row.Add(new Button { Label = UIRichText.Literal(label), Style = "button_flat", LabelScale = 0.11f, Animated = false, Selected = picked });
        button.Size = new Vector2(MathF.Ceiling(label.Length * 6.2f) + 12, 20);
        // Drawn again on the next tick, never under the press
        button.OnPressed = () =>
        {
            pick();
            familyFilled = (-1, string.Empty, null);
            familyFiltersStale = true;
        };
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

        familyFilled = wanted;
        familyLast = all.Count > 0 ? all[^1] : null;
        familyList.ItemCount = familyShown.Count;
        if (more)
            familyList.ItemsChanged();
        else
            familyList.Reset();
    }

    // Whether a message gets past the filters
    private bool Lets(HexMailEntry entry) =>
        (familyKind.Length == 0 || entry.Mail.Kind == familyKind)
        && (familyMember is not { } member || entry.Mail.From == member || entry.Mail.To == member);

    // A message as a row, who to whom, its kind, how it got there and all of what it said
    private Label FamilyRow(HexMailEntry entry)
    {
        HexAgentMail mail = entry.Mail;
        string to = mail.To == HexMailLog.USER ? Said("noon.family_you") : $"#{mail.To}";
        string from = mail.From is { } id ? $"#{id}" : Said("noon.family_you");
        return new Label
        {
            Text = $"$({MailIcon(mail.Kind)}) {UIRichText.Literal($"{from} → {to} · {mail.Kind} · {RouteWord(entry.Route)}: {mail.Text}")}",
            TextScale = 0.135f, Align = Origin.Left, Wrap = true, Fill = UIFill.Horizontal
        };
    }
}
