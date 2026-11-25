using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// How a local agent's chat runs: the model's server (provider ollama or openai), URL, model and key, its tuning, the
/// context window and the most tokens an answer may have, the approvals mode (ask, edits, all), the workspace folder, the
/// Agents panel's chat id, and the session to go on from (null for a new one).
/// </summary>
internal sealed record LocalAgentOptions(
    string Provider,
    string Url,
    string Model,
    string? ApiKey,
    PredictionTuning Tuning,
    int ContextTokens,
    int MaxTokens,
    string Approvals,
    string Folder,
    int ChatId,
    string? Session,
    string Profile = LocalAgentOptions.WRITE)
{
    public const string ASK = "ask", EDITS = "edits", ALL = "all";

    /// <summary>The profiles, which tools a chat has (apart from the approvals, which say when it asks): write (all), ask (read-only tools), minimal (none).</summary>
    public const string WRITE = "write", READ_ONLY = "ask", MINIMAL = "minimal";
}

/// <summary>What the agent reads from its stdin: a message to go on with, an approval's answer, or stop.</summary>
internal abstract record LocalAgentInput
{
    public sealed record Message(string Text) : LocalAgentInput;
    public sealed record Approval(string Id, bool Allow) : LocalAgentInput;
    public sealed record Stop : LocalAgentInput;
}

/// <summary>
/// The lines the agent writes on its stdout, one JSON object each, in the Agents panel's words: text, thinking, tool,
/// tool_done, usage, approval, session, note, error, done; and the lines it reads.
/// </summary>
internal static class LocalAgentProtocol
{
    public static string Text(string text) => Line("text", ("text", text));
    public static string Thinking(string text) => Line("thinking", ("text", text));
    public static string Tool(string id, string name, string inputJson) => Line("tool", ("id", id), ("name", name), ("input", InputNode(inputJson)));
    public static string ToolDone(string id, string output, bool error) => Line("tool_done", ("id", id), ("output", output), ("error", error));
    public static string Usage(int promptTokens, int outputTokens, double seconds, int contextUsed, int contextWindow) =>
        Line("usage", ("prompt", promptTokens), ("output", outputTokens), ("seconds", Math.Round(seconds, 3)), ("context", contextUsed), ("window", contextWindow));
    public static string Approval(string id, string tool, string inputJson, string text) =>
        Line("approval", ("id", id), ("tool", tool), ("input", InputNode(inputJson)), ("text", text));
    public static string Session(string id) => Line("session", ("id", id));
    public static string Note(string text) => Line("note", ("text", text));
    public static string Compacted(int before, int after) =>
        Line("compacted", ("text", $"Context compacted: ~{before} → ~{after} tokens"), ("before", before), ("after", after));
    public static string Error(string text) => Line("error", ("text", text));
    public static string Done(string reason) => Line("done", ("reason", reason));

