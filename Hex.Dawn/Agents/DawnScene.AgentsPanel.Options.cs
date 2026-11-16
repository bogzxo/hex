using System.Text.RegularExpressions;

using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Key = Horizon.Input.Key;

namespace Hex.Dawn;

// What the Agents view lets you choose and do around the chat. Over the box a row of pickers, the file on screen as a
// chip that goes in front of what's asked, and whatever the agent's extension offers (its run's options, a model, how
// much it may do, how hard it thinks), each a menu of its choices kept in that extension's settings. Up and Down in the
// empty box go back through what was asked, Escape stops the agent, a path:line in what it said opens the file there,
// and the chats menu can ask the last thing again, copy the last answer or say what the harness saved.
internal sealed partial class DawnScene
{
    private StackPanel agentsOptions = null!;
    private string agentsOptionsDrawn = string.Empty;
    private Button? agentsThinking;

    // What was asked in this run, the newest last, and how far back Up has gone (Count for not at all)
    private readonly List<string> agentsAsked = [];
    private int agentsRecall;

    // A path with a line, Hex.Core/Mcp/McpServer.cs:47, in what an agent says, in backticks or not
    private static readonly Regex PlaceInText = new(@"(?<link>!?\[[^\]\r\n]*\]\((?:[^()\r\n]|\([^()\r\n]*\))*\))|(?<ticks>`+)(?<code>.*?)\k<ticks>|(?<path>(?:[A-Za-z]:)?[\w.\-/\\]+\.\w{1,6}):(?<line>\d+)", RegexOptions.Compiled);
    private static readonly Regex CodePlace = new(@"\A(?<path>(?:[A-Za-z]:)?[\w.\-/\\]+\.\w{1,6}):(?<line>\d+)\z", RegexOptions.Compiled);

    private void BuildAgentsOptions(UILayout made)
    {
        agentsOptions = made.Get<StackPanel>("agents_options");

        // Up and Down in the box go through what was asked before, Escape stops what's going on
        commands.Keys.Add(Key.Up, () => compositor.Focus == agentsInput && RecallAsked(-1), whileTyping: true, first: true);
        commands.Keys.Add(Key.Down, () => compositor.Focus == agentsInput && RecallAsked(1), whileTyping: true, first: true);
        commands.Keys.Add(Key.Escape, () =>
        {
            if (compositor.Focus != agentsInput || agentChat?.Running != true)
                return false;
            StopAgent(agentChat);
            return true;
        }, whileTyping: true, first: true);
    }

    /// <summary>What was asked before put back in the box, false when there's nothing to go to so the key does what it does.</summary>
    private bool RecallAsked(int by)
    {
        if (agentsAsked.Count == 0 || (by < 0 && agentsInput.Text.Length > 0 && agentsRecall == agentsAsked.Count))
            return false;

        int to = Math.Clamp(agentsRecall + by, 0, agentsAsked.Count);
        if (to == agentsRecall)
            return false;

        agentDraftContext = null;
        agentsRecall = to;
        agentsInput.Text = to == agentsAsked.Count ? string.Empty : agentsAsked[to];
        return true;
    }

    /// <summary>What was asked kept for Up, the same thing twice in a row once.</summary>
    private void RememberAsked(string asked)
    {
        if (agentsAsked.Count == 0 || agentsAsked[^1] != asked)
            agentsAsked.Add(asked);
        if (agentsAsked.Count > 100)
            agentsAsked.RemoveAt(0);
        agentsRecall = agentsAsked.Count;
    }

    /// <summary>One of the choices an agent's extension offers, the setting it's kept in and what each choice is called.</summary>
    private sealed record AgentOption(string Key, string Title, string Icon, (string Value, string Label)[] Choices);

