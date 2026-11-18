using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

// Agents starting agents. An agent of a chat of the Agents panel (or any client of Hex's server) starts another in the
// background, of whichever agent system and model, with something to do, scouting, research, a second opinion, a part of
// the work. They're chats of the panel like any other, under the one that started them, so what they do can be watched
// and they can be talked to. The one that started them waits for them, all or the first, reads what they said, tells
// them more and stops them. The waiting is off the editor's thread, on what the chats were the last tick, since the
// calls of the editor's tools go one at a time and an agent waited for must still have its own calls answered.
internal sealed partial class DawnScene
{
    // How many agents up an agent can be from the one somebody started, and how many started by agents run at once
    private const int AGENTS_DEEPEST = 2, AGENTS_AT_ONCE = 6;

    // How long hex_agent_wait waits when it isn't told, the most it waits, and how much of each reply it hands back
    private const int AGENTS_WAIT = 300, AGENTS_WAIT_MOST = 600, AGENTS_REPLY = 6000;

    // What the chats were the last tick, for hex_agent_wait off the editor's thread, the hub's snapshot of them
    private IReadOnlyDictionary<int, HexHubRow> agentStatuses => hub.Snapshot.Rows;

    private IEnumerable<HexMcpTool> AgentsTools() =>
    [
        new(new McpTool("hex_agent_list", "List agents and chats",
            "The agent systems that can be started (their ids and the models they offer) and the chats of Hex's Agents panel, each with its id, name, agent, model, state (running, done, failed, stopped) and which chat started it. Says which chat you are when you're one.",
            McpSchema.Empty()), AgentsList),
        new(new McpTool("hex_agent_spawn", "Start a background agent",
            "Start another agent in the background on a task of its own, scouting, research, a review, a part of the work, of any agent system and model listed by hex_agent_list. It's a chat of the Agents panel under yours, runs on its own while you go on, and has Hex's tools too. Give it everything it needs to know, it doesn't see your conversation. Its last message is what you get back. Returns its id at once, hex_agent_wait waits for it.",
            McpSchema.Object([
                ("prompt", McpSchema.String("What it's to do, complete in itself.")),
                ("name", McpSchema.String("A short name for it in the panel, a few words.")),
                ("agent", McpSchema.String("The agent system's id from hex_agent_list, your own by default, the panel's when you aren't one.")),
                ("model", McpSchema.String("The model, one of those hex_agent_list says or a whole name, the agent system's setting by default.")),
                ("options", new JsonObject { ["type"] = "object", ["description"] = "Other options of the agent system by id, as hex_agent_list says them." }),
                ("permissions", new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "What it may do without asking (allow) and may never do (deny), each a list of rules in its agent system's own words: Claude Code's tool rules such as \"Bash(git status:*)\" or \"Edit\", Antigravity's such as \"command(git status)\" (one whole command line, \"command(*)\" for any) or \"mcp(hex/*)\". Codex has none, its sandbox option says what it may do.",
                    ["properties"] = new JsonObject
                    {
                        ["allow"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                        ["deny"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
                    }
                }),
                ("approvals", McpSchema.OneOf("Who answers what it asks leave to do beyond its rules. you (the default) get each as a question to answer with hex_agent_approve, the person can answer on its card either way, person leaves them to the person alone.", "you", "person"))], "prompt")),
            AgentSpawn),
        AgentApproveTool(),
        new(new McpTool("hex_agent_wait", "Wait for agents",
            "Wait until agents finish their turn and get what they said. By default every agent you started that's running (every running chat when you aren't a chat of the panel), until all of them are done. any returns when the first is. A message sent to you by one of them returns at once with it, so you can steer the others on what it found (messages false waits on regardless). A timeout says what's still running.",
            McpSchema.Object([
                ("ids", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" }, ["description"] = "The chats to wait for, those you started that are running by default." }),
                ("mode", McpSchema.OneOf("all (the default) or any.", "all", "any")),
                ("messages", McpSchema.Flag("Return as soon as a message comes for you, true by default.")),
                ("timeout", McpSchema.Number($"Seconds, {AGENTS_WAIT} by default, {AGENTS_WAIT_MOST} at the most."))])),
            null, RunAway: AgentWait),
        new(new McpTool("hex_agent_read", "Read an agent's chat",
            "What a chat's agent said in its last turn and its state. full lists the whole chat, what was asked, said and every tool called, the latest kept when it's long.",
            McpSchema.Object([
                ("id", McpSchema.Integer("The chat.")),
                ("full", McpSchema.Flag("The whole chat rather than the last reply.")),
                ("most", McpSchema.Integer("The most characters, 8000 by default."))], "id")), AgentRead),
        new(new McpTool("hex_agent_send", "Tell an agent more",
            "Steer a chat's agent, an instruction. A finished one starts another turn with it, going on from where it was. A running one that can be told more takes it in as it works, one that can't finds it in its inbox and starts its next turn with it if it hasn't read it by then. hex_agent_message sends a finding or an answer without it being an instruction.",
            McpSchema.Object([("id", McpSchema.Integer("The chat.")), ("text", McpSchema.String("What to say."))], "id", "text")), AgentSend),
        new(new McpTool("hex_agent_stop", "Stop an agent",
            "Stop a chat's agent and everything it started. What it said so far stays in its chat.",
            McpSchema.Object([("id", McpSchema.Integer("The chat."))], "id")), AgentStopTool),
        new(new McpTool("hex_agent_close", "Close or clear an agent you started",
            "Delete the chat of an agent you started, its agent stopped and the chats it started deleted with it, when its work is done and its chat would only be in the way. clear true keeps the chat and empties it instead, its rows, mail and session gone, so the next hex_agent_send starts it afresh on a new task without what it knew. Only for agents you started.",
            McpSchema.Object([
                ("id", McpSchema.Integer("The chat.")),
                ("clear", McpSchema.Flag("Keep the chat, emptied, rather than deleting it."))], "id")), AgentClose)
    ];

    /// <summary>The chat a call came from, when it's an agent of the panel's.</summary>
    private static int? CallerOf(JsonObject args) => args.Integer(HexMcpCaller.CALLER);

    private HexAgentChat? ChatById(int id) => hub.ChatById(id);

    // A chat's state in a word
    private static string StateWord(HexAgentChat chat) => HexAgentHub.StateWord(chat);

    /// <summary>
    /// What the chats are now, made again for the tools that wait off the editor's thread whenever one of them moved.
    /// Every tick, a sum of their versions when nothing did.
    /// </summary>
    private void PublishAgentStatuses()
    {
        if (hub.Pump(AGENTS_REPLY))
            agentsChanged?.Invoke();
    }

    /* The agents as the parts of the editor see them (DawnContext.Agents) */

    private Action? agentsChanged;

    IReadOnlyList<HexAgentSession> IAgents.Sessions => hub.Sessions;

    event Action IAgents.Changed
    {
        add => agentsChanged += value;
        remove => agentsChanged -= value;
    }

    void IAgents.Show(int session)
    {
        if (ChatById(session) is not { } chat)
            return;
        if (Face != HexFace.Noon)
            ShowAgentsPanel();
        OpenAgentChat(chat);
    }

    // A chat in a line, for the lists
    private static string StatusLine(HexHubRow status) =>
        $"#{status.Id} \"{status.Shown}\" {status.Agent}{(status.Model is { Length: > 0 } model ? " " + model : string.Empty)} {status.State}"
        + (status.Seconds > 0 ? $" {status.Seconds.ToString("0.#", CultureInfo.InvariantCulture)}s" : string.Empty)
        + (status.Cost > 0 ? $" ${status.Cost.ToString("0.000", CultureInfo.InvariantCulture)}" : string.Empty)
        + (status.Parent is { } parent ? $" (started by #{parent})" : string.Empty)
        + (status.Unread.Length is > 0 and var unread ? $", {unread} unread message{(unread == 1 ? string.Empty : "s")}" : string.Empty);

    private IEnumerable<McpToolResult?> AgentsList(JsonObject args)
    {
        PublishAgentStatuses();
        var said = new StringBuilder();
        if (CallerOf(args) is { } caller)
            said.Append("You are chat #").Append(caller).Append(ChatById(caller)?.Parent is { } parent ? $", started by #{parent}" : string.Empty).Append(".\n\n");

        said.Append("Agent systems:\n");
        foreach (HexAgentProfile profile in RunnableAgents())
        {
            said.Append("- ").Append(profile.Id).Append(" (").Append(profile.Name).Append(StateOf(profile) == AgentStates.Missing ? ", not installed" : string.Empty).Append(')');
            foreach (AgentOption option in OptionsOf(profile.Id))
            {
                string[] values = [.. option.Choices.Select(choice => choice.Value).Where(value => value.Length > 0)];
                if (values.Length > 0)
                    said.Append(", ").Append(option.Key).Append(": ").Append(string.Join(" | ", values));
            }

            said.Append('\n');
        }

        said.Append("\nChats:\n");
        if (agentChats.Count == 0)
            said.Append("none\n");
        foreach (HexHubRow status in agentStatuses.Values.OrderBy(status => status.Id))
            said.Append(StatusLine(status)).Append('\n');
        yield return McpToolResult.Text(said.ToString().TrimEnd());
    }

    private IEnumerable<McpToolResult?> AgentSpawn(JsonObject args)
    {
        int? caller = CallerOf(args);
        HexAgentChat? parent = caller is { } id ? ChatById(id) : null;
        int depth = parent is null ? 1 : parent.Depth + 1;
        if (depth > AGENTS_DEEPEST)
        {
            yield return McpToolResult.Error($"Agents started by agents can't start more past {AGENTS_DEEPEST} deep. Do this yourself, or ask the one that started you.");
            yield break;
        }

        int going = agentChats.Count(chat => chat.Running && chat.Parent is not null);
        if (going >= AGENTS_AT_ONCE)
        {
            yield return McpToolResult.Error($"{going} agents started by agents are running already, the most there can be. hex_agent_wait for some first.");
            yield break;
        }

        string agent = args.String("agent") is { Length: > 0 } asked ? asked : parent?.Agent ?? AgentFor(null) ?? string.Empty;
        HexAgentProfile? profile = HexAgentProfiles.All.FirstOrDefault(each => each.Id == agent);
        if (profile is null || !extensions.AgentRuns.TryGetValue(agent, out var run))
        {
            yield return McpToolResult.Error($"There's no agent system '{agent}' that can be started, hex_agent_list says which there are.");
            yield break;
        }

        if (StateOf(profile) == AgentStates.Missing)
        {
            yield return McpToolResult.Error($"{profile.Name} isn't installed on this machine.");
            yield break;
        }

        string prompt = args.String("prompt") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            yield return McpToolResult.Error("Give it something to do, the prompt is empty.");
            yield break;
        }

        string? name = args.String("name") is { Length: > 0 } named ? (named.Length > 40 ? named[..39] + "…" : named) : null;
        var chat = new HexAgentChat(++agentChatsMade, agent) { Parent = parent?.Id, Depth = depth, Name = name };
        if (args.String("model") is { Length: > 0 } model)
            chat.Options["model"] = model;
        if (args["options"] is JsonObject options)
        {
            foreach (var (key, value) in options)
            {
                if (value is JsonValue && value.ToString() is { Length: > 0 } chosen)
                    chat.Options[key] = chosen;
            }
        }

        // What it may do, said by the one starting it, and whose word what it asks waits on
        if (!TakePermissions(args, chat, out string? wrong))
        {
            agentChatsMade--;
            yield return McpToolResult.Error(wrong!);
            yield break;
        }

        agentChats.Add(chat);

        // It's told what it is, the one that started it reads its last message
        string from = parent is null ? "another agent" : $"chat #{parent.Id}";
        string told = $"[You're a background agent in Hex, chat #{chat.Id}, started by {from}. It doesn't see this chat, only your last message, so end with what you found or did, complete in itself. "
            + "You can talk to it as you both work. hex_agent_message tells it what you find as soon as it matters, asks it what you need or says you're blocked, "
            + "and its instructions, answers and what the other agents it started have found reach you as you work or in hex_agent_inbox, look between steps. hex_agent_list shows who else is working with you.]\n\n"
            + prompt;
        StartAgent(chat, run.Extension, run.Run, told, prompt);

        // A model the agent system doesn't list goes as it was given, a whole name it may know, said so in case it's a typo
        string unlisted = chat.Options.TryGetValue("model", out string? picked) && OptionsOf(agent).FirstOrDefault(option => option.Key == "model") is { } models
            && !models.Choices.Any(choice => string.Equals(choice.Value, picked, StringComparison.OrdinalIgnoreCase))
            ? $" {picked} isn't one {profile.Name} lists, it's passed on as it is."
            : string.Empty;
        yield return McpToolResult.Text($"Started #{chat.Id}{(name is null ? string.Empty : $" \"{name}\"")} with {profile.Name}{(picked is not null ? " " + picked : string.Empty)}.{unlisted} hex_agent_wait waits for it.");
    }

    private async Task<McpToolResult> AgentWait(JsonObject args, CancellationToken cancel)
    {
        int? caller = CallerOf(args);
        bool any = args.String("mode") == "any";
        bool wakes = args.Flag("messages", true);
        double timeout = Math.Clamp(args.Number("timeout") ?? AGENTS_WAIT, 1, AGENTS_WAIT_MOST);
        long deadline = System.Environment.TickCount64 + (long)(timeout * 1000);

        IReadOnlyDictionary<int, HexHubRow> now = agentStatuses;
        int[] ids;
        if (args["ids"] is JsonArray asked && asked.Count > 0)
        {
            ids = [.. asked.Select(each => each is JsonValue value && value.TryGetValue(out double number) ? (int)number : -1)];
            foreach (int id in ids)
            {
                if (!now.ContainsKey(id))
                    return McpToolResult.Error($"There's no chat #{id}, hex_agent_list says which there are.");
            }

            if (caller is { } self && ids.Contains(self))
                return McpToolResult.Error("A chat can't wait for itself.");
        }
        else
        {
            // Those it started, the running ones, or all of them when they're all done already. A client that isn't a chat
            // of the panel didn't start any of its own as far as Hex knows, so it waits for every chat
            HexHubRow[] mine = [.. now.Values.Where(status => caller is null || status.Parent == caller)];
            ids = [.. (mine.Any(status => status.State == "running") ? mine.Where(status => status.State == "running") : mine).Select(status => status.Id)];
            if (ids.Length == 0)
                return McpToolResult.Error(caller is null ? "There are no chats to wait for, hex_agent_spawn starts one." : "You haven't started any agents to wait for, hex_agent_spawn starts one.");
        }

        // Known to be waiting, so what's sent to it meanwhile wakes this rather than going in on its stdin, which it
        // only reads once this call is over
        if (caller is { } waiting)
            agentsWaiting.AddOrUpdate(waiting, 1, (_, count) => count + 1);
        try
        {
            while (true)
            {
                now = agentStatuses;
                HexHubRow[] those = [.. ids.Select(id => now.GetValueOrDefault(id)).OfType<HexHubRow>()];
                int done = those.Count(status => status.State != "running");
                bool over = any ? done > 0 : done == those.Length;
                bool late = System.Environment.TickCount64 >= deadline;
                HexAgentMail[] mail = caller is { } me && wakes && now.GetValueOrDefault(me) is { } mine ? mine.Unread : [];
                if (over || late || mail.Length > 0)
                {
                    // What came for it is read with the report, the waiting one steering the others on it
                    string report = WaitReport(those, late && !over && mail.Length == 0);
                    if (mail.Length == 0)
                        return McpToolResult.Text(report);

                    now[caller!.Value].Chat.MailRead(mail[^1].Seq);
                    return McpToolResult.Text("Messages for you:\n\n" + string.Join("\n\n", mail.Select(MailText))
                        + (over ? string.Empty : "\n\nThe rest are still working, hex_agent_wait again to go on waiting.") + "\n\n" + report);
                }

                try
                {
                    await Task.Delay(250, cancel);
                }
                catch (TaskCanceledException)
                {
                    return McpToolResult.Error("hex_agent_wait was cancelled.");
                }
            }
        }
        finally
        {
            if (caller is { } waited)
                agentsWaiting.AddOrUpdate(waited, 0, (_, count) => Math.Max(0, count - 1));
        }
    }

    // What the agents waited for said, the finished ones with their replies and the rest by their state
    private static string WaitReport(HexHubRow[] those, bool late)
    {
        var said = new StringBuilder();
        if (late)
            said.Append("Timed out, ").Append(those.Count(status => status.State == "running")).Append(" still running. hex_agent_wait again to go on waiting.\n\n");
        foreach (HexHubRow status in those.OrderBy(status => status.State == "running"))
        {
            said.Append(StatusLine(status)).Append('\n');
            if (status.State != "running")
                said.Append(status.Reply.Length > 0 ? status.Reply : "(it said nothing)").Append("\n\n");
        }

        return said.ToString().TrimEnd();
    }

    private IEnumerable<McpToolResult?> AgentRead(JsonObject args)
    {
        int most = Math.Clamp(args.Integer("most") ?? 8000, 500, 60000);
        if (ChatById(args.Integer("id") ?? -1) is not { } chat)
        {
            yield return McpToolResult.Error("There's no such chat, hex_agent_list says which there are.");
            yield break;
        }

        PublishAgentStatuses();
        string head = StatusLine(agentStatuses[chat.Id]);
        if (!args.Flag("full"))
        {
            string reply = chat.LastReply(most);
            yield return McpToolResult.Text($"{head}\n{(reply.Length > 0 ? reply : chat.Running ? "(working, nothing said yet)" : "(it said nothing)")}");
            yield break;
        }

        var lines = new List<string>();
        foreach (HexAgentEntry entry in chat.Entries)
        {
            if (entry.Kind == HexAgentEntryKind.Thinking && string.IsNullOrWhiteSpace(entry.Text))
                continue;

            lines.Add(entry.Kind switch
            {
                HexAgentEntryKind.User => "> " + (entry.AskedText ?? entry.Text),
                HexAgentEntryKind.Tool => $"[{entry.ToolName} {entry.State.ToString().ToLowerInvariant()}] {ToolPreview(entry.Input)}",
                HexAgentEntryKind.Thinking => "(thinking) " + entry.Text,
                HexAgentEntryKind.Error => "ERROR " + entry.Text,
                _ => entry.Text
            });
        }

        string all = string.Join("\n", lines);
        yield return McpToolResult.Text($"{head}\n{(all.Length > most ? "…" + all[^most..] : all)}");
    }

    private IEnumerable<McpToolResult?> AgentSend(JsonObject args)
    {
        int? caller = CallerOf(args);
        string text = args.String("text") ?? string.Empty;
        if (ChatById(args.Integer("id") ?? -1) is not { } chat)
        {
            yield return McpToolResult.Error("There's no such chat, hex_agent_list says which there are.");
            yield break;
        }

        if (chat.Id == caller)
        {
            yield return McpToolResult.Error("That's your own chat.");
            yield break;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            yield return McpToolResult.Error("Say something.");
            yield break;
        }

        // Working, it's an instruction, taken in at once by an agent that can be told more as it goes and kept in the
        // inbox of one that can't, starting its next turn if it isn't read by then
        if (chat.Running)
        {
            yield return McpToolResult.Text(Deliver(chat, caller is { } from ? ChatById(from) : null, "instruction", text, directive: true));
            yield break;
        }

        if (!extensions.AgentRuns.TryGetValue(chat.Agent, out var run))
        {
            yield return McpToolResult.Error($"#{chat.Id}'s agent system isn't there any more.");
            yield break;
        }

        string was = StateWord(chat);
        StartAgent(chat, run.Extension, run.Run, text, text);
        yield return McpToolResult.Text(was is "stopped" or "failed"
            ? $"#{chat.Id} had {was}, it's going again from where it was. hex_agent_wait waits for it."
            : $"#{chat.Id} is on it. hex_agent_wait waits for it.");
    }

    private IEnumerable<McpToolResult?> AgentStopTool(JsonObject args)
    {
        if (ChatById(args.Integer("id") ?? -1) is not { } chat)
        {
            yield return McpToolResult.Error("There's no such chat, hex_agent_list says which there are.");
            yield break;
        }

        if (chat.Id == CallerOf(args))
        {
            yield return McpToolResult.Error("That's your own chat.");
            yield break;
        }

        bool was = chat.Running;
        StopAgent(chat);
        yield return McpToolResult.Text(was ? $"Stopped #{chat.Id}." : $"#{chat.Id} wasn't running.");
    }

    /// <summary>
    /// A chat an agent started deleted, with the chats it started, or with clear emptied to start afresh. Only by the agent
    /// that started it, or a client that isn't an agent of the panel (the person's own tools).
    /// </summary>
    private IEnumerable<McpToolResult?> AgentClose(JsonObject args)
    {
        if (ChatById(args.Integer("id") ?? -1) is not { } chat)
        {
            yield return McpToolResult.Error("There's no such chat, hex_agent_list says which there are.");
            yield break;
        }

        int? caller = CallerOf(args);
        if (caller is { } asking && chat.Parent != asking)
        {
            yield return McpToolResult.Error(chat.Id == asking
                ? "That's your own chat."
                : $"#{chat.Id} wasn't started by you, only the agent that started it closes or clears it.");
            yield break;
        }

        if (args["clear"]?.GetValue<bool>() != true)
        {
            int family = 1 + agentChats.Count(each => IsUnder(each, chat));
            CloseAgentChat(chat);
            yield return McpToolResult.Text($"Deleted #{chat.Id}{(family > 1 ? $" and the {family - 1} it started" : string.Empty)}.");
            yield break;
        }

        // Emptied, it has to have stopped, what a working agent printed next would land in the chat that was cleared
        if (chat.Running || agentRuns.Any(run => run.Chat == chat))
        {
            yield return McpToolResult.Error($"#{chat.Id} is working, hex_agent_stop it first and clear it once it has stopped.");
            yield break;
        }

        // Its older rows go with them, the family's log of what was said between them stays the family's record
        hub.History?.Delete(chat.Key);
        chat.Clear();
        if (agentChat == chat)
            agentRowsFor = default;
        yield return McpToolResult.Text($"Cleared #{chat.Id}, its rows, mail and session gone. hex_agent_send starts it afresh.");
    }

    // Whether a chat is under another, started by it or by one it started
    private bool IsUnder(HexAgentChat chat, HexAgentChat top)
    {
        for (int guard = 0; chat.Parent is { } up && ChatById(up) is { } parent && guard < 16; guard++)
        {
            if (parent == top)
                return true;
            chat = parent;
        }

        return false;
    }
}
