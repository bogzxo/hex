using System.Text.Json.Nodes;

namespace Hex.Dawn;

// What an agent started by another may do, decided by the one that started it. At the start, hex_agent_spawn's
// permissions say what it may and mayn't do without asking, in its agent system's own rules, which its extension hands
// on (chat options allow and deny). As it works, what it asks leave for goes to the agent that started it as a message,
// which hex_agent_approve answers. The card is in its chat for the person as well, whoever answers first decides.
internal sealed partial class DawnScene
{
    // The chat options a spawn's permissions are kept in, a rule a line, and whose word its approvals wait on
    internal const string ALLOW = "allow", DENY = "deny", APPROVALS = "approvals";

    private HexMcpTool AgentApproveTool() =>
        new(new McpTool("hex_agent_approve", "Answer an agent's request",
            "Allow or deny what an agent you started asked leave to do (a command, a change, a tool its rules don't let it use without asking), which reaches you as a message of kind question. The oldest request it's waiting on unless you say which by its number in that message. The person can answer it on its card as well, whoever answers first decides.",
            McpSchema.Object([
                ("id", McpSchema.Integer("The chat that asked.")),
                ("allow", McpSchema.Flag("True to let it, false to refuse.")),
                ("request", McpSchema.Integer("Which of its requests, its number in the message, the oldest waiting by default."))], "id", "allow")),
            AgentApprove);

    /// <summary>
    /// A chat started by another one of the same agent system works at the permission level its parent does (what it may do
    /// without asking), never above it: the option the parent has in effect is the child's too, unless the spawn's options
    /// said another. Of the agent system's options <paramref name="optionKeys"/>, those that name a permission level.
    /// </summary>
    internal static void InheritPermissions(IDictionary<string, string> child, IEnumerable<string> optionKeys, Func<string, string?> parentValue)
    {
        // Its model too: a helper that runs a bigger model than the one that started it spends what that one wasn't given
        foreach (string key in optionKeys.Concat([ALLOW, DENY]).Distinct().Where(key => PERMISSION_KEYS.Contains(key) || key is "model" or ALLOW or DENY))
        {
            if (!child.ContainsKey(key) && parentValue(key) is { Length: > 0 } level)
                child[key] = level;
        }
    }

    /// <summary>
    /// The rules a spawn says for the agent, kept as its chat's options a rule a line, and whose word its requests wait on.
    /// False with why when they aren't lists of rules.
    /// </summary>
    private static bool TakePermissions(JsonObject args, HexAgentChat chat, out string? why)
    {
        why = null;
        if (args["permissions"] is JsonObject permissions)
        {
            foreach (string kind in (ReadOnlySpan<string>)[ALLOW, DENY])
            {
                if (permissions[kind] is null)
                    continue;
                if (permissions[kind] is not JsonArray rules || rules.Any(rule => rule is not JsonValue value || !value.TryGetValue(out string? _)))
                {
                    why = $"permissions.{kind} is a list of rules as text.";
                    return false;
                }

                string[] said = [.. rules.Select(rule => rule!.GetValue<string>().Trim()).Where(rule => rule.Length > 0)];
                if (said.Length > 0)
                    chat.Options[kind] = string.Join('\n', said);
            }
        }

        if (args.String("approvals") is { Length: > 0 } approvals)
            chat.Options[APPROVALS] = approvals;
        return true;
    }

    /// <summary>
    /// An approval asked by an agent another started, sent to that one as a question it answers with hex_agent_approve,
    /// starting its next turn if it had finished. One started by the person, or told its requests are the person's, has
    /// its card alone.
    /// </summary>
    private void RouteApproval(HexAgentChat chat, HexAgentEntry asked)
    {
        if (chat.Parent is not { } up || ChatById(up) is not { } parent || chat.Options.GetValueOrDefault(APPROVALS) == "person")
            return;

        int number = RequestNumber(chat, asked);
        string detail = string.IsNullOrWhiteSpace(asked.Input) ? string.Empty : $"\n{OneLine(HexAgentRows.Readable(asked.Input!), 600)}";
        string text = $"#{chat.Id} {MailName(chat)} asks leave (request {number}): {asked.Text}{detail}\n"
            + $"Answer with hex_agent_approve (id {chat.Id}, request {number}, allow true or false). It waits until you do, or the person answers on its card.";
        Deliver(parent, chat, "question", text, directive: true, allowStopped: false);
    }

    // A request's number in its chat, counted over the approvals among the rows it keeps, so an answer names the one it
    // means. One waiting is always recent, never among the rows let go of
    private static int RequestNumber(HexAgentChat chat, HexAgentEntry asked) => HexAgentHub.RequestNumber(chat, asked);

    private IEnumerable<McpToolResult?> AgentApprove(JsonObject args)
    {
        if (args.Integer("id") is not { } id || ChatById(id) is not { } chat)
        {
            yield return McpToolResult.Error("There's no chat of that id, hex_agent_list says which there are.");
            yield break;
        }

        // Only the agent that started it, or a client that isn't an agent of the panel (the person's own tools)
        if (CallerOf(args) is { } caller && chat.Parent != caller)
        {
            yield return McpToolResult.Error($"#{id} wasn't started by you, only the agent that started it decides what it may do.");
            yield break;
        }

        HexLinkAgentCall? remote = HexLinkAllowed.AgentCall(args);
        if (remote is { Chat: not null } && chat.LinkStarter?.Matches(remote) != true)
        {
            yield return McpToolResult.Error($"#{id} wasn't started by this linked chat. The person can answer its approval card.");
            yield break;
        }

        HexAgentEntry[] waiting = [.. HexAgentHub.Approvals(chat)];
        HexAgentEntry? request = args.Integer("request") is { } number
            ? waiting.FirstOrDefault(entry => RequestNumber(chat, entry) == number)
            : waiting.FirstOrDefault();
        if (request is null)
        {
            yield return McpToolResult.Error(waiting.Length == 0
                ? $"#{id} isn't waiting on any request, it was answered already or it gave up asking."
                : $"#{id} has no request of that number waiting, it's waiting on {string.Join(", ", waiting.Select(entry => RequestNumber(chat, entry)))}.");
            yield break;
        }

        bool allow = args["allow"]?.GetValue<bool>() == true;
        chat.Decide(request, allow);
        string who = remote is null ? CallerOf(args) is { } deciding ? $"#{deciding}" : "a tool"
            : remote.Chat is { } starter ? $"{remote.Device.Name} #{starter}" : remote.Device.Name;
        chat.Note($"$(shield) {(allow ? "allowed" : "denied")} by {who}: {OneLine(request.Text, 120)}");
        yield return McpToolResult.Text($"#{id}'s request {RequestNumber(chat, request)} {(allow ? "allowed" : "denied")}: {OneLine(request.Text, 160)}");
    }
}
