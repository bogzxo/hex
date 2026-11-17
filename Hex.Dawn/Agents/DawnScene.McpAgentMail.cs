using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

// Agents talking to each other while they work. A chat started by another can tell it what it finds as it goes, ask it
// something or say it's stuck, and the one that started it can steer it, pass on what another found, or answer. So a
// smarter model can sit in the middle, coordinating cheaper ones that explore, implement and test, each kept up to date
// with what the others learn. An agent that can be told more as it works (Claude Code) takes a message in at once,
// one that can't (Codex) finds it in its inbox between steps, and an instruction it never got to starts its next turn.
// Only chats of one family talk, the chat somebody started and everything under it.
internal sealed partial class DawnScene
{
    // The kinds a message can be, instruction being the one a finished agent is started again on
    private static readonly string[] MAIL_KINDS = ["finding", "question", "answer", "progress", "blocked", "instruction"];

    // The chats whose agent is in hex_agent_wait now, how many waits each, written off the editor's thread, kept by the hub
    private System.Collections.Concurrent.ConcurrentDictionary<int, int> agentsWaiting => hub.Waiting;

    /// <summary>Whether a chat's agent is waiting in hex_agent_wait, which a message to it wakes.</summary>
    private bool AgentWaiting(HexAgentChat chat) => hub.IsWaiting(chat);

    private IEnumerable<HexMcpTool> AgentMailTools() =>
    [
        new(new McpTool("hex_agent_message", "Message another agent",
            "Tell another agent of your family something while you both keep working, your parent by default (the chat that started you), or a chat you started, or one your parent started. Say what you found as soon as it matters to the others, ask what you need to know, say when you're blocked, give instructions. An agent that can be told more as it works takes it in at once, the rest find it in hex_agent_inbox, and an instruction starts a finished agent working on it.",
            McpSchema.Object([
                ("text", McpSchema.String("What to tell it, complete in itself, path:line for code.")),
                ("to", McpSchema.Integer("The chat, your parent's when left out.")),
                ("kind", McpSchema.OneOf("What it is, finding by default.", MAIL_KINDS))], "text")), AgentMessage),
        new(new McpTool("hex_agent_inbox", "Read your messages",
            "The messages other agents sent you that you haven't read, who from and what kind, oldest first. Look between steps of a long task, an instruction from the agent that started you changes what you're to do.",
            McpSchema.Empty()), AgentInbox)
    ];

    /// <summary>The chat at the top of a chat's family, the one somebody started.</summary>
    private HexAgentChat RootOf(HexAgentChat chat) => hub.RootOf(chat);

    // A chat as a message says who it's from
    private static string MailName(HexAgentChat chat) =>
        $"#{chat.Id}" + (chat.Name is { Length: > 0 } name ? $" \"{name}\"" : string.Empty);

    // A message as an agent reads it, who from and what kind first
    private static string MailText(HexAgentMail message) =>
        $"[{message.Kind} from {message.FromName}] {message.Text}";

    private IEnumerable<McpToolResult?> AgentMessage(JsonObject args)
    {
        HexAgentChat? from = CallerOf(args) is { } caller ? ChatById(caller) : null;
        string text = args.String("text") ?? string.Empty;
        string kind = args.String("kind") is { Length: > 0 } said ? said : "finding";
        HexAgentChat? to = args.Integer("to") is { } id ? ChatById(id) : from?.Parent is { } parent ? ChatById(parent) : null;
        if (to is null)
        {
            yield return McpToolResult.Error(args.Integer("to") is null
                ? "You weren't started by another agent, say which chat with to. hex_agent_list has them."
                : "There's no such chat, hex_agent_list says which there are.");
            yield break;
        }

        if (to == from)
        {
            yield return McpToolResult.Error("That's your own chat.");
            yield break;
        }

        if (from is not null && RootOf(from) != RootOf(to))
        {
            yield return McpToolResult.Error($"#{to.Id} isn't of your family, only chats started from the same one talk to each other.");
            yield break;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            yield return McpToolResult.Error("Say something.");
            yield break;
        }

        yield return McpToolResult.Text(Deliver(to, from, kind, text, directive: kind == "instruction"));
    }

    private IEnumerable<McpToolResult?> AgentInbox(JsonObject args)
    {
        if (CallerOf(args) is not { } caller || ChatById(caller) is not { } chat)
        {
            yield return McpToolResult.Error("Only an agent of a chat of the Agents panel has an inbox.");
            yield break;
        }

        HexAgentMail[] unread = chat.UnreadMail();
        if (unread.Length == 0)
        {
            yield return McpToolResult.Text("No new messages.");
            yield break;
        }

        chat.MailRead(unread[^1].Seq);
        yield return McpToolResult.Text(string.Join("\n\n", unread.Select(MailText)));
    }