    /// <summary>The options an agent's run offers, none when it offers none or isn't there.</summary>
    private List<AgentOption> OptionsOf(string? agent)
    {
        var options = new List<AgentOption>();
        if (agent is null || !extensions.AgentRuns.TryGetValue(agent, out var run) || !run.Run.Properties.TryGetValue("options", out IRuntimeValue? listed) || listed is not ListValue list)
            return options;

        foreach (ObjectValue option in list.Items.OfType<ObjectValue>())
        {
            if (HexExtensions.Text(option, "id") is not { } key || !option.Properties.TryGetValue("choices", out IRuntimeValue? choices) || choices is not ListValue each)
                continue;

            (string, string)[] made = [.. each.Items.OfType<ObjectValue>().Select(choice => (Values.Text(choice.Properties.GetValueOrDefault("value") ?? Values.Null), HexExtensions.Text(choice, "label") ?? Values.Text(choice.Properties.GetValueOrDefault("value") ?? Values.Null)))];
            options.Add(new AgentOption(key, HexExtensions.Text(option, "title") ?? key, HexExtensions.Text(option, "icon") ?? "settings-gear", made));
        }

        return options;
    }

    /// <summary>The row of pickers over the box, made again when the agent, a choice or the file on screen changes.</summary>
    private void DrawAgentOptions()
    {
        string? agent = AgentFor(agentChat);
        List<AgentOption> options = OptionsOf(agent);
        HexExtension? extension = agent is not null && extensions.AgentRuns.TryGetValue(agent, out var run) ? run.Extension : null;
        string file = document?.Path is { } path && document.IsText ? Path.GetFileName(path) : string.Empty;

        bool compact = agentsCompose.Bounds.Width < 300;
        string said = $"{compact}|{agent}|{agentWithFile}|{file}|{AgentDraftCaption()}|{string.Join("|", options.Select(option => option.Key + "=" + (extension is null ? string.Empty : Values.Text(extensions.SettingOf(extension, option.Key)))))}";
        if (said == agentsOptionsDrawn) return;
        agentsOptionsDrawn = said;
        Empty(agentsOptions);
        agentsThinking = null;
        bool captured = agentDraftContext is not null;
        string attached = agentDraftContext is { } snapshot ? Path.GetFileName(snapshot.Path) : string.Empty;
        if (attached.Length > 12) attached = attached[..10] + "…";
        if (agentDraftContext is { } position) attached += $":{position.Line}";
        Button context = agentsOptions.Add(Picker(captured && !compact ? attached : string.Empty, captured ? "close" : "file-code", captured || agentWithFile && file.Length > 0,
            captured ? $"{AgentDraftCaption()} · {Said("agents_panel.remove_context")}" : $"{Said("agents_panel.context")}{(file.Length > 0 ? $" ({file})" : string.Empty)}", () =>
            {
                if (captured) agentDraftContext = null;
                else agentWithFile = !agentWithFile;
                agentsOptionsDrawn = string.Empty;
            }));
        context.Size = new System.Numerics.Vector2(captured && !compact ? 120 : 32, 32);
        context.Enabled = captured || file.Length > 0;
        if (extension is null || options.Count == 0) return;
        AgentOption? thinking = options.FirstOrDefault(option => option.Key is
            "effort" or "reasoning_effort" or "reasoningEffort" or "thinking_level" or "thinkingLevel" or "thinking");
        Button settings = agentsOptions.Add(Picker(compact || thinking is not null ? string.Empty : Said("agents_panel.options"), "settings-gear", false,
            Said("agents_panel.options"), () => { }));
        settings.Size = new System.Numerics.Vector2(compact || thinking is not null ? 32 : 100, 32);
        settings.OnPressed = () =>
        {
            var menu = new Menu("agent options");
            foreach (AgentOption option in options)
            {
                Menu choices = menu.AddMenu(option.Title, option.Icon);
                foreach (var (value, named) in option.Choices)
                {
                    string chosen = value;
                    choices.Add(named, () =>
                    {
                        extensions.SetSetting(extension, option.Key, new StringValue(chosen), tell: true);
                        agentsOptionsDrawn = string.Empty;
                    }).IsChecked = () => Values.Text(extensions.SettingOf(extension, option.Key)) == chosen;
                }
            }
            ContextMenu.Show(chrome, settings.Bounds.PointAt(Origin.TopLeft), menu);
        };
        if (thinking is not null)
        {
            string value = Values.Text(extensions.SettingOf(extension, thinking.Key));
            string current = value.Length == 0 ? Said("agents_panel.thinking_auto")
                : thinking.Choices.FirstOrDefault(choice => choice.Value == value).Label ?? value;
            float room = MathF.Max(32, MathF.Min(96, agentsCompose.Bounds.Width - context.Size.X - settings.Size.X - 48));
            agentsThinking = agentsOptions.Add(Picker(room >= 60 ? current : string.Empty, "lightbulb", false,
                Said("agents_panel.thinking_tip", ("level", current)), () => { }));
            agentsThinking.Size = new System.Numerics.Vector2(room, 32);
            Button thinkingButton = agentsThinking;
            thinkingButton.OnPressed = () =>
            {
                var menu = new Menu(Said("agents_panel.thinking"));
                foreach (var (choiceValue, label) in thinking.Choices)
                {
                    string selected = choiceValue;
                    menu.Add(label, () =>
                    {
                        extensions.SetSetting(extension, thinking.Key, new StringValue(selected), tell: true);
                        agentsOptionsDrawn = string.Empty;
                    }).IsChecked = () => Values.Text(extensions.SettingOf(extension, thinking.Key)) == selected;
                }
                ContextMenu.Show(chrome, thinkingButton.Bounds.PointAt(Origin.TopLeft), menu);
            };
        }
    }

