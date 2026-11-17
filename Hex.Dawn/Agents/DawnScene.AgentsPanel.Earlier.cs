using System.Numerics;

using Horizon.Rendering;

namespace Hex.Dawn;

// The rows of a chat it no longer keeps in memory, read back from its file only when somebody asks for them, a press on
// "Show earlier" over the first row it keeps, or scrolling to the top. A hundred at a time, drawn above the rows it keeps
// and let go of again as soon as another chat is on screen. A long agent's chat held thousands of rows in memory and drew
// them all, only to be read now and then.
internal sealed partial class DawnScene
{
    // How many earlier rows are read back at a time
    private const int EARLIER_STEP = 100;

    // What's above the kept rows, the button and the rows read back, and how many of them there are
    private StackPanel? agentsEarlier;
    private int agentsEarlierShown;

    /// <summary>
    /// The block over the rows a chat keeps, made with the rows when the chat is drawn from the start. Nothing when the
    /// chat has never let a row go.
    /// </summary>
    private void DrawAgentEarlier(HexAgentChat chat)
    {
        agentsEarlier = null;
        agentsEarlierShown = 0;
        if (chat.Dropped <= 0 || hub.History is null)
            return;

        agentsEarlier = agentsRows.Insert(0, new StackPanel { Fill = UIFill.Horizontal, Spacing = 8 });
        FillAgentEarlier(chat);
    }

    // The button and what's been read back, made again as more is
    private void FillAgentEarlier(HexAgentChat chat)
    {
        if (agentsEarlier is null)
            return;

        Empty(agentsEarlier);
        int left = chat.Dropped - agentsEarlierShown;
        if (left > 0)
        {
            agentsEarlier.Add(new Button
            {
                Label = Said("agents_panel.earlier", ("count", left.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                Icon = "history", Style = "button_flat", Fill = UIFill.Horizontal, Size = new Vector2(0, 26), LabelScale = 0.125f,
                Animated = false, OnPressed = () => ShowAgentEarlier(chat)
            });
        }

        if (agentsEarlierShown > 0)
        {
            foreach (HexAgentEntry entry in hub.History!.Read(chat.Key, 0, agentsEarlierShown))
                agentsEarlier.Add(RowOf(chat, entry));
        }
    }

    /// <summary>
    /// Another hundred earlier rows read back over the ones shown, the chat kept where it was on screen as they come in
    /// above it.
    /// </summary>
    private void ShowAgentEarlier(HexAgentChat chat)
    {
        if (agentsEarlier is null || agentsEarlierShown >= chat.Dropped)
            return;

        if (chat.Entries.Count > 0)
            HoldRowInPlace(chat.Entries[0], agentRowsShown.Count > 0 ? agentRowsShown[0].Row : agentsEarlier);
        agentsEarlierShown = Math.Min(chat.Dropped, agentsEarlierShown + EARLIER_STEP);
        FillAgentEarlier(chat);
    }

    /// <summary>Every tick, the next earlier rows read back when the chat's been scrolled to its very top.</summary>
    private void LoadEarlierAtTop()
    {
        if (agentChat is { } chat && agentsEarlier is not null && agentsEarlierShown < chat.Dropped
            && agentsScroll.MaxOffset > 0 && agentsScroll.Offset <= 1.0f && agentRowHeld is not { Ticks: > 0 })
            ShowAgentEarlier(chat);
    }
}
