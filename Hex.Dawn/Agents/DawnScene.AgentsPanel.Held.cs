namespace Hex.Dawn;

// What somebody typed for an agent while it works, held by Hex until the agent next says something (a line of its
// output, or its turn coming to an end), and written to it then. An agent takes what it's told between its steps, so
// holding it costs nothing, and meanwhile the queued bubble can be taken back into the box, changed and sent again.
// Written at once to the agent's stdin, a message there could never be taken back.
internal sealed partial class DawnScene
{
    // The messages held, oldest first, each with the line for the agent's stdin and what was typed for it
    private readonly List<(HexAgentChat Chat, HexAgentEntry Entry, string Line, string Asked)> heldSteers = [];

    /// <summary>Whether a queued row is one Hex still holds, so it can be taken back and changed.</summary>
    private bool IsHeld(HexAgentEntry entry) => heldSteers.Any(held => ReferenceEquals(held.Entry, entry));

    /// <summary>
    /// A chat's held messages written to its agent, in the order they were typed. One the agent can't take now (it's gone)
    /// is marked as never heard, the way one that couldn't be written was.
    /// </summary>
    private void FlushSteers(HexAgentChat chat)
    {
        for (int i = 0; i < heldSteers.Count; i++)
        {
            var held = heldSteers[i];
            if (held.Chat != chat)
                continue;
            heldSteers.RemoveAt(i--);
            if (SteeredRun(chat) is { } run && run.Process.Send(held.Line))
                continue;
            held.Entry.Queued = false;
            held.Entry.Unheard = true;
            chat.Touch(held.Entry);
        }
    }

    /// <summary>
    /// A held message taken back out of the chat and put in the box to be changed, sent again as anything typed is. False
    /// when it had gone to the agent already.
    /// </summary>
    private bool EditHeld(HexAgentEntry entry)
    {
        int at = heldSteers.FindIndex(held => ReferenceEquals(held.Entry, entry));
        if (at < 0)
            return false;

        var held = heldSteers[at];
        heldSteers.RemoveAt(at);
        held.Chat.Withdraw(entry);
        if (agentChat != held.Chat)
            OpenAgentChat(held.Chat);
        agentsInput.Text = held.Asked;
        agentsInput.Focus();
        agentsInput.Document.MoveTo(new UITextPosition(agentsInput.Document.LineCount - 1, agentsInput.Document.LineAt(agentsInput.Document.LineCount - 1).Length));
        return true;
    }
}