    /// <summary>
    /// A message given to a chat's agent, at once when it's working and can be told more, otherwise kept for it to read,
    /// an instruction starting it again when it's finished. Both chats get a short note of it. Says what became of it.
    /// </summary>
    private string Deliver(HexAgentChat to, HexAgentChat? from, string kind, string text, bool directive)
    {
        var message = new HexAgentMail(hub.NextMailSeq(), from?.Id, from is null ? "an agent outside Hex's panel" : MailName(from), kind, text, directive) { To = to.Id };
        to.Post(message);
        HexMailLog log = hub.MailOf(to);
        string line = OneLine(text, 160);
        from?.Note($"$(mail) to {MailName(to)} · {kind}: {line}");

        // Waiting for agents, it gets it from the wait, which this wakes. Said on its stdin it would only be read once
        // the wait was over, too late to steer the others on it
        if (to.Running && AgentWaiting(to))
        {
            log.Add(message, HexMailRoute.WokeWait);
            to.Note($"$(mail) from {message.FromName} · {kind}: {line}");
            return $"{MailName(to)} is waiting for its agents, the message wakes it.";
        }

        if (to.Running && SteeredRun(to) is not null)
        {
            log.Add(message, HexMailRoute.Steered);
            to.MailRead(message.Seq);
            SteerAgent(to, MailText(message), typed: false);
            return $"{MailName(to)} takes it in as it works.";
        }

        to.Note($"$(mail) from {message.FromName} · {kind}: {line}");
        if (to.Running)
        {
            log.Add(message, HexMailRoute.Inbox);
            return $"{MailName(to)} is working and can't be told more as it goes, it's in its inbox"
                + (directive ? ", and starts its next turn if it isn't read by the end of this one." : ".");
        }

        if (directive && !to.Stopped && extensions.AgentRuns.TryGetValue(to.Agent, out var run))
        {
            log.Add(message, HexMailRoute.StartedTurn);
            StartAgent(to, run.Extension, run.Run, string.Empty, text);
            return $"{MailName(to)} had finished, it's working on it now. hex_agent_wait waits for it.";
        }

        log.Add(message, HexMailRoute.Inbox);
        return $"{MailName(to)} isn't working, it reads it when it next does.";
    }

    // Who a message typed in the panel's box is from, as the agent reads it
    private const string FROM_PERSON = "the person watching";

    /// <summary>
    /// What somebody typed for an agent that's working but can't take it in on its stdin now, kept as an instruction in
    /// its mail. A wait for its agents is woken with it, otherwise it's read between steps or starts its next turn. Says
    /// in the person's words what became of it.
    /// </summary>
    private string DeliverFromPerson(HexAgentChat to, string text)
    {
        var message = new HexAgentMail(hub.NextMailSeq(), null, FROM_PERSON, "instruction", text, Directive: true) { To = to.Id };
        to.Post(message);
        hub.MailOf(to).Add(message, AgentWaiting(to) ? HexMailRoute.WokeWait : HexMailRoute.Inbox);
        return AgentWaiting(to)
            ? Said("agents_panel.told_waiting")
            : Said("agents_panel.told_inbox");
    }

    /// <summary>
    /// What a chat's agent is asked with what it hasn't read put in front, read from then on. An empty prompt when the
    /// messages are all there is to it.
    /// </summary>
    private static string UnreadPrompt(HexAgentChat chat, string prompt)
    {
        HexAgentMail[] unread = chat.UnreadMail();
        if (unread.Length == 0)
            return prompt;

        chat.MailRead(unread[^1].Seq);
        var said = new StringBuilder("Messages from the agents you work with, sent while you weren't working:\n\n");
        foreach (HexAgentMail message in unread)
            said.Append(MailText(message)).Append("\n\n");
        if (prompt.Length > 0)
            said.Append("---\n").Append(prompt);
        return said.ToString().TrimEnd();
    }

    /// <summary>
    /// A chat's turn over. An instruction it never read starts it again on what it was told, so a coordinator's word
    /// reaches an agent that couldn't be told as it worked. A stopped chat stays stopped.
    /// </summary>
    private void AfterAgentTurn(HexAgentChat chat)
    {
        if (chat.Running || chat.Stopped || !chat.UnreadMail().Any(message => message.Directive)
            || !extensions.AgentRuns.TryGetValue(chat.Agent, out var run))
            return;

        StartAgent(chat, run.Extension, run.Run, string.Empty, "messages sent while it worked");
    }
}
