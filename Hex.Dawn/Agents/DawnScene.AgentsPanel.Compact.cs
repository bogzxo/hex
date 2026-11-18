using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

// An agent told to compact its context, summing up what it has so far to make room, from the Agents menu, the palette or
// the button Details shows beside a context bar that's nearly full. Its run says the message that does it (Claude Code's
// /compact), a system that says none can't be told. Asked while it works, it's done once the turn is over, a compaction
// in the middle of a turn being the agent's own business.
internal sealed partial class DawnScene
{
    // The chats to compact as soon as their turn is over
    private readonly HashSet<HexAgentChat> compactAfter = [];

    /// <summary>The message a chat's agent system compacts with, null when it says none.</summary>
    private string? CompactMessage(HexAgentChat? chat) =>
        chat is not null && extensions.AgentRuns.TryGetValue(chat.Agent, out var run) && run.Run.Properties.TryGetValue("compact", out IRuntimeValue? said)
            && said is StringValue { Value.Length: > 0 } message ? message.Value : null;

    /// <summary>Whether a chat can be told to compact, its system saying how and it not waiting to already.</summary>
    private bool CanCompact(HexAgentChat? chat) => CompactMessage(chat) is not null && !compactAfter.Contains(chat!);

    /// <summary>A chat's agent told to compact its context, now, or once the turn it's in is over.</summary>
    private void CompactAgent(HexAgentChat? chat)
    {
        if (chat is null || !CanCompact(chat))
            return;
        if (chat.Running || agentRuns.Any(run => run.Chat == chat))
        {
            compactAfter.Add(chat);
            chat.Note(Said("agents_panel.compact_after"));
            return;
        }

        StartCompact(chat);
    }

    private void StartCompact(HexAgentChat chat)
    {
        if (CompactMessage(chat) is not { } message || !extensions.AgentRuns.TryGetValue(chat.Agent, out var run))
            return;
        StartAgent(chat, run.Extension, run.Run, message, message);
    }

    /// <summary>Every tick while any wait, those whose turn is over and whose agent has gone told to compact.</summary>
    private void TickCompactAfter()
    {
        if (compactAfter.Count == 0)
            return;
        foreach (HexAgentChat chat in compactAfter.ToArray())
        {
            if (chat.Running || agentRuns.Any(run => run.Chat == chat))
                continue;
            compactAfter.Remove(chat);
            if (!chat.Stopped && !chat.Failed)
                StartCompact(chat);
        }
    }
}
