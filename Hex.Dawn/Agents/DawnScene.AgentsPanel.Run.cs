using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

// An agent run for a turn of a chat in the Agents page. Its extension says the program, the arguments for a prompt and
// what each line it prints means (hex.agent's run), Hex starts it in the workspace with the prompt on its stdin and hands
// every JSON line to the extension's on_event with a chat object to say what happened on, on the editor's thread. Hex's
// MCP server is put up for it first so it has Hex's tools, somebody pressed Send after all.
internal sealed partial class DawnScene
{
    // The most lines of an agent taken a tick, a flood of output still leaves the editor its frames
    private const int AGENTS_LINES_A_TICK = 200;

    // Steers, its run says how a message is written to its stdin (run's message), which is kept open while the turn goes
    // on so what's asked as it works goes in as well, for it to take in as it can
    private sealed record AgentRun(HexAgentChat Chat, HexAgentProcess Process, HexExtension Extension, ObjectValue Run, ObjectValue ChatObject, string Program, bool Steers)
    {
        public bool SaidWrong { get; set; }

        // Whether the agent has said it took in what it was started with, the messages it says it took after that being
        // the ones asked as it worked
        public bool HeardStart { get; set; }
    }

    private readonly List<AgentRun> agentRuns = [];

    /// <summary>What's in the box sent to the agent of the chat on screen, a new chat made for it when there isn't one.</summary>
    private void SendToAgent()
    {
        string asked = agentsInput.Text.Trim();
        if (asked.Length == 0 && agentImages.Count == 0)
            return;

        if (agentChat?.Running == true)
        {
            SteerAgent(agentChat, asked);
            return;
        }

        if (AgentFor(agentChat) is not { } agent || !extensions.AgentRuns.TryGetValue(agent, out var run))
        {
            Say(Said("agents_panel.no_agents"));
            return;
        }

        if (!CanComposeAgent())
        {
            Say(Said("agents_panel.no_agents"));
            return;
        }

        if (agentChat is null)
        {
            agentChat = new HexAgentChat(++agentChatsMade, agent);
            agentChats.Add(agentChat);
        }

        string prompt = AgentDraftPrompt(asked);
        string? location = agentDraftContext is { } context
            ? $"{context.Path}:{context.Line}:{context.Column}–{context.EndLine}:{context.EndColumn}" : null;
        agentsInput.Text = string.Empty;
        RememberAsked(asked);
        agentDraftContext = null;

        // The pictures pasted in go with it, for its extension to hand over while the turn is being started
        HexAgentChat sending = agentChat;
        sending.Attached = TakeAgentImages(ref prompt, ref asked);
        try
        {
            StartAgent(sending, run.Extension, run.Run, prompt, asked, location);
        }
        finally
        {
            sending.Attached = [];
        }
    }

    /// <summary>
    /// What was asked with the file on screen in front of it, its path and the caret's line, and the lines selected when
    /// there are some. The card shows what was asked and says the file under it.
    /// </summary>
    private string WithFile(string asked)
    {
        if (document?.Path is not { } path || !document.IsText)
            return asked;

        var text = code.Document;
        string where = $"{HexSearch.RelativeTo(explorer.workspace.Folders, path)}:{text.Caret.Line + 1}";
        string selected = text.HasSelection && text.SelectedText.Length < 20_000 ? $"\nSelected, lines {text.SelectionStart.Line + 1} to {text.SelectionEnd.Line + 1}:\n```\n{text.SelectedText}\n```" : string.Empty;
        return $"[from {where}] The file open in the editor is {where}.{selected}\n\n---\n{asked}";
    }

