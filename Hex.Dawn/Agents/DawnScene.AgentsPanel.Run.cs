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

    private sealed record AgentRun(HexAgentChat Chat, HexAgentProcess Process, HexExtension Extension, ObjectValue Run, ObjectValue ChatObject, string Program)
    {
        public bool SaidWrong { get; set; }
    }

    private readonly List<AgentRun> agentRuns = [];

    /// <summary>What's in the box sent to the agent of the chat on screen, a new chat made for it when there isn't one.</summary>
    private void SendToAgent()
    {
        string asked = agentsInput.Text.Trim();
        if (asked.Length == 0 || agentChat?.Running == true)
            return;

        if (AgentFor(agentChat) is not { } agent || !extensions.AgentRuns.TryGetValue(agent, out var run))
        {
            Say(Said("agents_panel.no_agents"));
            return;
        }

        if (agentChat is null)
        {
            agentChat = new HexAgentChat(++agentChatsMade, agent);
            agentChats.Add(agentChat);
        }

        agentsInput.Text = string.Empty;
        RememberAsked(asked);
        string prompt = agentWithFile ? WithFile(asked) : asked;
        StartAgent(agentChat, run.Extension, run.Run, prompt, asked);
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

    private void StartAgent(HexAgentChat chat, HexExtension extension, ObjectValue run, string prompt, string asked)
    {
        chat.Ask(prompt, asked);
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

        ObjectValue chatObject = ChatObject(chat);
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
        string folder = explorer.workspace.Folders.FirstOrDefault() ?? Directory.GetCurrentDirectory();
        try
        {
            HexAgentProcess process = HexAgentProcess.Start(found, arguments, folder, onStdin ? prompt : null);
            agentRuns.Add(new AgentRun(chat, process, extension, run, chatObject, program));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            chat.Error(Said("agents_panel.failed_start", ("program", program), ("why", e.Message)));
            chat.Done();
        }
    }

    /// <summary>Stops a chat's agent, it and everything it started killed.</summary>
    private void StopAgent(HexAgentChat? chat)
    {
        foreach (AgentRun run in agentRuns.Where(run => run.Chat == chat).ToList())
        {
            run.Process.Dispose();
            agentRuns.Remove(run);
            run.Chat.Note(Said("agents_panel.stopped"));
            run.Chat.Done();
        }
    }

    /// <summary>
    /// The lines the agents printed since the last tick, each that's JSON handed to its extension's on_event, the rest
    /// put in as notes. An agent that's gone with all of its lines taken ends its turn.
    /// </summary>
    private void TakeAgentLines()
    {
        for (int r = agentRuns.Count - 1; r >= 0; r--)
        {
            AgentRun run = agentRuns[r];
            bool exited = run.Process.Exited;
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
    private ObjectValue ChatObject(HexAgentChat chat) =>
        HexAgentChatApi.Of(chat, explorer.workspace.Folders.FirstOrDefault() ?? Directory.GetCurrentDirectory(), () =>
        {
            foreach (AgentRun run in agentRuns.Where(run => run.Chat == chat))
                run.SaidWrong = true;
        });
}
