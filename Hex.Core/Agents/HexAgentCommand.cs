using Horizon.HIDL.Runtime;

namespace Hex.Core;

/// <summary>A provider command, or an action implemented by the chat host.</summary>
internal sealed record HexAgentCommand(string Name, string About = "", string Hint = "", string Action = "")
{
    public static bool TryParse(string text, out string name, out string arguments)
    {
        name = arguments = string.Empty;
        text = text.Trim();
        if (!text.StartsWith('/') || text.Length < 2)
            return false;
        int end = 1;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
            end++;
        string candidate = text[1..end];
        if (!IsCommandName(candidate))
            return false;
        name = candidate;
        arguments = text[end..].Trim();
        return true;
    }

    private static bool IsCommandName(string name) =>
        name.Length > 0 && !name.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\');

    public static List<HexAgentCommand> Read(IRuntimeValue? value)
    {
        var commands = new List<HexAgentCommand>();
        if (value is not ListValue list)
            return commands;
        foreach (ObjectValue item in list.Items.OfType<ObjectValue>())
        {
            string Text(string key) => item.Properties.GetValueOrDefault(key) is StringValue text ? text.Value : string.Empty;
            string name = Text("name").Trim().TrimStart('/');
            if (!IsCommandName(name))
                continue;
            string about = Text("about");
            string hint = Text("hint");
            commands.Add(new(name, about.Length > 0 ? about : Text("description"), hint.Length > 0 ? hint : Text("argumentHint"), Text("action")));
        }
        return commands.DistinctBy(command => command.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<HexAgentCommand> Matching(IEnumerable<HexAgentCommand> commands, string typed) =>
        commands.Where(command => command.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            .OrderBy(command => command.Name, StringComparer.OrdinalIgnoreCase).ToList();
}
