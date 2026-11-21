namespace Hex.Dawn;

// Agentic work that a usage or session limit stopped, started again by itself once the limit has reset, so a run of
// agents left working overnight gets on with it at the hour the limit comes back rather than when the person next looks.
// A chat whose last turn ended refused by a limit (its row the limit's error, Limit Hit, a time it resets at) is sent
// one message telling it so a minute after that time, the minute being the systems' clocks and ours not agreeing. Once
// for each reset: refused again, it waits for the next one. Stopped by the person, it's left alone. The agents_resume_after_limit
// setting turns it off.
internal sealed partial class DawnScene
{
    // How long after the limit says it resets the chat is started again
    private static readonly TimeSpan RESUME_AFTER = TimeSpan.FromMinutes(1);

    // The chats started again for a reset, by the chat and the reset, so a reset is acted on once
    private readonly HashSet<(int Chat, DateTimeOffset Resets)> resumedFor = [];

    // When it was last looked at, once a second is plenty for a wait of hours
    private long resumeLookedAt;

    /// <summary>Every tick, with the agents' lines: the chats a limit stopped whose limit has reset, started again.</summary>
    private void TickResumeAfterLimits()
    {
        long now = System.Environment.TickCount64;
        if (now - resumeLookedAt < 1000 || !settings.AgentsResumeAfterLimit)
            return;
        resumeLookedAt = now;

        DateTimeOffset clock = DateTimeOffset.Now;
        foreach (HexAgentChat chat in agentChats)
        {
            if (ResumeDue(chat, clock) is not { } resets || !extensions.AgentRuns.TryGetValue(chat.Agent, out var run) || AgentRunBusy(chat))
                continue;

            resumedFor.Add((chat.Id, resets));
            chat.Note(Said("agents_panel.resumed_after_limit"));
            StartAgent(chat, run.Extension, run.Run, Said("agents_panel.resume_prompt"), Said("agents_panel.resume_asked"));
        }
    }

    /// <summary>
    /// The reset a chat is due to be started again for: its last turn failed on a reached limit that has a reset time, a
    /// minute past now, not started again for it yet, and nobody stopped it. Null when it isn't due.
    /// </summary>
    private DateTimeOffset? ResumeDue(HexAgentChat chat, DateTimeOffset now) =>
        !chat.Running && !chat.Stopped && chat.Failed
        && chat.Limit is { State: HexAgentLimitState.Hit, Resets: { } resets }
        && now >= resets + RESUME_AFTER && !resumedFor.Contains((chat.Id, resets))
            ? resets
            : null;
}
