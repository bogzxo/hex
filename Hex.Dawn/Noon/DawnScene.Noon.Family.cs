using System.Numerics;

namespace Hex.Dawn;

// The family's conversation in Noon's Details, everything said between the agents of the family the chat on screen is
// in, the person watching and Hex, from the family's one log (HexMailLog), who to whom, what kind and how it reached
// the one it was for. A row of filters over it shows only the findings, the questions or the instructions. Pressing a
// filter has it drawn on the next tick, never under the press.
internal sealed partial class DawnScene
{
    // The kinds the filters pick, empty for all of them
    private static readonly string[] FAMILY_FILTERS = [string.Empty, "finding", "question", "instruction"];

    // How many messages of the family are shown, the latest
    private const int FAMILY_SHOWN = 12;

    // The kind of message the family's view shows, empty for every kind
    private string noonTalkShown = string.Empty;

    /// <summary>The family's conversation for the chat on screen, nothing while nobody in it has said anything to anyone.</summary>
    private void DrawFamilyTalk(HexAgentChat chat)
    {
        IReadOnlyList<HexMailEntry> all = hub.MailOf(chat).All;
        if (all.Count == 0)
            return;

        ContextSection(Said("noon.family_talk", ("count", all.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))));
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

        HexMailEntry[] shown = [.. all.Where(entry => noonTalkShown.Length == 0 || entry.Mail.Kind == noonTalkShown).TakeLast(FAMILY_SHOWN)];
        if (shown.Length == 0)
            noonContextRows.Add(ContextText(Said("noon.family_none"), dim: true));
        foreach (HexMailEntry entry in shown)
            noonContextRows.Add(FamilyMessage(entry, 160));

        // The whole of it in a window of its own, filtered by kind and by member
        ContextLink("mail", Said("noon.family_every", ("count", all.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))), () => ShowFamilyTalk(chat));
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