    private static Button Picker(string label, string icon, bool selected, string tooltip, Action pressed) => new()
    {
        Label = label,
        Icon = icon,
        Style = "button_flat",
        LabelScale = 0.135f,
        Size = new System.Numerics.Vector2(0, 32),
        Selected = selected,
        Animated = false,
        Tooltip = tooltip,
        OnPressed = pressed
    };

    /// <summary>
    /// What an agent said with every path:line of a file of the workspace made a link that opens it there. One in
    /// backticks loses them, a link in code isn't one, and what's in a block of code is left as it is.
    /// </summary>
    private string WithPlaces(string text)
    {
        var lines = text.Split('\n');
        char fence = '\0';
        int fenceLength = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            ReadOnlySpan<char> trimmed = lines[i].AsSpan().TrimStart();
            int run = 0;
            if (trimmed.Length > 0 && trimmed[0] is '`' or '~')
                while (run < trimmed.Length && trimmed[run] == trimmed[0]) run++;
            if (run >= 3)
            {
                if (fence == '\0') { fence = trimmed[0]; fenceLength = run; }
                else if (trimmed[0] == fence && run >= fenceLength && trimmed[run..].Trim().IsEmpty) fence = '\0';
                continue;
            }
            if (fence != '\0') continue;

            lines[i] = PlaceInText.Replace(lines[i], match =>
            {
                if (match.Groups["link"].Success) return match.Value;
                Match place = match.Groups["code"].Success ? CodePlace.Match(match.Groups["code"].Value) : match;
                if (!place.Success) return match.Value;
                string path = place.Groups["path"].Value, line = place.Groups["line"].Value;
                return File.Exists(McpPath(path)) ? $"[{path}:{line}]({path}:{line})" : match.Value;
            });
        }

        return string.Join('\n', lines);
    }

    /// <summary>The last thing asked of the chat on screen asked again, as it was asked.</summary>
    private void RetryAgent()
    {
        if (agentChat is not { Running: false } chat || chat.Entries.LastOrDefault(entry => entry.Kind == HexAgentEntryKind.User) is not { } asked
            || !extensions.AgentRuns.TryGetValue(chat.Agent, out var run))
            return;

        StartAgent(chat, run.Extension, run.Run, asked.Text, asked.AskedText ?? chat.Title, asked.ContextLocation);
    }

    /// <summary>What the agent said last in the chat on screen, on the clipboard.</summary>
    private void CopyAgentReply()
    {
        if (agentChat?.Entries.LastOrDefault(entry => entry.Kind == HexAgentEntryKind.Text) is not { } said)
            return;

        UICompositor.Copy(said.Text);
        Say(Said("agents_panel.copied"));
    }

    /// <summary>What the harness has saved this run, in a card, its last line being the whole of it.</summary>
    private void ShowHarnessSaved()
    {
        string described = Harness.Stats.Describe().TrimEnd();
        Notify(described.Split('\n')[^1]);
    }
}
