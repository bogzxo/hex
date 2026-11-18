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
        var filters = noonContextRows.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 2, Fill = UIFill.Horizontal, Size = new Vector2(0, 22) });
        foreach (string kind in FAMILY_FILTERS)
        {
            int count = kind.Length == 0 ? all.Count : all.Count(entry => entry.Mail.Kind == kind);
            string label = $"{Said(kind.Length == 0 ? "noon.family_all" : $"noon.family_{kind}")} {count}";
            var button = filters.Add(new Button
            {
                Label = label, Style = "button_flat", Size = new Vector2(0, 20), LabelScale = 0.11f, Animated = false, Selected = noonTalkShown == kind,
                Enabled = count > 0 || kind.Length == 0
            });
            button.Size = new Vector2(MathF.Ceiling(label.Length * 6.2f) + 12, 20);
            button.OnPressed = () =>
            {
                noonTalkShown = kind;
                noonContextDrawn = string.Empty;
                noonWait = 0.0f;
            };
        }

        HexMailEntry[] shown = [.. all.Where(entry => noonTalkShown.Length == 0 || entry.Mail.Kind == noonTalkShown).TakeLast(FAMILY_SHOWN)];
        if (shown.Length == 0)
            noonContextRows.Add(ContextText(Said("noon.family_none"), dim: true));
        foreach (HexMailEntry entry in shown)
        {
            HexAgentMail mail = entry.Mail;
            string to = mail.To == HexMailLog.USER ? Said("noon.family_you") : $"#{mail.To}";
            string from = mail.From is { } id ? $"#{id}" : Said("noon.family_you");
            ContextLine(MailIcon(mail.Kind), $"{from} → {to} · {mail.Kind} · {RouteWord(entry.Route)}: {OneLine(mail.Text, 160)}", wrap: true);
        }

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
