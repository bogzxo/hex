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

    /// <summary>Updates the "Show earlier" counter in place when older rows spill off the top during streaming.</summary>
    private void UpdateAgentEarlierCount(HexAgentChat chat)
    {
        if (chat.Dropped <= 0 || hub.History is null)
            return;
        if (agentsEarlier is null)
        {
            DrawAgentEarlier(chat);
            return;
        }
        int left = chat.Dropped - agentsEarlierShown;
        if (left > 0 && agentsEarlier.Children.Count > 0 && agentsEarlier.Children[0] is Button button)
            button.Label = Said("agents_panel.earlier", ("count", left.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        else
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

    // How long the chat's been at its latest with earlier rows read back, before they go back to its file
    private const long EARLIER_KEPT_MS = 60_000;

    // When the chat came to its latest with earlier rows read back, null while it isn't there or has none
    private long? agentsEarlierAtEnd;

    /// <summary>
    /// Every tick, the next earlier rows read back when the chat's been scrolled to its very top. And those read back let
    /// go again once the chat has stayed at its latest for a minute, somebody done looking back, so a chat read far back
    /// once doesn't hold them for as long as it's open.
    /// </summary>
    private void LoadEarlierAtTop()
    {
        if (agentChat is not { } chat || agentsEarlier is null)
            return;
        if (agentsEarlierShown < chat.Dropped && agentsScroll.MaxOffset > 0 && agentsScroll.Offset <= 1.0f && agentRowHeld is not { Ticks: > 0 })
            ShowAgentEarlier(chat);

        bool atEnd = agentsEarlierShown > 0 && agentsScroll.Offset >= agentsScroll.MaxOffset - 1.0f;
        if (!atEnd)
        {
            agentsEarlierAtEnd = null;
            return;
        }

        long now = System.Environment.TickCount64;
        agentsEarlierAtEnd ??= now;
        if (now - agentsEarlierAtEnd < EARLIER_KEPT_MS)
            return;
        agentsEarlierAtEnd = null;
        agentsEarlierShown = 0;
        FillAgentEarlier(chat);
        agentsScroll.ScrollToEnd();
    }
}
