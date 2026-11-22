namespace Hex.Core;

internal sealed record HexAgentApprovalStatus(int Number, string Title, string Detail)
{
    public string Summary => $"asks leave: {Title} (request {Number})";
}

/// <summary>
/// A chat as the tools see it off the editor's thread, as it was the last time the hub was pumped. What hex_agent_wait
/// and hex_agent_list read, so they never touch a chat while the editor's thread is changing it.
/// </summary>
internal sealed record HexHubRow(int Id, string Shown, string Agent, string? Model, int? Parent, int Depth, string State,
    string Reply, double Seconds, double Cost, int Version, HexAgentChat Chat, HexAgentMail[] Mail)
{
    public IReadOnlyList<HexAgentApprovalStatus> Approvals { get; init; } = [];
    public HexLinkAgentStarter? LinkStarter { get; init; }

    /// <summary>The linked device that started this chat, null when it was started here.</summary>
    public string? LinkDevice { get; init; }
    public bool RouteLinkApprovals { get; init; }

    /// <summary>Its messages not read yet, read off any thread by the number the chat has read up to.</summary>
    public HexAgentMail[] Unread => [.. Mail.Where(message => message.Seq > Chat.MailSeen)];

    /// <summary>Returns the whole unread batch when the wait ends or mail wakes it, with the person's messages first.</summary>
    public HexAgentMail[] UnreadForWait(bool wakesOnMail, bool finished)
    {
        HexAgentMail[] unread = Unread;
        if (!finished && !wakesOnMail &&
            !unread.Any(message => message.FromPerson || message.Kind is "question" or "blocked"))
            return [];

        // OrderBy is stable. Both the person's messages and the other messages stay in arrival order.
        return [.. unread.OrderByDescending(message => message.FromPerson)];
    }
}

/// <summary>Every session as it was at one pump of the hub, made again only when one of them moved.</summary>
internal sealed record HexHubSnapshot(long Version, IReadOnlyDictionary<int, HexHubRow> Rows)
{
    public static readonly HexHubSnapshot Empty = new(0, new Dictionary<int, HexHubRow>());
}
