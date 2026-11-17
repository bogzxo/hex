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
    public static ObjectValue Of(HexAgentChat chat, string folder, Action? wentWrong = null, Action? finished = null, Action? taken = null) => Natives.Object(
        ("id", new StringValue(chat.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))),
        ("folder", new StringValue(folder)),
        ("session", Natives.Function("session", args =>
        {
            if (args.Has(0) && args[0] is StringValue said && said.Value.Length > 0)
                chat.Session = said.Value;
            return chat.Session is { } session ? new StringValue(session) : Values.Null;
        })),
        ("option", Natives.Function("option", args =>
            chat.Options.TryGetValue(args.Text(0, string.Empty), out string? value) ? new StringValue(value) : Values.Null)),
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
}
