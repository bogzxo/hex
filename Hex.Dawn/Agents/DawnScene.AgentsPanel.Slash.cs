using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.Input;
using Horizon.Rendering;
using Horizon.UI.Drawing;

namespace Hex.Dawn;

// "/" in the box, the commands the agent system says it has (Claude Code's own and the skills it found, said in its
// handshake) listed over the card, those that start with what's typed. A press or Tab puts the command in the box with a
// space after it for its arguments, the agent itself doing what it says once it's sent. A system that says no commands
// lists none.
internal sealed partial class DawnScene
{
    // The most commands the list shows at once, and how tall a row of it is
    private const int SLASH_MOST = 8;
    private const float SLASH_ROW = 24.0f;

    private StackPanel? agentsSlash;
    private string agentsSlashFor = string.Empty;
    private string? agentsSlashFirst;

    /// <summary>The commands a chat's agent system says it has, name, what it does and what it takes.</summary>
    private List<(string Name, string About, string Hint)> SlashCommandsOf(string? agent)
    {
        var listed = new List<(string, string, string)>();
        if (agent is null || !extensions.AgentRuns.TryGetValue(agent, out var run))
            return listed;
        if (run.Run.Properties.TryGetValue("commands", out IRuntimeValue? said) && said is ListValue commands)
        {
            foreach (ObjectValue command in commands.Items.OfType<ObjectValue>().Take(500))
            {
                if (HexExtensions.Text(command, "name") is { Length: > 0 } name)
                    listed.Add((name, HexExtensions.Text(command, "about") ?? string.Empty, HexExtensions.Text(command, "hint") ?? string.Empty));
            }
        }

        // Compact is Hex's to offer when the system says how, whether or not it listed it
        if (run.Run.Properties.TryGetValue("compact", out IRuntimeValue? compact) && compact is StringValue { Value: ['/', .. string named] } && !listed.Any(each => each.Item1 == named))
            listed.Add((named, Said("noon.command_compact"), string.Empty));
        return listed;
    }

    /// <summary>Every tick, the list over the card while the box holds a "/" and part of a command's name, gone otherwise.</summary>
    private void DrawAgentSlash()
    {
        string text = agentsInput.Text;
        bool typing = compositor.Focus == agentsInput && text.Length > 0 && text[0] == '/' && !text.Any(char.IsWhiteSpace);
        string key = typing ? $"{AgentFor(agentChat)}|{text}|{agentsComposeTall}" : string.Empty;
        if (key == agentsSlashFor)
            return;
        agentsSlashFor = key;
        if (agentsSlash is not null)
        {
            agentsSlash.Parent?.Remove(agentsSlash);
            agentsSlash = null;
        }
        agentsSlashFirst = null;
        if (!typing)
            return;

        string typed = text[1..];
        var matches = SlashCommandsOf(AgentFor(agentChat))
            .Where(command => command.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            .OrderBy(command => command.Name.Length).ThenBy(command => command.Name, StringComparer.OrdinalIgnoreCase).Take(SLASH_MOST).ToList();
        if (matches.Count == 0)
            return;

        agentsSlashFirst = matches[0].Name;
        Vector4 ground = Skin?.Palette.Raised ?? new Vector4(0.2f, 0.2f, 0.22f, 1.0f);
        agentsSlash = agentsView.Add(new StackPanel
        {
            Anchor = Origin.Bottom, Pivot = Origin.Bottom, Fill = UIFill.Horizontal, Position = new Vector2(0, agentsComposeTall + 4),
            Padding = new UIEdges(4, 4, 4, 4), Spacing = 0, Color = ground with { W = 1.0f }, Radius = 8
        });
        foreach (var (name, about, hint) in matches)
        {
            string label = $"/{name}" + (hint.Length > 0 ? $" {hint}" : string.Empty) + (about.Length > 0 ? $"  ·  {OneLine(about, 80)}" : string.Empty);
            agentsSlash.Add(new Button
            {
                Label = UIRichText.Literal(label), Style = "button_flat", Align = Origin.Left, Fill = UIFill.Horizontal, Size = new Vector2(0, SLASH_ROW),
                LabelScale = 0.13f, Animated = false, Tooltip = about, OnPressed = () => TakeSlash(name)
            });
        }
    }

    /// <summary>A command put in the box with a space for what it takes, the box keeping the keyboard.</summary>
    private void TakeSlash(string name)
    {
        agentsInput.Text = $"/{name} ";
        agentsInput.Focus();
        agentsInput.Document.MoveTo(new UITextPosition(0, agentsInput.Document.LineAt(0).Length));
        agentsSlashFor = "\0";
    }

    /// <summary>Tab in the box while the list is up, its first command taken.</summary>
    private bool TakeFirstSlash()
    {
        if (agentsSlash is null || agentsSlashFirst is not { } first || compositor.Focus != agentsInput)
            return false;
        TakeSlash(first);
        return true;
    }
}
