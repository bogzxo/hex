namespace Hex.Dawn;

// The agents in the status bar, how many are working and how many want looking at (finished unseen, failed, or with
// messages nobody read), there while there's a chat at all. A press goes to the next one that wants looking at, in Noon,
// or opens Noon when none does. Its words are put right when the hub says a chat moved, not every tick.
internal sealed partial class DawnScene
{
    private StatusItem agentsItem = null!;

    // What the item said last, so its words are only set when they change
    private string agentsItemSaid = string.Empty;

    private void BuildAgentsItem()
    {
        agentsItem = statusBar.Add("$(robot)", right: true, pressed: () =>
        {
            if (AgentsWanting() > 0)
            {
                ShowFace(HexFace.Noon);
                NextNeedingMe();
            }
            else
                ShowFace(HexFace.Noon);
        }, tooltip: Said("agents_item.tip"));
        agentsItem.IsVisible = () => agentChats.Count > 0;
        agentsChanged += DrawAgentsItem;
        agentsChanged += CountHorizonAgents;
    }

    // How many chats want looking at
    private int AgentsWanting() => agentChats.Count(chat => chat.HasPending || chat.Unread || (!chat.Running && chat.UnreadMailCount() > 0));

    /// <summary>The item's words as the chats are now.</summary>
    private void DrawAgentsItem()
    {
        int working = agentChats.Count(chat => chat.Running), wanting = AgentsWanting();
        string said = (working > 0 ? $"$(loading~spin) {Said("agents_item.working", ("count", working.ToString(System.Globalization.CultureInfo.InvariantCulture)))}" : $"$(robot) {Said(agentChats.Count == 1 ? "agents_item.agents_one" : "agents_item.agents", ("count", agentChats.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)))}")
            + (wanting > 0 ? $"  $(bell-dot) {wanting}" : string.Empty);
        if (said == agentsItemSaid)
            return;
        agentsItemSaid = said;
        agentsItem.Text = said;
        agentsItem.Tooltip = Said("agents_item.tip_counts", ("agents", agentChats.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("working", working.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("wanting", wanting.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }
}
