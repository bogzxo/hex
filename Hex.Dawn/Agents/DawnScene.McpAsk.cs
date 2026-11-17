using System.Globalization;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

// An agent asking the person something it can't settle itself, a card in its chat with the answers it offers and a box
// for one of their own. It waits off the editor's thread, as hex_agent_wait does, since the editor's calls go one at a
// time and a question can wait minutes. Nobody answering in time isn't the end of the work, the agent is told to go on
// with the answer it thinks best, the one it said it would pick when there's one.
internal sealed partial class DawnScene
{
    // How long a question waits when the agent doesn't say, and the most it may
    private const int ASK_WAIT = 300, ASK_WAIT_MOST = 3600;

    // How many questions are waiting across the chats, so the tick only looks for ones whose time is up while there are
    private int questionsOpen;

    private IEnumerable<HexMcpTool> AskTools() =>
    [
        new(new McpTool("hex_ask", "Ask the person",
            "Ask the person watching a question you can't settle yourself, a decision between approaches, what they meant, which of several things to do. It's a card in your chat with the options you give as buttons and a box for an answer of their own. Waits for the answer, up to timeout seconds, then says nobody answered and you go on with the option you judge best (recommended, when you give it). Only for chats of Hex's Agents panel.",
            McpSchema.Object([
                ("question", McpSchema.String("The question, complete in itself.")),
                ("options", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "The answers to pick from, a few words each, two to six of them." }),
                ("recommended", McpSchema.Integer("Which option you'd pick yourself, counted from 0, what's taken when nobody answers.")),
                ("timeout", McpSchema.Number($"Seconds to wait for an answer, {ASK_WAIT} by default, {ASK_WAIT_MOST} at the most."))], "question")),
            null, RunAway: Ask, NeedsEditor: false, TakesCaller: true)
    ];

    private async Task<McpToolResult> Ask(JsonObject args, CancellationToken cancel)
    {
        if (CallerOf(args) is not { } caller)
            return McpToolResult.Error("hex_ask is for the agents of Hex's Agents panel, there's no chat to ask in.");
        string question = args.String("question")?.Trim() ?? string.Empty;
        if (question.Length == 0)
            return McpToolResult.Error("Say the question.");
        string[] options = args["options"] is JsonArray listed
            ? [.. listed.Select(each => each is JsonValue value && value.TryGetValue(out string? text) ? text.Trim() : string.Empty).Where(text => text.Length > 0).Take(8)]
            : [];
        int? recommended = args.Integer("recommended") is { } pick && pick >= 0 && pick < options.Length ? pick : null;
        double seconds = Math.Clamp(args.Number("timeout") ?? ASK_WAIT, 10, ASK_WAIT_MOST);

        // The card is made on the editor's thread, the answer handed back here from there
        var answered = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        HexAgentEntry? card = null;
        IEnumerable<McpToolResult?> Put()
        {
            if (ChatById(caller) is not { } chat)
            {
                yield return McpToolResult.Error("Your chat isn't there any more.");
                yield break;
            }

            card = chat.Question(question, options, recommended, seconds, answer => answered.TrySetResult(answer));
            questionsOpen++;
            yield return McpToolResult.Text("asked");
        }

        var put = new HexMcpCall("hex_ask", Put().GetEnumerator(), 30, cancel, caller, needsEditor: false);
        QueueMcp(put);
        McpToolResult made = await put.Done;
        if (made.IsError)
            return made;

        string? answer;
        try
        {
            // A little past the card's own time, which the tick ends, in case the editor's thread is held up
            answer = await answered.Task.WaitAsync(TimeSpan.FromSeconds(seconds + 5), cancel);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            answer = null;
            QueueMcp(new HexMcpCall("hex_ask", Expire().GetEnumerator(), 30, CancellationToken.None, caller, needsEditor: false));
            if (cancel.IsCancellationRequested)
                return McpToolResult.Error("hex_ask was cancelled.");
        }

        if (answer is not null)
            return McpToolResult.Text($"They answered: {answer}");
        string minutes = (seconds / 60).ToString("0.#", CultureInfo.InvariantCulture);
        return McpToolResult.Text(recommended is { } recommend
            ? $"Nobody answered in {minutes} minutes. Go on with the one you recommended, {options[recommend]}."
            : $"Nobody answered in {minutes} minutes. Go on with the option you judge best and say which you took.");

        // The card taken back unanswered when the call ended without the tick doing it
        IEnumerable<McpToolResult?> Expire()
        {
            if (card is not null && ChatById(caller) is { } chat && chat.Reply(card, null))
                questionsOpen = Math.Max(0, questionsOpen - 1);
            yield return McpToolResult.Text("expired");
        }
    }

    /// <summary>Every tick while questions wait, those whose time is up answered with nobody's answer.</summary>
    private void ExpireQuestions()
    {
        if (questionsOpen == 0)
            return;
        long now = System.Environment.TickCount64;
        int open = 0;
        foreach (HexAgentChat chat in agentChats)
        {
            foreach (HexAgentEntry question in chat.Pending.Where(entry => entry.Kind == HexAgentEntryKind.Question).ToArray())
            {
                if (question.Deadline is { } deadline && now >= deadline)
                    chat.Reply(question, null);
                else
                    open++;
            }
        }

        questionsOpen = open;
    }
}
