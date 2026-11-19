using Horizon.HIDL.Runtime;
using Horizon.Rendering;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private bool IsAgentSlash(string text) => HexAgentCommand.TryParse(text, out _, out _);

    /// <summary>Host commands execute before prompt decoration and before steering a running agent.</summary>
    private bool HandleAgentCommand(string asked)
    {
        if (!HexAgentCommand.TryParse(asked, out string name, out string arguments)
            || AgentFor(agentChat) is not { } agent || !extensions.AgentRuns.TryGetValue(agent, out var run))
            return false;
        List<HexAgentCommand> commands = SlashCommandsOf(agent);
        HexAgentCommand? command = commands.FirstOrDefault(each => string.Equals(each.Name, name, StringComparison.OrdinalIgnoreCase));
        // A message that only starts with a slash, "/tmp is full", is a message: Codex's strict commands turned every one away
        if (command is null)
        {
            if (arguments.Length == 0 && run.Run.Properties.GetValueOrDefault("strict_commands") is BooleanValue { Value: true })
            {
                Say($"Unknown command /{name}. Use /help to see the commands supported by this provider.");
                return true;
            }
            return false;
        }
        // Commands don't consume attachments or accidentally send their contents as command arguments.
        if (agentImages.Count > 0)
        {
            Say("Remove the attached images before running a slash command.");
            return true;
        }
        if (command.Name == "compact" && arguments.Length == 0)
        {
            if (agentChat?.Session is null)
                Say("There is no conversation to compact yet.");
            else
                CompactAgent(agentChat);
            agentsInput.Text = string.Empty;
            return true;
        }
        if (command.Action.Length == 0)
        {
            if (agentChat?.Running == true || agentChat is { } active && agentRuns.Any(each => each.Chat == active))
            {
                Say("Wait for the current turn to finish before running this command.");
                return true;
            }
            return false;
        }
        if (command.Action.StartsWith("option:", StringComparison.Ordinal))
        {
            if (agentChat?.Running == true)
            {
                Say("Wait for the current turn to finish before changing its options.");
                return true;
            }
            string key = command.Action["option:".Length..];
            AgentOption? option = OptionsOf(agent).FirstOrDefault(each => each.Key == key);
            if (option is null) return true;
            HexAgentChat? target = agentChat;
            void Choose(string value)
            {
                if (target is not null)
                    target.Options[key] = value;
                else
                    extensions.SetSetting(run.Extension, key, new StringValue(value), tell: true);
                agentsOptionsDrawn = string.Empty;
                Say($"{option.Title}: {option.Choices.FirstOrDefault(each => each.Value == value).Label ?? value}. Applies to the next turn.");
            }
            if (arguments.Length > 0)
            {
                var choice = option.Choices.FirstOrDefault(each => string.Equals(each.Value, arguments, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(each.Label, arguments, StringComparison.OrdinalIgnoreCase)
                    || arguments == "default" && each.Value.Length == 0);
                if (choice.Value is null)
                {
                    Say($"Unknown {option.Title.ToLowerInvariant()}: {arguments}. Use /{name} to choose a value.");
                    return true;
                }
                Choose(choice.Value);
            }
            else
            {
                var menu = new Menu(option.Title);
                foreach (var (value, label) in option.Choices)
                {
                    string selected = value;
                    menu.Add(label, () => Choose(selected));
                }
                ContextMenu.Show(chrome, agentsInput.Bounds.PointAt(Origin.TopLeft), menu);
            }
        }
        else
        {
            // Hex's own commands take nothing after them, so words after one ("/help me with this") are a message to send
            if (arguments.Length > 0)
                return false;
            switch (command.Action)
            {
                case "help":
                    CommandNote(string.Join("\n", commands.OrderBy(each => each.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(each => $"/{each.Name}{(each.Hint.Length > 0 ? " " + each.Hint : "")} — {each.About}")));
                    break;
                case "new":
                    NewAgentChat();
                    break;
                case "resume":
                    agentsHistory.OnPressed?.Invoke();
                    break;
                case "stop":
                    StopAgent(agentChat);
                    break;
                case "status":
                    CommandNote(agentChat is { } chat
                        ? $"{HexAgentProfiles.All.FirstOrDefault(each => each.Id == agent)?.Name ?? agent}\nModel: {chat.Model ?? "not reported"}\nContext: {chat.ContextUsed:N0} / {chat.ContextLimit:N0} tokens\nSession: {chat.Session ?? "not started"}\n{(chat.Running ? "Working" : "Idle")}"
                        : "No conversation has been started.");
                    break;
                default:
                    return false;
            }
        }
        RememberAsked(asked);
        agentsInput.Text = string.Empty;
        return true;
    }

    private void CommandNote(string text)
    {
        if (agentChat is null && AgentFor(null) is { } agent)
        {
            agentChat = new HexAgentChat(++agentChatsMade, agent);
            agentChats.Add(agentChat);
        }
        agentChat?.Note(text);
    }
}
