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

    /// <summary>A chat as the tools see it off the editor's thread, as it was the last tick.</summary>
    private sealed record AgentStatus(int Id, string Shown, string Agent, string? Model, int? Parent, int Depth, string State,
        string Reply, double Seconds, double Cost, int Version);

    // What the chats were the last tick, for hex_agent_wait off the editor's thread, and what they were made from
    private volatile IReadOnlyDictionary<int, AgentStatus> agentStatuses = new Dictionary<int, AgentStatus>();
    private long agentStatusesFrom = -1;

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
                ("options", new JsonObject { ["type"] = "object", ["description"] = "Other options of the agent system by id, as hex_agent_list says them." })], "prompt")),
            AgentSpawn),
        new(new McpTool("hex_agent_wait", "Wait for agents",
            "Wait until agents finish their turn and get what they said. By default every agent you started that's running (every running chat when you aren't a chat of the panel), until all of them are done. any returns when the first is. A timeout says what's still running.",
            McpSchema.Object([
                ("ids", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" }, ["description"] = "The chats to wait for, those you started that are running by default." }),
                ("mode", McpSchema.OneOf("all (the default) or any.", "all", "any")),
                ("timeout", McpSchema.Number($"Seconds, {AGENTS_WAIT} by default, {AGENTS_WAIT_MOST} at the most."))])),
            null, RunAway: AgentWait),
        new(new McpTool("hex_agent_read", "Read an agent's chat",
            "What a chat's agent said in its last turn and its state. full lists the whole chat, what was asked, said and every tool called, the latest kept when it's long.",
            McpSchema.Object([
                ("id", McpSchema.Integer("The chat.")),
                ("full", McpSchema.Flag("The whole chat rather than the last reply.")),
                ("most", McpSchema.Integer("The most characters, 8000 by default."))], "id")), AgentRead),
        new(new McpTool("hex_agent_send", "Tell an agent more",
            "Say something to a chat's agent. A finished one starts another turn with it, going on from where it was. A running one that can be steered takes it in as it works, one that can't says so, wait for it first.",
            McpSchema.Object([("id", McpSchema.Integer("The chat.")), ("text", McpSchema.String("What to say."))], "id", "text")), AgentSend),
        new(new McpTool("hex_agent_stop", "Stop an agent",
            "Stop a chat's agent and everything it started. What it said so far stays in its chat.",
            McpSchema.Object([("id", McpSchema.Integer("The chat."))], "id")), AgentStopTool)
    ];

    /// <summary>The chat a call came from, when it's an agent of the panel's.</summary>
    private static int? CallerOf(JsonObject args) => args.Integer(HexMcpCaller.CALLER);

    private HexAgentChat? ChatById(int id) => agentChats.FirstOrDefault(chat => chat.Id == id);

    // A chat's state in a word
    private static string StateWord(HexAgentChat chat) =>
        chat.Running ? "running" : chat.Stopped ? "stopped" : chat.Failed ? "failed" : chat.Entries.Count == 0 ? "new" : "done";

    /// <summary>
    /// What the chats are now, made again for the tools that wait off the editor's thread whenever one of them moved.
    /// Every tick, a sum of their versions when nothing did.
    /// </summary>
    private void PublishAgentStatuses()
    {
        long from = agentChats.Count;
        foreach (HexAgentChat chat in agentChats)
            from = from * 31 + chat.Version * 2 + (chat.Running ? 1 : 0) + chat.Id * 7919L;
        if (from == agentStatusesFrom)
            return;

        agentStatusesFrom = from;
        agentStatuses = agentChats.ToDictionary(chat => chat.Id, chat => new AgentStatus(chat.Id, chat.Shown, chat.Agent,
            chat.Options.GetValueOrDefault("model") ?? chat.Model, chat.Parent, chat.Depth, StateWord(chat),
            chat.Running ? string.Empty : chat.LastReply(AGENTS_REPLY), chat.TurnSeconds, chat.Cost, chat.Version));
    }

    // A chat in a line, for the lists
    private static string StatusLine(AgentStatus status) =>
        $"#{status.Id} \"{status.Shown}\" {status.Agent}{(status.Model is { Length: > 0 } model ? " " + model : string.Empty)} {status.State}"
        + (status.Seconds > 0 ? $" {status.Seconds.ToString("0.#", CultureInfo.InvariantCulture)}s" : string.Empty)
        + (status.Cost > 0 ? $" ${status.Cost.ToString("0.000", CultureInfo.InvariantCulture)}" : string.Empty)
        + (status.Parent is { } parent ? $" (started by #{parent})" : string.Empty);

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
        foreach (AgentStatus status in agentStatuses.Values.OrderBy(status => status.Id))
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

        agentChats.Add(chat);

        // It's told what it is, the one that started it reads its last message
        string from = parent is null ? "another agent" : $"chat #{parent.Id}";
        string told = $"[You're a background agent in Hex, chat #{chat.Id}, started by {from}. It doesn't see this chat, only your last message, so end with what you found or did, complete in itself.]\n\n{prompt}";
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
        double timeout = Math.Clamp(args.Number("timeout") ?? AGENTS_WAIT, 1, AGENTS_WAIT_MOST);
        long deadline = System.Environment.TickCount64 + (long)(timeout * 1000);

        IReadOnlyDictionary<int, AgentStatus> now = agentStatuses;
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
            AgentStatus[] mine = [.. now.Values.Where(status => caller is null || status.Parent == caller)];
            ids = [.. (mine.Any(status => status.State == "running") ? mine.Where(status => status.State == "running") : mine).Select(status => status.Id)];
            if (ids.Length == 0)
                return McpToolResult.Error(caller is null ? "There are no chats to wait for, hex_agent_spawn starts one." : "You haven't started any agents to wait for, hex_agent_spawn starts one.");
        }

        while (true)
        {
            now = agentStatuses;
            AgentStatus[] those = [.. ids.Select(id => now.GetValueOrDefault(id)).OfType<AgentStatus>()];
            int done = those.Count(status => status.State != "running");
            bool over = any ? done > 0 : done == those.Length;
            bool late = System.Environment.TickCount64 >= deadline;
            if (over || late)
                return McpToolResult.Text(WaitReport(those, late && !over));

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

    // What the agents waited for said, the finished ones with their replies and the rest by their state
    private static string WaitReport(AgentStatus[] those, bool late)
    {
        var said = new StringBuilder();
        if (late)
            said.Append("Timed out, ").Append(those.Count(status => status.State == "running")).Append(" still running. hex_agent_wait again to go on waiting.\n\n");
        foreach (AgentStatus status in those.OrderBy(status => status.State == "running"))
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

        if (chat.Running)
        {
            if (SteeredRun(chat) is null)
            {
                yield return McpToolResult.Error($"#{chat.Id} is working and its agent can't be told more as it goes. hex_agent_wait for it, then send.");
                yield break;
            }

            SteerAgent(chat, text, typed: false);
            yield return McpToolResult.Text($"Sent to #{chat.Id}, it takes it in as it works.");
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
}
