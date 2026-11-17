using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Hex.Core;

/// <summary>
/// The chat object an agent system's extension is handed with every line its agent prints (run's on_event), what it
/// says what happened on. Rows of text, thinking, notes and errors, a tool started and done by its id, the session the
/// agent's in, what the turn used and the turn over. Its id and the folder the agent runs in too.
/// </summary>
internal static class HexAgentChatApi
{
    /// <param name="wentWrong">Called when the extension says something went wrong, so the run's exit doesn't say it again.</param>
    /// <param name="finished">Called for chat.done in place of ending the turn, for a run that decides when it's over.</param>
    /// <param name="taken">Called for chat.taken in place of the chat's own, for a run that knows which message that was.</param>
    /// <param name="write">A line for the agent's stdin, false when it couldn't be written (it isn't running, or its stdin is shut).</param>
    /// <param name="call">How a function of the extension's is called back later (an approval's answer), null when it can't be.</param>
    public static ObjectValue Of(HexAgentChat chat, string folder, Action? wentWrong = null, Action? finished = null, Action? taken = null,
        Func<string, bool>? write = null, Func<IRuntimeValue, IRuntimeValue[], bool>? call = null) => Natives.Object(
        // The agent asking leave, a row with Allow and Deny, the extension's function called with true or false once the
        // person answers. Returns a function that takes the row back unanswered (the agent gave up asking)
        ("approval", Natives.Function("approval", args =>
        {
            IRuntimeValue? answered = args.Has(2) ? args[2] : null;
            HexAgentEntry asked = chat.Approval(args.Text(0, "Allow this?"), args.Has(1) ? HexJson.Short(args[1]) : string.Empty,
                allowed => { if (answered is not null) call?.Invoke(answered, [new BooleanValue(allowed)]); });
            return Natives.Action("withdraw", _ =>
            {
                if (asked.Answer is null)
                    return;
                asked.Answer = null;
                asked.State = HexAgentToolState.Failed;
                chat.Touch(asked);
            });
        })),
        ("id", new StringValue(chat.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))),
        // Something for the agent's stdin, a string as it is or an object as a line of JSON, true when it went
        ("write", Natives.Function("write", args =>
        {
            if (write is null || !args.Has(0))
                return new BooleanValue(false);
            string line = args[0] is StringValue said ? said.Value : HexJson.FromHidl(args[0])?.ToJsonString() ?? string.Empty;
            return new BooleanValue(line.Length > 0 && write(line.ReplaceLineEndings(" ")));
        })),
        ("folder", new StringValue(folder)),
        ("session", Natives.Function("session", args =>
        {
            if (args.Has(0) && args[0] is StringValue said && said.Value.Length > 0)
                chat.Session = said.Value;
            return chat.Session is { } session ? new StringValue(session) : Values.Null;
        })),
        ("option", Natives.Function("option", args =>
            chat.Options.TryGetValue(args.Text(0, string.Empty), out string? value) ? new StringValue(value) : Values.Null)),
        // The pictures going with the message being written, { path, media_type, data } each with data its base64, for a
        // system that takes them inline (Claude Code's image blocks) or by their files (Codex's --image)
        ("images", Natives.Function("images", _ => new ListValue([.. chat.Attached.Select(ImageOf).OfType<IRuntimeValue>()]))),
        ("text", Natives.Action("text", args => chat.Say(args.Text(0, string.Empty)))),
        ("thinking", Natives.Action("thinking", args => chat.Think(args.Text(0, string.Empty)))),
        ("note", Natives.Action("note", args => chat.Note(args.Text(0, string.Empty)))),
        ("error", Natives.Action("error", args =>
        {
            chat.Error(args.Has(0) ? HexJson.Short(args[0]) : string.Empty);
            wentWrong?.Invoke();
        })),
        ("tool", Natives.Action("tool", args => chat.Tool(Values.Text(args[0]), args.Text(1, "tool"), args.Has(2) ? HexJson.Short(args[2]) : string.Empty))),
        ("tool_done", Natives.Action("tool_done", args =>
            chat.ToolDone(Values.Text(args[0]), !args.Has(1) || args[1] is not BooleanValue { Value: false }, args.Has(2) ? HexJson.Short(args[2]) : string.Empty))),
        // A step of a subagent the tool of that id started, on the tool's row instead of a row of its own
        ("progress", Natives.Action("progress", args => chat.Progress(Values.Text(args[0]), args.Has(1) ? HexJson.Short(args[1]) : string.Empty))),
        ("usage", Natives.Action("usage", args =>
        {
            ObjectValue used = args.Object(0);
            if (used.Properties.TryGetValue("input", out IRuntimeValue? input) && input is NumberValue tokensIn)
                chat.InputTokens += (long)tokensIn.Value;
            if (used.Properties.TryGetValue("output", out IRuntimeValue? output) && output is NumberValue tokensOut)
                chat.OutputTokens += (long)tokensOut.Value;
            if (used.Properties.TryGetValue("cost", out IRuntimeValue? cost) && cost is NumberValue dollars)
                chat.Cost += dollars.Value;
            if (used.Properties.TryGetValue("model", out IRuntimeValue? model) && model is StringValue { Value.Length: > 0 } named)
                chat.Model = named.Value;

            // Where its context is, as of its last call, and how much there is of it
            if (used.Properties.TryGetValue("context", out IRuntimeValue? context) && context is NumberValue { Value: > 0 } taken)
                chat.ContextUsed = (long)taken.Value;
            if (used.Properties.TryGetValue("context_limit", out IRuntimeValue? limit) && limit is NumberValue { Value: > 0 } room)
                chat.ContextLimit = (long)room.Value;
            if (used.Properties.TryGetValue("cache_read", out IRuntimeValue? cached) && cached is NumberValue cache)
                chat.CacheRead += (long)cache.Value;
            chat.Moved();
        })),
        // The agent's context compacted, { trigger: "auto" or "manual", before, after } in tokens
        ("compacted", Natives.Action("compacted", args =>
        {
            ObjectValue said = args.Has(0) && args[0] is ObjectValue given ? given : Natives.Object();
            chat.Compacted(TextOf(said, "trigger") ?? "auto", (long)NumberOf(said, "before", 0), (long)NumberOf(said, "after", 0));
        })),
        // A call to the model tried again, { attempt, most, delay } with the delay in seconds, and why
        ("retry", Natives.Action("retry", args =>
        {
            ObjectValue said = args.Has(0) && args[0] is ObjectValue given ? given : Natives.Object();
            chat.Retry((int)NumberOf(said, "attempt", chat.Retries + 1), (int)NumberOf(said, "most", 0), NumberOf(said, "delay", 0), TextOf(said, "why") ?? string.Empty);
        })),
        // Where it stands against a limit, { kind, status, used, resets, message }, used a share from 0 to 1 and resets
        // the time it starts again in seconds since 1970, as the systems say them
        ("limit", Natives.Action("limit", args =>
        {
            ObjectValue said = args.Object(0);
            double resets = NumberOf(said, "resets", 0);
            chat.Limited(new HexAgentLimit(TextOf(said, "kind") ?? string.Empty, HexAgentLimit.StateOf(TextOf(said, "status")),
                NumberOf(said, "used", -1), resets > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)resets) : null, TextOf(said, "message")));
        })),
        ("taken", Natives.Action("taken", _ =>
        {
            if (taken is not null)
                taken();
            else
                chat.Taken();
        })),
        ("done", Natives.Action("done", _ =>
        {
            if (finished is not null)
                finished();
            else
                chat.Done();
        })));

    // A picture as the extension is handed it, null for a file that's gone or can't be read
    private static ObjectValue? ImageOf(string path)
    {
        try
        {
            string type = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "image/png"
            };
            return Natives.Object(("path", new StringValue(path)), ("media_type", new StringValue(type)),
                ("data", new StringValue(Convert.ToBase64String(File.ReadAllBytes(path)))));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? TextOf(ObjectValue said, string key) =>
        said.Properties.TryGetValue(key, out IRuntimeValue? value) && value is StringValue { Value.Length: > 0 } text ? text.Value : null;

    private static double NumberOf(ObjectValue said, string key, double otherwise) =>
        said.Properties.TryGetValue(key, out IRuntimeValue? value) && value is NumberValue number && double.IsFinite(number.Value) ? number.Value : otherwise;
}