    /// <summary>A line of stdin read, null when it isn't one the agent knows.</summary>
    public static LocalAgentInput? Read(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                return null;
            switch (type.GetString())
            {
                case "message":
                    return root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                        ? new LocalAgentInput.Message(text.GetString() ?? "")
                        : null;
                case "approval":
                    if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                        !root.TryGetProperty("allow", out var allow) || allow.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        return null;
                    return new LocalAgentInput.Approval(id.GetString() ?? "", allow.GetBoolean());
                case "stop":
                    return new LocalAgentInput.Stop();
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Arguments as JSON text become the object they hold, or a plain string when they aren't JSON.</summary>
    private static JsonNode? InputNode(string inputJson)
    {
        try
        {
            return JsonNode.Parse(inputJson) ?? new JsonObject();
        }
        catch (JsonException)
        {
            return JsonValue.Create(inputJson);
        }
    }

    /// <summary>One compact JSON line with the given type and fields; the JSON escapes every newline so it is a single line.</summary>
    private static string Line(string type, params (string Key, object? Value)[] fields)
    {
        var o = new JsonObject { ["type"] = type };
        foreach (var (key, value) in fields)
        {
            o[key] = value switch
            {
                null => null,
                JsonNode node => node,
                string s => JsonValue.Create(s),
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                double d => JsonValue.Create(d),
                _ => JsonValue.Create(value.ToString()),
            };
        }
        return o.ToJsonString();
    }
}

/// <summary>Hex's tools as the loop sees them.</summary>
internal interface IToolHost
{
    IReadOnlyList<ToolSpec> Tools { get; }
    Task<AgentToolResult> CallAsync(string name, string argumentsJson, CancellationToken cancel);
}

/// <summary>
/// One local agent chat: its conversation, and its turns run against the model and Hex's tools (docs/local-agent-plan.md
/// section 2). Every line it says goes to <c>say</c>; approvals are asked through <c>approve</c>, which answers when the
/// person does.
/// </summary>
internal sealed partial class LocalAgentLoop
{
    /// <summary>The most model calls one turn may make.</summary>
    public const int MAX_CALLS = 40;

    /// <summary>The window assumed when the options give none.</summary>
    private const int DEFAULT_WINDOW = 32768;

    /// <summary>What an output dropped to save context is replaced with.</summary>
    public const string NUDGE = "Continue: call a tool or give your answer.";

    public const string DROPPED = "[output dropped to save context]";

    private readonly IAgentChat chat;
    private readonly IToolHost tools;
    private readonly LocalAgentOptions options;
    private readonly Action<string> say;
    private readonly Func<string, string, string, CancellationToken, Task<bool>> approve;
    private readonly LocalAgentStore? store;
    private readonly List<AgentMessage> conversation = [];
    private readonly IReadOnlyList<ToolSpec> active;
    private string? session;
    private bool sessionSaid;

    /// <summary>The tokens the server counted for the last call (prompt and answer), 0 before any and after a compaction.</summary>
    private int lastUsed;

    /// <summary>The share of the window at which the older turns are summarised.</summary>
    public const int COMPACT_PERCENT = 85;

    public const string COMPACT_COMMAND = "/compact";

    /// <summary>What /compact says when there is nothing older than the last turns, a reply row of the chat's own (a note was not shown).</summary>
    public const string NOTHING_TO_COMPACT = "Nothing to compact yet.";

    /// <summary>What the model is asked to write to stand for the older turns.</summary>
    public const string SUMMARISE = "Summarise the conversation so far for continuing the task: goals, files touched, decisions, open steps. Be brief and concrete.";

    /// <summary>The tokens the conversation takes: what the server counted for the last call when it said, else an estimate.</summary>
    public int UsedTokens => Math.Max(lastUsed, EstimateTokens());

    public LocalAgentLoop(IAgentChat chat, IToolHost tools, LocalAgentOptions options, Action<string> say,
        Func<string, string, string, CancellationToken, Task<bool>> approve, LocalAgentStore? store = null)
    {
        this.chat = chat;
        this.tools = tools;
        this.options = options;
        this.say = say;
        this.approve = approve;
        this.store = store;
        session = options.Session;
        active = LocalAgentTools.ForProfile(tools.Tools, options.Profile);

        var system = new AgentMessage("system", SystemPrompt(options.Folder, active, true, options.Profile));
        var loaded = store != null && session != null ? store.Load(session) : null;
        if (loaded is { Count: > 0 })
        {
            conversation.AddRange(loaded);
            // The prompt of this run (its profile may not be the one the session started with)
            if (conversation[0].Role != "system")
                conversation.Insert(0, system);
            else
                conversation[0] = system;
        }
        else
            conversation.Add(system);
    }

    /// <summary>The conversation so far, the system prompt first.</summary>
    public IReadOnlyList<AgentMessage> Conversation => conversation;

    /// <summary>A turn: the person's message, then the model and its tools until it answers without a call, or the turn's limit.</summary>
    public async Task RunTurnAsync(string message, CancellationToken cancel)
    {
        if (message.Trim().Equals(COMPACT_COMMAND, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (!await CompactAsync(cancel))
                    say(LocalAgentProtocol.Text(NOTHING_TO_COMPACT));
            }
            catch (OperationCanceledException)
            {
                Save();
                throw;
            }
            catch (PredictionException ex)
            {
                say(LocalAgentProtocol.Error(ex.Message));
            }
            Save();
            say(LocalAgentProtocol.Done("stop"));
            return;
        }

        conversation.Add(new AgentMessage("user", message));
        if (store != null)
        {
            session ??= store.NewSession();
            if (!sessionSaid)
            {
                sessionSaid = true;
                say(LocalAgentProtocol.Session(session));
            }
        }

        string? lastSignature = null;
        int repeats = 0;
        bool editToolRan = false;
        bool nudged = false;
        try
        {
            for (int modelCalls = 0; modelCalls < MAX_CALLS; modelCalls++)
            {
                if (lastUsed >= Window * COMPACT_PERCENT / 100)
                {
                    try
                    {
                        await CompactAsync(cancel);
                    }
                    catch (PredictionException ex)
                    {
                        say(LocalAgentProtocol.Note("couldn't compact the context: " + ex.Message));
                        lastUsed = 0;
                    }
                }
                Trim();
                var text = new StringBuilder();
                var thought = new StringBuilder();
                var calls = new List<ToolCall>();
                await foreach (var delta in chat.Chat(conversation, active, cancel).WithCancellation(cancel))
                {
                    switch (delta)
                    {
                        case AgentDelta.Text t:
                            text.Append(t.Value);
                            say(LocalAgentProtocol.Text(t.Value));
                            break;
                        case AgentDelta.Thinking t:
                            thought.Append(t.Value);
                            say(LocalAgentProtocol.Thinking(t.Value));
                            break;
                        case AgentDelta.Call c:
                            calls.Add(c.Value);
                            break;
                        case AgentDelta.Usage u:
                            if (u.PromptTokens > 0)
                                lastUsed = u.PromptTokens + u.OutputTokens;
                            say(LocalAgentProtocol.Usage(u.PromptTokens, u.OutputTokens, u.Seconds, lastUsed > 0 ? lastUsed : EstimateTokens(), Window));
                            break;
                    }
                    if (delta is AgentDelta.Done)
                        break;
                }

                string content = text.ToString();
                if (calls.Count == 0)
                {
                    // A model that wrote its call as text: in the answer first, then in its reasoning
                    var found = ToolCallText.Find(content, out string rest);
                    if (found.Count > 0)
                        content = rest;
                    else
                        found = ToolCallText.Find(thought.ToString(), out _);
                    if (found.Count > 0)
                    {
                        say(LocalAgentProtocol.Note("recovered a tool call the model wrote as text"));
                        foreach (var call in found)
                            calls.Add(call with { Id = $"call_{conversation.Count}_{calls.Count + 1}" });
                    }
                }

                if (calls.Count == 0 && string.IsNullOrWhiteSpace(content))
                {
                    if (!nudged)
                    {
                        nudged = true;
                        conversation.Add(new AgentMessage("user", NUDGE));
                        Save();
                        continue;
                    }
                    say(LocalAgentProtocol.Note("the model ended its turn with no tool call and no answer"));
                    Save();
                    say(LocalAgentProtocol.Done("empty"));
                    return;
                }

                conversation.Add(new AgentMessage("assistant", content, calls.Count > 0 ? calls : null));
                Save();
                if (calls.Count == 0)
                {
                    if (options.Profile == LocalAgentOptions.READ_ONLY && !editToolRan && ClaimsEdit(content))
                        say(LocalAgentProtocol.Note(NO_FILES_CHANGED));
                    say(LocalAgentProtocol.Done("stop"));
                    return;
                }

                foreach (var call in calls)
                {
                    editToolRan |= IsEditTool(call.Name);
                    string signature = call.Name + "\n" + call.ArgumentsJson;
                    repeats = signature == lastSignature ? repeats + 1 : 1;
                    lastSignature = signature;
                    if (repeats >= 3)
                    {
                        say(LocalAgentProtocol.Note("stopped a repeated call"));
                        AnswerOrphans("Stopped: this call was repeated.");
                        Save();
                        say(LocalAgentProtocol.Done("repeat"));
                        return;
                    }
                    await RunCallAsync(call, cancel);
                }
                Save();
            }

            say(LocalAgentProtocol.Note($"stopped after {MAX_CALLS} model calls"));
            Save();
            say(LocalAgentProtocol.Done("limit"));
        }
        catch (OperationCanceledException)
        {
            AnswerOrphans("Cancelled.");
            Save();
            throw;
        }
        catch (PredictionException ex)
        {
            say(LocalAgentProtocol.Error(ex.Message));
            AnswerOrphans("The model's answer failed.");
            Save();
            say(LocalAgentProtocol.Done("error"));
        }
    }

    /// <summary>One tool call: checked, approved by the mode, run, and its output put in the conversation.</summary>
    private async Task RunCallAsync(ToolCall call, CancellationToken cancel)
    {
        string arguments = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson;

        string? problem = null;
        if (!active.Any(t => t.Name == call.Name))
            problem = $"There is no tool called {call.Name}. The tools are: {string.Join(", ", active.Select(t => t.Name))}.";
        else
        {
            try
            {
                using var document = JsonDocument.Parse(arguments);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    problem = "The arguments must be a JSON object.";
            }
            catch (JsonException ex)
            {
                problem = $"The arguments are not valid JSON: {ex.Message}";
            }
        }
        if (problem != null)
        {
            Finish(call, arguments, problem, true);
            return;
        }

        if (NeedsApproval(call.Name))
        {
            say(LocalAgentProtocol.Approval(call.Id, call.Name, arguments, $"Run {call.Name}?"));
            if (!await approve(call.Id, call.Name, arguments, cancel))
            {
                Finish(call, arguments, "The person didn't allow this.", true);
                return;
            }
        }

        say(LocalAgentProtocol.Tool(call.Id, call.Name, arguments));
        AgentToolResult result;
        try
        {
            result = await tools.CallAsync(call.Name, arguments, cancel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new AgentToolResult($"The tool failed: {ex.Message}", true);
        }
        int budget = Math.Max(2000, (options.ContextTokens > 0 ? options.ContextTokens : DEFAULT_WINDOW) * 4 / 8);
        string output = LocalAgentTools.Cut(result.Text, budget);
        say(LocalAgentProtocol.ToolDone(call.Id, output, result.IsError));
        conversation.Add(new AgentMessage("tool", output, null, call.Id, call.Name));
    }

    /// <summary>A call that wasn't run (or wasn't asked): its tool and tool_done lines, and the tool message with the text.</summary>
    private void Finish(ToolCall call, string arguments, string text, bool error)
    {
        say(LocalAgentProtocol.Tool(call.Id, call.Name, arguments));
        say(LocalAgentProtocol.ToolDone(call.Id, text, error));
        conversation.Add(new AgentMessage("tool", text, null, call.Id, call.Name));
    }

    /// <summary>Whether the approvals mode asks the person before this tool runs.</summary>
    private bool NeedsApproval(string tool)
    {
        if (LocalAgentTools.ReadsOnly(tool))
            return false;
        return options.Approvals switch
        {
            LocalAgentOptions.ALL => false,
            LocalAgentOptions.EDITS => !LocalAgentTools.Edits(tool),
            _ => true,
        };
    }

    /// <summary>Gives every call of the last assistant message that has no tool message yet one with the text, so the conversation stays valid.</summary>
    private void AnswerOrphans(string text)
    {
        int at = conversation.FindLastIndex(m => m.Role == "assistant");
        if (at < 0 || conversation[at].Calls is not { Count: > 0 } calls)
            return;
        foreach (var call in calls)
        {
            bool answered = false;
            for (int i = at + 1; i < conversation.Count; i++)
                if (conversation[i].Role == "tool" && conversation[i].CallId == call.Id)
                    answered = true;
            if (!answered)
                conversation.Add(new AgentMessage("tool", text, null, call.Id, call.Name));
        }
    }

    private void Save()
    {
        if (store == null || session == null)
            return;
        try
        {
            store.Save(session, conversation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A conversation that can't be kept is still a conversation; the turn goes on.
        }
    }

    private int Window => options.ContextTokens > 0 ? options.ContextTokens : DEFAULT_WINDOW;

    /// <summary>The conversation's size in tokens, estimated as a quarter of its characters.</summary>
    private int EstimateTokens()
    {
        long chars = 0;
        foreach (var m in conversation)
        {
            chars += m.Content.Length;
            if (m.Calls != null)
                foreach (var c in m.Calls)
                    chars += c.Name.Length + c.ArgumentsJson.Length;
        }
        return (int)(chars / 4);
    }

    /// <summary>
    /// Keeps the conversation under three quarters of the window: the older tool outputs are dropped first (the newest
    /// four stay), then the oldest turns are merged into one short summary (the last six messages stay).
    /// </summary>
    private void Trim()
    {
        // With the server's own counts the compaction is what keeps the room; this is then only the last resort
        int limit = lastUsed > 0 ? Window * 95 / 100 : Window * 3 / 4;
        if (EstimateTokens() <= limit)
            return;

        bool trimmed = false;
        var toolAt = new List<int>();
        for (int i = 0; i < conversation.Count; i++)
            if (conversation[i].Role == "tool")
                toolAt.Add(i);
        for (int k = 0; k < toolAt.Count - 4 && EstimateTokens() > limit; k++)
        {
            var m = conversation[toolAt[k]];
            if (m.Content == DROPPED)
                continue;
            conversation[toolAt[k]] = m with { Content = DROPPED };
            trimmed = true;
        }

        if (EstimateTokens() > limit)
        {
            // The kept tail starts at a message that isn't a tool output, so no output loses the call it answers.
            int keepFrom = conversation.Count - 6;
            while (keepFrom < conversation.Count && keepFrom > 0 && conversation[keepFrom].Role == "tool")
                keepFrom++;
            if (keepFrom > 1 && keepFrom < conversation.Count)
            {
                var summary = new StringBuilder("Earlier in this chat (summarised):");
                for (int i = 1; i < keepFrom; i++)
                {
                    var m = conversation[i];
                    if (m.Role == "tool" || string.IsNullOrWhiteSpace(m.Content))
                        continue;
                    string content = m.Content.Length > 200 ? m.Content[..200] : m.Content;
                    summary.Append('\n').Append(m.Role).Append(": ").Append(content);
                }
                conversation.RemoveRange(1, keepFrom - 1);
                conversation.Insert(1, new AgentMessage("user", summary.ToString()));
                trimmed = true;
            }
        }

        if (trimmed)
            say(LocalAgentProtocol.Note("trimmed the earlier conversation to fit the context"));
    }

    /// <summary>
    /// Summarises the older turns with the model and puts the summary in their place, the system prompt and the last two
    /// turns (from the second-last of the person's messages) kept. False when there is nothing older to summarise.
    /// </summary>
    private async Task<bool> CompactAsync(CancellationToken cancel)
    {
        var users = new List<int>();
        for (int i = 1; i < conversation.Count; i++)
            if (conversation[i].Role == "user")
                users.Add(i);
        int keepFrom = users.Count >= 2 ? users[^2] : -1;
        if (keepFrom < 2)
            return false;

        int before = UsedTokens;
        var ask = new List<AgentMessage> { conversation[0] };
        for (int i = 1; i < keepFrom; i++)
        {
            var m = conversation[i];
            ask.Add(m.Role == "tool" ? m with { Content = LocalAgentTools.Cut(m.Content, 2000) } : m);
        }
        ask.Add(new AgentMessage("user", SUMMARISE));

        var summary = new StringBuilder();
        await foreach (var delta in chat.Chat(ask, [], cancel).WithCancellation(cancel))
        {
            if (delta is AgentDelta.Text t)
                summary.Append(t.Value);
            else if (delta is AgentDelta.Usage u)
                say(LocalAgentProtocol.Usage(u.PromptTokens, u.OutputTokens, u.Seconds, u.PromptTokens + u.OutputTokens, Window));
            if (delta is AgentDelta.Done)
                break;
        }

        string text = summary.ToString().Trim();
        if (text.Length == 0)
            throw new PredictionException(PredictionStatus.Unreachable, "the model gave no summary");

        conversation.RemoveRange(1, keepFrom - 1);
        conversation.Insert(1, new AgentMessage("user", "Summary of the earlier conversation (context compacted):\n" + text));
        lastUsed = 0;
        Save();
        say(LocalAgentProtocol.Compacted(before, EstimateTokens()));
        return true;
    }

    /// <summary>The note an Ask-mode answer gets when it says it changed something and nothing was written.</summary>
    public const string NO_FILES_CHANGED = "No files were changed (Ask mode).";

    /// <summary>Whether an answer says it changed code: "renamed", "updated", "changed", "edited", "wrote" and the like.</summary>
    public static bool ClaimsEdit(string answer) => EditClaim().IsMatch(answer);

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(renamed|updated|changed|edited|wrote|written|modified|replaced|fixed|added)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex EditClaim();

    /// <summary>Whether a tool writes files (hex_edit, hex_write, a rename, a fix, a shell).</summary>
    public static bool IsEditTool(string name) =>
        name.Contains("edit", StringComparison.OrdinalIgnoreCase) || name.Contains("write", StringComparison.OrdinalIgnoreCase)
        || name.Contains("rename", StringComparison.OrdinalIgnoreCase) || name.Contains("fix", StringComparison.OrdinalIgnoreCase)
        || name.Contains("terminal_run", StringComparison.OrdinalIgnoreCase) || name.Contains("bash", StringComparison.OrdinalIgnoreCase);

    /// <summary>The system prompt a local agent starts with for a folder and the tools it has.</summary>
    public static string SystemPrompt(string folder, IReadOnlyList<ToolSpec> tools, bool nativeTools, string profile = LocalAgentOptions.WRITE)
    {
        var prompt = new StringBuilder();
        if (profile == LocalAgentOptions.MINIMAL)
        {
            prompt.Append("You are a helpful coding assistant inside the Hex code editor, working on the folder ").Append(folder).Append(".\n");
            prompt.Append("You have no tools here: answer from the conversation. Keep your answers short.");
            return prompt.ToString();
        }
        if (profile == LocalAgentOptions.READ_ONLY)
        {
            prompt.Append("You are a coding assistant working in the folder ").Append(folder).Append(" inside the Hex code editor.\n");
            prompt.Append("You can only read: use the tools to look at the code before you answer, do not guess what a file holds.\n");
            prompt.Append("You cannot edit files or run commands; if asked to change something, say what you would change and that you can't apply it in Ask mode. Never say you changed, renamed, updated or wrote anything.\n");
            prompt.Append("Paths are relative to the folder, or absolute inside it.\n");
            prompt.Append("Make one tool call at a time. Keep your answers short.");
            if (!nativeTools)
                prompt.Append("\n\n").Append(ToolCallText.Describe(tools));
            return prompt.ToString();
        }
        prompt.Append("You are a coding agent working in the folder ").Append(folder).Append(" inside the Hex code editor.\n");
        prompt.Append("Use the tools to look at the code before you change it; do not guess what a file holds.\n");
        prompt.Append("Paths are relative to the folder, or absolute inside it.\n");
        prompt.Append("Make one tool call at a time and wait for its result before the next.\n");
        prompt.Append("To change code prefer hex_edit with the exact old text and the new text.\n");
        prompt.Append("After you change code, check the problems and fix what you broke.\n");
        prompt.Append("Keep your answers short. When you are finished, say what you did.");
        if (!nativeTools)
            prompt.Append("\n\n").Append(ToolCallText.Describe(tools));
        return prompt.ToString();
    }
}

/// <summary>Where a local agent's conversations are kept between turns and restarts: one JSON file a session in a folder.</summary>
internal sealed class LocalAgentStore(string folder)
{
    public string Folder => folder;

    /// <summary>A new session id: local-, the time, and six random hex digits.</summary>
    public string NewSession() =>
        "local-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();

    /// <summary>Writes the conversation to the session's file, through a temporary file so a crash never leaves half of one.</summary>
    public void Save(string session, IReadOnlyList<AgentMessage> conversation)
    {
        Directory.CreateDirectory(folder);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var m in conversation)
            {
                writer.WriteStartObject();
                writer.WriteString("role", m.Role);
                writer.WriteString("content", m.Content);
                if (m.Calls is { Count: > 0 })
                {
                    writer.WriteStartArray("calls");
                    foreach (var c in m.Calls)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("id", c.Id);
                        writer.WriteString("name", c.Name);
                        writer.WriteString("arguments", c.ArgumentsJson);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                if (m.CallId != null)
                    writer.WriteString("callId", m.CallId);
                if (m.Name != null)
                    writer.WriteString("name", m.Name);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        string path = PathOf(session);
        string temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllBytes(temp, stream.ToArray());
        File.Move(temp, path, true);
    }

    /// <summary>The session's conversation, null when it has no file or the file isn't one.</summary>
    public List<AgentMessage>? Load(string session)
    {
        try
        {
            string path = PathOf(session);
            if (!File.Exists(path))
                return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return null;
            var result = new List<AgentMessage>();
            foreach (var e in document.RootElement.EnumerateArray())
            {
                List<ToolCall>? calls = null;
                if (e.TryGetProperty("calls", out var array) && array.ValueKind == JsonValueKind.Array)
                {
                    calls = [];
                    foreach (var c in array.EnumerateArray())
                        calls.Add(new ToolCall(Str(c, "id") ?? "", Str(c, "name") ?? "", Str(c, "arguments") ?? "{}"));
                }
                result.Add(new AgentMessage(Str(e, "role") ?? "user", Str(e, "content") ?? "", calls, Str(e, "callId"), Str(e, "name")));
            }
            return result;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private string PathOf(string session) => Path.Combine(folder, Path.GetFileName(session) + ".json");

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