    private void StartAgent(HexAgentChat chat, HexExtension extension, ObjectValue run, string prompt, string asked, string? contextLocation = null)
    {
        // What other agents told it while it wasn't working goes in front of what it's asked, so nothing sent is lost
        prompt = UnreadPrompt(chat, prompt);
        chat.Ask(prompt, asked, contextLocation is null ? null : asked, contextLocation);
        string program = HexExtensions.Text(run, "program") ?? string.Empty;
        string? found = HexAgentProcess.Find(program);
        if (found is null)
        {
            chat.Error(Said("agents_panel.not_found", ("program", program), ("install", HexExtensions.Text(run, "install") ?? string.Empty)));
            chat.Done();
            return;
        }

        // The agent reaches Hex's tools through the server, which is up from now on in this run
        if (mcpHost is null && StartMcp())
            chat.Note(Said("agents_panel.mcp_started"));

        ObjectValue chatObject = ChatObject(chat, extension);
        List<string> arguments = [];
        if (HexExtensions.FunctionOf(run, "arguments") is { } making)
        {
            if (!extension.Invoke(making, [new StringValue(prompt), chatObject], out IRuntimeValue made))
            {
                chat.Error($"{extension.Name}, {extension.Failed}");
                chat.Done();
                return;
            }

            if (made is ListValue list)
                arguments = [.. list.Items.Select(Values.Text)];
        }

        bool onStdin = !(run.Properties.TryGetValue("stdin", out IRuntimeValue? stdin) && stdin is BooleanValue { Value: false });
        string? input = onStdin ? prompt : null;

        // One that steers is asked in its own words from the start, its stdin left open for more
        bool steers = HexExtensions.FunctionOf(run, "message") is not null;
        if (steers)
        {
            if (MessageLine(extension, run, prompt, chatObject) is not { } first)
            {
                chat.Error($"{extension.Name}, {extension.Failed}");
                chat.Done();
                return;
            }

            input = first + "\n";
        }

        string folder = explorer.workspace.Folders.FirstOrDefault() ?? Directory.GetCurrentDirectory();
        try
        {
            HexAgentProcess process = HexAgentProcess.Start(found, arguments, folder, input, keepInput: steers);
            agentRuns.Add(new AgentRun(chat, process, extension, run, chatObject, program, steers));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            chat.Error(Said("agents_panel.failed_start", ("program", program), ("why", e.Message)));
            chat.Done();
        }
    }

    /// <summary>A message as its run's message writes it for the agent's stdin, one line, null when the extension failed.</summary>
    private static string? MessageLine(HexExtension extension, ObjectValue run, string text, ObjectValue chatObject)
    {
        if (HexExtensions.FunctionOf(run, "message") is not { } message
            || !extension.Invoke(message, [new StringValue(text), chatObject], out IRuntimeValue made))
            return null;

        // A list is several lines, for an agent that's spoken to in steps (Codex's app-server, its handshake and its thread
        // before the turn), each a line of its own
        static string LineOf(IRuntimeValue value) =>
            (value is StringValue said ? said.Value : HexJson.FromHidl(value)?.ToJsonString() ?? string.Empty).ReplaceLineEndings(" ");
        return made is ListValue lines ? string.Join("\n", lines.Items.Select(LineOf).Where(line => line.Length > 0)) : LineOf(made);
    }

    /// <summary>The run of a chat that can be told more while it works, null when there's none.</summary>
    private AgentRun? SteeredRun(HexAgentChat? chat) =>
        chat is { Running: true } ? agentRuns.FirstOrDefault(run => run.Chat == chat && run.Steers && run.Process.TakesInput) : null;

    /// <summary>
    /// What's in the box said to the agent while it works, queued on the chat until the agent takes it in. Nothing when
    /// the agent can't be told more as it goes, the box kept as it is for when it's done. Not <paramref name="typed"/>,
    /// it's another agent saying it (hex_agent_send), and the box and the file on screen are nothing to do with it.
    /// </summary>
    private void SteerAgent(HexAgentChat chat, string asked, bool typed = true)
    {
        // Somebody's word to an agent sitting in a long wait for its agents, or to one that can't be told more as it
        // works, goes by the mail. Said on its stdin it would only be read once the wait was over, minutes later, or never
        if (typed && chat.Running && (AgentWaiting(chat) || SteeredRun(chat) is null))
        {
            TellWorkingAgent(chat, asked);
            return;
        }

        if (SteeredRun(chat) is not { } run)
            return;

        string prompt = asked;
        string? location = null;
        if (typed)
        {
            prompt = AgentDraftPrompt(asked);
            location = agentDraftContext is { } context
                ? $"{context.Path}:{context.Line}:{context.Column}–{context.EndLine}:{context.EndColumn}" : null;
            agentsInput.Text = string.Empty;
            RememberAsked(asked);
            agentDraftContext = null;
            chat.Attached = TakeAgentImages(ref prompt, ref asked);
        }

        HexAgentEntry entry = chat.Steer(prompt, location is null && chat.Attached.Count == 0 ? null : asked, location);
        string? line = MessageLine(run.Extension, run.Run, prompt, run.ChatObject);
        chat.Attached = [];

        // What somebody typed is held by Hex until the agent next says something, where it would be taken in anyway, so
        // it can be put back in the box and changed meanwhile. Written to the agent's stdin there was no taking it back
        if (line is not null && typed)
        {
            heldSteers.Add((chat, entry, line, asked));
            return;
        }

        if (line is not null && run.Process.Send(line))
            return;

        entry.Queued = false;
        entry.Unheard = true;
        chat.Touch(entry);
    }

