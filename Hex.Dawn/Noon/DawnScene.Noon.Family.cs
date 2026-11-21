using System.Numerics;

namespace Hex.Dawn;

// The family's conversation in Noon's Details, everything said between the agents of the family the chat on screen is
// in, the person watching and Hex, from the family's one log (HexMailLog), who to whom, what kind and how it reached
// the one it was for. A row of filters over it shows only the findings, the questions or the instructions. The newest
// few are shown, the older folded into a row that opens them in place over those and folds them again, open or folded
// kept for each chat. Pressing a filter or the fold has it drawn on the next tick, never under the press.
internal sealed partial class DawnScene
{
    // The kinds the filters pick, empty for all of them
    private static readonly string[] FAMILY_FILTERS = [string.Empty, "finding", "question", "instruction"];

    // How many messages of the family are shown while the older are folded, the latest
    private const int FAMILY_SHOWN = 5;

    // The kind of message the family's view shows, empty for every kind
    private string noonTalkShown = string.Empty;

    // The chats whose family conversation has its older messages opened, by id
    private readonly HashSet<int> noonTalkOpen = [];

    /// <summary>The family's conversation for the chat on screen, nothing while nobody in it has said anything to anyone.</summary>
    private void DrawFamilyTalk(HexAgentChat chat)
    {
        IReadOnlyList<HexMailEntry> all = hub.MailOf(chat).All;
        if (all.Count == 0)
            return;

        ContextSection(Said("noon.family_talk", ("count", all.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        // The way to the whole of it first, in a window of its own filtered by kind and by member. Under the latest it was
        // found only by going past them
        ContextLink("link-external", Said("noon.family_every", ("count", all.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))), () => ShowFamilyTalk(chat));
        // Chips as the family's window has, only the kinds there are messages of, so the row fits Details' width
        var filters = noonContextRows.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 5, Fill = UIFill.Horizontal, Size = new Vector2(0, CHIP_TALL) });
        foreach (string kind in FAMILY_FILTERS)
        {
            int count = kind.Length == 0 ? all.Count : all.Count(entry => entry.Mail.Kind == kind);
            if (count == 0 && kind.Length > 0 && noonTalkShown != kind)
                continue;
            Chip(filters, Said(kind.Length == 0 ? "noon.family_all" : $"noon.family_{kind}"), count, noonTalkShown == kind, () =>
            {
                noonTalkShown = kind;
                noonContextDrawn = CONTEXT_NOT_DRAWN;
                noonWait = 0.0f;
            });
        }

        HexMailEntry[] picked = [.. all.Where(entry => noonTalkShown.Length == 0 || entry.Mail.Kind == noonTalkShown)];
        if (picked.Length == 0)
            noonContextRows.Add(ContextText(Said("noon.family_none"), dim: true));

        // The older ones folded into a row over the newest, opened in place under it and folded again from it
        int older = Math.Max(0, picked.Length - FAMILY_SHOWN);
        bool open = noonTalkOpen.Contains(chat.Id);
        if (older > 0)
        {
            int id = chat.Id;
            ContextLink(open ? "chevron-up" : "chevron-right",
                open ? Said("noon.family_fewer") : Said("noon.family_more", ("count", older.ToString(System.Globalization.CultureInfo.InvariantCulture))), () =>
                {
                    if (!noonTalkOpen.Remove(id))
                        noonTalkOpen.Add(id);
                    noonContextDrawn = CONTEXT_NOT_DRAWN;
                    noonWait = 0.0f;
                });
        }

        for (int i = open ? 0 : older; i < picked.Length; i++)
            noonContextRows.Add(FamilyMessage(picked[i], 160));
    }

    // A message's kind as its icon
    private static string MailIcon(string kind) => kind switch
    {
        "finding" => "lightbulb",
        "question" => "question",
        "answer" => "comment",
        "instruction" => "arrow-right",
        "progress" => "pulse",
        "blocked" => "error",
        _ => "mail"
    };

    // How a message reached the one it was for, in a word or two
    private string RouteWord(HexMailRoute route) => Said(route switch
    {
        HexMailRoute.Steered => "noon.route_steered",
        HexMailRoute.WokeWait => "noon.route_woke",
        HexMailRoute.StartedTurn => "noon.route_started",
        _ => "noon.route_inbox"
    });
}
