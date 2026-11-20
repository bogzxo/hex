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
        if (HandleAgentCommand(asked))
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

        bool slash = IsAgentSlash(asked);
        string prompt = slash ? asked : AgentDraftPrompt(asked);
        string? location = !slash && agentDraftContext is { } context
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

    /// <summary>
    /// Whether a chat's agent is starting or has a run not taken off agentRuns yet, whatever its turn says. An exited
    /// process counts until TakeAgentLines has read its last lines and removed it, so a new turn can't start while the old
    /// run's Done is still to come and land on it. What is asked meanwhile waits in its mail and AfterAgentTurn starts it.
    /// </summary>
    private bool AgentRunBusy(HexAgentChat chat) => agentsStarting.Contains(chat) || agentRuns.Any(each => each.Chat == chat);

    private void StartAgent(HexAgentChat chat, HexExtension extension, ObjectValue run, string prompt, string asked, string? contextLocation = null)
    {
        // Its turn said over, the agent can still be going of its own (Claude Code woken by a task of its own in the
        // background), and a second run on the same session had two agents writing over each other. What's asked waits in
        // its inbox and starts the next run once that one has gone (AfterAgentTurn)
        if (AgentRunBusy(chat))
        {
            string said = prompt.Length > 0 ? prompt : asked;
            var message = new HexAgentMail(hub.NextMailSeq(), null, FROM_PERSON, "instruction", said, Directive: true) { To = chat.Id };
            chat.Post(message);
            hub.MailOf(chat).Add(message, HexMailRoute.Inbox);
            chat.Note(Said("agents_panel.after_run", ("asked", OneLine(asked, 80))));
            return;
        }

        // What other agents told it while it wasn't working goes in front of what it's asked, so nothing sent is lost
        // Provider commands must remain at the start; unread mail waits for the next ordinary prompt.
        if (!IsAgentSlash(prompt))
        {
            prompt = PlanPrompt(chat, UnreadPrompt(chat, prompt));
            // What the person changed of its plan in the Plan tab goes in front too, so the agent keeps their changes
            if (TakePlanTold(chat) is { } told)
                prompt = $"{told}\n\n{prompt}";
        }
        // The working copy as it was before the turn, for its changes to be seen and put back (Agentic's Changes). The
        // agent starts once it's taken, started alongside it its first writes could be in what was taken as before
        HexAgentEntry turn = chat.Ask(prompt, asked, contextLocation is null ? null : asked, contextLocation);
        agentsStarting.Add(chat);
        TakeCheckpoint(turn, () =>
        {
            // Stopped while the checkpoint was taken, it isn't started at all. Left in agentsStarting through the launch,
            // which is off the editor's thread in two steps (the program looked for, the program started), so the chat is
            // busy to the end and a stop in between is seen at either
            if (agentsStarting.Contains(chat))
                LaunchAgent(chat, extension, run, prompt);
        });
    }

    /// <summary>
    /// What a chat's agent is asked with where its plan stands in front, when it has steps left: the step going on, the
    /// next and any blocked, by ID. A go ahead in a later turn, a turn after a restart or a compaction, otherwise starts with
    /// nothing saying the tab's rows are there to move on. Nothing for a chat without a plan or with every step closed.
    /// </summary>
    internal static string PlanPrompt(HexAgentChat chat, string prompt)
    {
        if (chat.Plan is not { Unfinished: true } plan || prompt.Length == 0)
            return prompt;

        static string Row(HexPlanItem item) => $"{item.Id ?? "?"} \"{OneLine(item.Text, 80)}\"";
        var parts = new List<string>();
        if (plan.Doing is { } doing)
            parts.Add("doing " + Row(doing));
        if (plan.Items.FirstOrDefault(item => item.State == HexPlanState.Pending) is { } next)
            parts.Add("next " + Row(next));
        HexPlanItem[] blocked = [.. plan.Items.Where(item => item.State == HexPlanState.Blocked).Take(3)];
        if (blocked.Length > 0)
            parts.Add("blocked " + string.Join(", ", blocked.Select(Row)));
        return $"[Your Plan tab: {plan.Done} of {plan.Items.Count} done, {string.Join("; ", parts)}. If this message carries that work on, "
            + "move those rows by ID with hex_plan_update as steps start, finish or block, hex_plan_read has them all.]\n\n" + prompt;
    }

    // The chats whose turn has begun and whose agent waits for its checkpoint before it's started
    private readonly HashSet<HexAgentChat> agentsStarting = [];

    /// <summary>A chat's agent started on what it was asked, the row of it already in the chat.</summary>
    private void LaunchAgent(HexAgentChat chat, HexExtension extension, ObjectValue run, string prompt)
    {
        // Looking for the program is a walk of every folder on PATH (and the system's, when it isn't on Hex's), a disk or a
        // network share that answers slowly held the editor still through it
        string program = HexExtensions.Text(run, "program") ?? string.Empty;
        InBackground<string?>("find agent", () => HexAgentProcess.Find(program), found =>
        {
            // Stopped while it was looked for, StopAgent has ended the turn
            if (!agentsStarting.Contains(chat))
                return;

            if (found is null)
            {
                agentsStarting.Remove(chat);
                chat.Error(Said("agents_panel.not_found", ("program", program), ("install", HexExtensions.Text(run, "install") ?? string.Empty)));
                chat.Done();
                return;
            }

            ContinueLaunch(chat, extension, run, prompt, program, found);
        }, quiet: true);
    }

    // The launch from the program found, the extension's words for it made on the editor's thread (a script isn't run
    // anywhere else) and the program started off it
    private void ContinueLaunch(HexAgentChat chat, HexExtension extension, ObjectValue run, string prompt, string program, string found)
    {
        // The agent reaches Hex's tools through the server, which is up from now on in this run
        if (mcpHost is null && StartMcp())
            chat.Note(Said("agents_panel.mcp_started"));

        ObjectValue chatObject = ChatObject(chat, extension);
        List<string> arguments = [];
        if (HexExtensions.FunctionOf(run, "arguments") is { } making)
        {
            if (!extension.Invoke(making, [new StringValue(prompt), chatObject], out IRuntimeValue made))
            {
                agentsStarting.Remove(chat);
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
                agentsStarting.Remove(chat);
                chat.Error($"{extension.Name}, {extension.Failed}");
                chat.Done();
                return;
            }

            input = first + "\n";
        }

        string folder = explorer.workspace.Folders.FirstOrDefault() ?? Directory.GetCurrentDirectory();

        // Starting it makes a process and writes the prompt down its stdin, which waits when the program is slow to take it
        // (a scanner looking it over first, a prompt bigger than the pipe), so it's off the editor's thread. What went wrong
        // comes back as the exception, nothing thrown on the pool
        InBackground<(HexAgentProcess? Process, Exception? Failed)>("start agent", () =>
        {
            try
            {
                return (Process: HexAgentProcess.Start(found, arguments, folder, input, keepInput: steers), Failed: (Exception?)null);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                return (Process: null, Failed: e);
            }
        }, started =>
        {
            // Stopped while it was being started, what was started goes with it
            if (!agentsStarting.Remove(chat))
            {
                started.Process?.Dispose();
                return;
            }

            if (started.Process is null)
            {
                chat.Error(Said("agents_panel.failed_start", ("program", program), ("why", started.Failed!.Message)));
                chat.Done();
                return;
            }

            agentRuns.Add(new AgentRun(chat, started.Process, extension, run, chatObject, program, steers));
        }, quiet: true);
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
    private bool SteerAgent(HexAgentChat chat, string asked, bool typed = true)
    {
        // Somebody's word to an agent sitting in a long wait for its agents, or to one that can't be told more as it
        // works, goes by the mail. Said on its stdin it would only be read once the wait was over, minutes later, or never
        if (typed && chat.Running && (AgentWaiting(chat) || SteeredRun(chat) is null))
        {
            TellWorkingAgent(chat, asked);
            return true;
        }

        if (SteeredRun(chat) is not { } run)
            return false;

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
            return true;
        }

        if (line is not null && run.Process.Send(line))
            return true;

        // Another agent's message that couldn't be written stays in its mail to be read later, so its row is taken back
        // rather than saying it was never heard
        if (!typed)
        {
            chat.Withdraw(entry);
            return false;
        }

        entry.Queued = false;
        entry.Unheard = true;
        chat.Touch(entry);
        return false;
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
    /// Stops a chat's agent. One whose run says how (run's stop, Claude Code's interrupt, Codex's turn/interrupt) is told to
    /// stop its turn, its session left whole to go on from, and killed only if it hasn't within a few seconds. Any other is
    /// killed. The agents it started carry on unless <paramref name="withStarted"/>: the person stopping a chat's turn is
    /// most often stopping it to say something, after which it goes on waiting for them, and its background work was all
    /// stopped with it, an hour of three agents' work lost to a press of Stop to add a sentence.
    /// </summary>
    /// <param name="withStarted">Whether the agents it started (and theirs) stop too, its menu's Stop with its agents, an agent's hex_agent_stop and closing it.</param>
    private void StopAgent(HexAgentChat? chat, bool withStarted = false)
    {
        if (chat is null)
            return;

        // What was held for it goes with the run, kept it went to the next run's agent as soon as it said anything
        heldSteers.RemoveAll(held => held.Chat == chat);

        // One still waiting for its checkpoint is never started, its turn over as it is
        if (agentsStarting.Remove(chat))
            chat.Done();

        foreach (AgentRun run in agentRuns.Where(run => run.Chat == chat).ToList())
        {
            if (!agentsStopping.ContainsKey(chat) && run.Chat.Running && run.Process.TakesInput && StopLine(run) is { Length: > 0 } line && run.Process.Send(line))
            {
                agentsStopping[chat] = System.Environment.TickCount64 + AGENT_STOP_MS;
                continue;
            }

            KillAgentRun(run);
        }

        List<HexAgentChat> running = [.. agentChats.Where(each => each.Parent == chat.Id && each.Running)];
        if (running.Count == 0)
            return;

        if (withStarted)
        {
            foreach (HexAgentChat started in running)
                StopAgent(started, withStarted: true);
            return;
        }

        // Said in the chat, so the person knows they're still going and where to stop them
        chat.Note(Said("agents_panel.started_carry_on", ("chats", string.Join(", ", running.Select(started => "#" + started.Id)))));
    }

    /// <summary>Whether a chat has agents it started still running, for its menu's Stop with its agents.</summary>
    private bool HasRunningStarted(HexAgentChat chat)
    {
        foreach (HexAgentChat each in agentChats)
        {
            if (each.Parent == chat.Id && each.Running)
                return true;
        }
        return false;
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
        TickCompactAfter();
        TickResumeAfterLimits();
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