    /// <summary>
    /// What's in the box sent to a working agent as an instruction from the person watching. It wakes a wait for its
    /// agents at once, or waits in its inbox and starts its next turn when it isn't read before this one ends. The row of
    /// it stands in the chat where it was said, as anything asked does.
    /// </summary>
    private void TellWorkingAgent(HexAgentChat chat, string asked)
    {
        string prompt = AgentDraftPrompt(asked);
        agentsInput.Text = string.Empty;
        RememberAsked(asked);
        agentDraftContext = null;
        // By the mail a picture is its path, which the agent reads when it takes the message in
        TakeAgentImages(ref prompt, ref asked);
        HexAgentEntry entry = chat.Steer(prompt);
        entry.Queued = false;
        chat.Touch(entry);
        string said = DeliverFromPerson(chat, prompt);
        chat.Note(said);
    }

    /// <summary>
    /// The extension saying the agent's turn is over. Something asked since that the agent hasn't taken in yet starts
    /// it working again, so the turn goes on. Otherwise it's over, and the agent's stdin closed so it finishes.
    /// </summary>
    private void FinishAgentTurn(HexAgentChat chat)
    {
        // What's held for it goes now, its turn going on with it rather than ending with it unsaid
        FlushSteers(chat);
        if (chat.HasQueued && SteeredRun(chat) is not null)
            return;

        chat.Done();
        foreach (AgentRun run in agentRuns.Where(run => run.Chat == chat))
            run.Process.CloseInput();
    }

    // How long an agent told to stop has to end its turn itself before it's killed
    private const long AGENT_STOP_MS = 5000;

    // The chats whose agents were told to stop and when they're killed if they haven't by then
    private readonly Dictionary<HexAgentChat, long> agentsStopping = [];

    /// <summary>
    /// Stops a chat's agent and everything it started. One whose run says how (run's stop, Claude Code's interrupt, Codex's
    /// turn/interrupt) is told to stop its turn, its session left whole to go on from, and killed only if it hasn't
    /// within a few seconds. Any other is killed.
    /// </summary>
    private void StopAgent(HexAgentChat? chat)
    {
        if (chat is null)
            return;

        foreach (AgentRun run in agentRuns.Where(run => run.Chat == chat).ToList())
        {
            if (!agentsStopping.ContainsKey(chat) && run.Chat.Running && run.Process.TakesInput && StopLine(run) is { Length: > 0 } line && run.Process.Send(line))
            {
                agentsStopping[chat] = System.Environment.TickCount64 + AGENT_STOP_MS;
                continue;
            }

            KillAgentRun(run);
        }

        // The agents it started go with it, nobody's waiting for them now
        foreach (HexAgentChat started in agentChats.Where(each => each.Parent == chat.Id && each.Running).ToList())
            StopAgent(started);
    }

    /// <summary>A run's agent killed and its chat said to be stopped.</summary>
    private void KillAgentRun(AgentRun run)
    {
        run.Process.Dispose();
        agentRuns.Remove(run);
        agentsStopping.Remove(run.Chat);
        run.Chat.Note(Said("agents_panel.stopped"));
        run.Chat.Done();
        run.Chat.Stopped = true;
    }

    /// <summary>What a run's extension writes to stop its agent's turn, null when it says nothing (and it's killed).</summary>
    private static string? StopLine(AgentRun run)
    {
        if (HexExtensions.FunctionOf(run.Run, "stop") is not { } stop || !run.Extension.Invoke(stop, [run.ChatObject], out IRuntimeValue made) || made is NullValue)
            return null;
        return made is StringValue said ? said.Value : HexJson.FromHidl(made)?.ToJsonString().ReplaceLineEndings(" ");
    }

