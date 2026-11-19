namespace Hex.Dawn;

// The keys of Noon, commands like any other so they're in the palette and can be bound again. Going from agent to agent
// in the fleet's order, up to the one that started this one, to the next that needs looking at, and stopping one. The
// ones that only make sense with the fleet on screen are greyed out in Dawn. Alt with Page Up and Page Down, since Alt
// with the arrows moves and copies lines in the editor.
internal sealed partial class DawnScene
{
    private void AddNoonCommands()
    {
        bool InNoon() => Face == HexFace.Noon;
        commands.Add("Agents/open", Said("noon.command_new"), () =>
        {
            if (Face != HexFace.Noon)
                ShowAgentsPanel();
            NewAgentChat();
        }, icon: "add");
        commands.Add("Agents/harness", Said("agents_panel.saved"), ShowHarnessSaved, icon: "graph");
        // Heard while typing too, the box has the keyboard whenever Agentic is up and they never type anything
        commands.Add("Agents/chats", Said("noon.command_next"), () => StepAgent(1), "Alt+PageDown", "arrow-down", UIKeyUse.BoundWhileTyping).IsEnabled = InNoon;
        commands.Add("Agents/chats", Said("noon.command_previous"), () => StepAgent(-1), "Alt+PageUp", "arrow-up", UIKeyUse.BoundWhileTyping).IsEnabled = InNoon;
        commands.Add("Agents/chats", Said("noon.command_parent"), AgentParent, "Alt+Home", "arrow-up", UIKeyUse.BoundWhileTyping).IsEnabled =
            () => InNoon() && agentChat?.Parent is { } parent && ChatById(parent) is not null;
        commands.Add("Agents/chats", Said("noon.command_needs_me"), NextNeedingMe, "Ctrl+Alt+.", "bell", UIKeyUse.BoundWhileTyping);
        commands.Add("Agents/chats", Said("noon.command_compact"), () => CompactAgent(agentChat), icon: "fold").IsEnabled = () => CanCompact(agentChat);
        commands.Add("Agents/chats", Said("noon.command_stop"), () => StopAgent(agentChat), "Ctrl+Alt+Shift+.", "debug-stop", UIKeyUse.BoundWhileTyping).IsEnabled =
            () => agentChat?.Running == true;
    }

    /// <summary>
    /// Every chat in the order the fleet and the list show them (ChatsInOrder), the newest that somebody started first,
    /// each followed by the ones it started. Not narrowed by the fleet's filter, what waits on the person (the inbox, the
    /// next that needs looking at) is everybody's whatever is typed there.
    /// </summary>
    /// <remarks>
    /// It walked the chats its own way once, the oldest first, so with three chats Alt+Page Down went to the card over
    /// this one rather than under it.
    /// </remarks>
    internal List<HexAgentChat> FleetOrder() => ChatsInOrder().ConvertAll(each => each.Chat);

    /// <summary>
    /// The next or the one before of the cards the fleet shows, round from the end to the start. In Noon only those the
    /// fleet's filter lets through, the keys never going to a chat that isn't on screen.
    /// </summary>
    private void StepAgent(int by)
    {
        List<HexAgentChat> order = Face == HexFace.Noon ? FleetShown().ConvertAll(each => each.Chat) : FleetOrder();
        if (order.Count == 0)
            return;
        int at = agentChat is null ? -1 : order.IndexOf(agentChat);
        int next = at < 0 ? (by > 0 ? 0 : order.Count - 1) : ((at + by) % order.Count + order.Count) % order.Count;
        OpenAgentChat(order[next]);
    }

    /// <summary>The chat that started the one on screen.</summary>
    private void AgentParent()
    {
        if (agentChat?.Parent is { } parent && ChatById(parent) is { } starter)
            OpenAgentChat(starter);
    }

    /// <summary>
    /// The next chat that wants looking at, after the one on screen in the fleet's order. One that failed, then one with
    /// messages for it nobody read, then one that finished while somebody looked elsewhere. Brought up in Noon, or in the
    /// side bar from Dawn. Said so when there's none.
    /// </summary>
    internal void NextNeedingMe()
    {
        List<HexAgentChat> order = FleetOrder();
        int at = agentChat is null ? -1 : order.IndexOf(agentChat);
        HexAgentChat[] after = [.. order.Skip(at + 1), .. order.Take(at + 1)];
        HexAgentChat? wanted = after.FirstOrDefault(chat => chat.HasPending)
            ?? after.FirstOrDefault(chat => chat.Failed && chat.Unread)
            ?? after.FirstOrDefault(chat => chat.UnreadMailCount() > 0 && !chat.Running)
            ?? after.FirstOrDefault(chat => chat.Unread && chat != agentChat);
        if (wanted is null)
        {
            Say(Said("noon.nothing_needs_me"));
            return;
        }

        if (Face != HexFace.Noon)
            ShowAgentsPanel();
        OpenAgentChat(wanted);
    }
}