    /// <summary>
    /// Every tick, the agents told to stop. One whose turn ended is stopped, its process kept for the next message, one
    /// that went on past its time killed.
    /// </summary>
    private void TickStopping()
    {
        if (agentsStopping.Count == 0)
            return;
        long now = System.Environment.TickCount64;
        foreach (var (chat, deadline) in agentsStopping.ToArray())
        {
            if (!chat.Running)
            {
                agentsStopping.Remove(chat);
                chat.Note(Said("agents_panel.stopped"));
                chat.Stopped = true;
            }
            else if (now >= deadline)
            {
                foreach (AgentRun run in agentRuns.Where(run => run.Chat == chat).ToList())
                    KillAgentRun(run);
                agentsStopping.Remove(chat);
            }
        }
    }

    /// <summary>
    /// The lines the agents printed since the last tick, each that's JSON handed to its extension's on_event, the rest
    /// put in as notes. An agent that's gone with all of its lines taken ends its turn.
    /// </summary>
    private void TakeAgentLines()
    {
        TickStopping();
        for (int r = agentRuns.Count - 1; r >= 0; r--)
        {
            AgentRun run = agentRuns[r];
            bool exited = run.Process.Exited;

            // The agent said something, so it's between steps, and what was held for it goes in now
            if (!run.Process.Lines.IsEmpty)
                FlushSteers(run.Chat);
            for (int taken = 0; taken < AGENTS_LINES_A_TICK && run.Process.Lines.TryDequeue(out var line); taken++)
                TakeAgentLine(run, line.Line, line.Error);

            if (!exited || !run.Process.Lines.IsEmpty)
                continue;

            int code = run.Process.ExitCode ?? -1;
            if (code != 0 && !run.SaidWrong)
                run.Chat.Error(Said("agents_panel.exited", ("program", run.Program), ("code", code.ToString())));
            run.Chat.Done();
            run.Process.Dispose();
            agentRuns.RemoveAt(r);
            if (!agentRuns.Any(other => other.Chat == run.Chat))
                AfterAgentTurn(run.Chat);
        }
    }

    private void TakeAgentLine(AgentRun run, string line, bool error)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        if (error)
        {
            run.Chat.Note(line.TrimEnd());
            return;
        }

        if (HexJson.Parse(line) is not { } parsed || HexExtensions.FunctionOf(run.Run, "on_event") is not { } heard)
        {
            run.Chat.Note(line.TrimEnd());
            return;
        }

        if (!run.Extension.Invoke(heard, [run.ChatObject, parsed]))
        {
            run.Chat.Error($"{run.Extension.Name} couldn't read what its agent said, {run.Extension.Failed}");
            run.SaidWrong = true;
            extensions.Failed(run.Extension);
            run.Process.Dispose();
        }
    }

    /// <summary>What an extension says what its agent did on, a chat's rows, a turn that went wrong marked so on its run.</summary>
    private ObjectValue ChatObject(HexAgentChat chat, HexExtension extension) =>
        HexAgentChatApi.Of(chat, explorer.workspace.Folders.FirstOrDefault() ?? Directory.GetCurrentDirectory(), () =>
        {
            foreach (AgentRun run in agentRuns.Where(run => run.Chat == chat))
                run.SaidWrong = true;
        }, () => FinishAgentTurn(chat), () => TakeAgentMessage(chat),
            // A line for the agent's stdin from its extension, an answer to something it asked (Codex's approvals) or the
            // next step of a conversation it holds with the agent (a turn started once its thread is)
            line => agentRuns.FirstOrDefault(run => run.Chat == chat && run.Process.TakesInput)?.Process.Send(line) == true,
            // An approval's answer handed back to the extension's own function
            (function, arguments) => extension.Invoke(function, arguments, out _));

    /// <summary>The agent saying it took in a message, the first of a run being what it was started with.</summary>
    private void TakeAgentMessage(HexAgentChat chat)
    {
        if (agentRuns.FirstOrDefault(run => run.Chat == chat && run.Steers) is { HeardStart: false } run)
        {
            run.HeardStart = true;
            return;
        }

        chat.Taken();
    }
}
