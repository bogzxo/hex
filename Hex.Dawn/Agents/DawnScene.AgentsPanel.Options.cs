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
    // Emptied for the row to be made again, otherwise the word that it was, the number of what it was made from beside it
    private string agentsOptionsDrawn = string.Empty;
    private long agentsOptionsKey;
    private Button? agentsThinking;

    // What was asked in this run, the newest last, and how far back Up has gone (Count for not at all)
    private readonly List<string> agentsAsked = [];
    private int agentsRecall;

    // A path with a line, Hex.Core/Mcp/McpServer.cs:47, in what an agent says, in backticks or not
    // Made when Hex is built, a compiled one is only read by the interpreter in the native build, and every row an agent
    // writes goes through it
    [GeneratedRegex(@"(?<link>!?\[[^\]\r\n]*\]\((?:[^()\r\n]|\([^()\r\n]*\))*\))|(?<ticks>`+)(?<code>.*?)\k<ticks>|(?<path>(?:/?[A-Za-z]:)?[\w.\-/\\]+\.\w{1,6}):(?<line>\d+)")]
    private static partial Regex PlaceInText { get; }

    [GeneratedRegex(@"\A(?<path>(?:/?[A-Za-z]:)?[\w.\-/\\]+\.\w{1,6}):(?<line>\d+)\z")]
    private static partial Regex CodePlace { get; }

    private void BuildAgentsOptions(UILayout made)
    {
        agentsOptions = made.Get<StackPanel>("agents_options");

        // Up and Down in the box go through what was asked before, Escape stops what's going on
        // Only from the box's first line up and its last line down, between its lines they move the caret
        // The / commands' list takes them first in the box itself, where a held key repeats and the caret stays put. As a
        // shortcut it moved once a press, and Up took the caret to the start of the line too, which closed the list
        agentsInput.OnUpDown = MoveSlash;
        commands.Keys.Add(Key.Up, () => agentsSlash is null && compositor.Focus == agentsInput && agentsInput.Document.Caret.Line == 0 && RecallAsked(-1), whileTyping: true, first: true);
        commands.Keys.Add(Key.Down, () => agentsSlash is null && compositor.Focus == agentsInput && agentsInput.Document.Caret.Line == agentsInput.Document.LineCount - 1 && RecallAsked(1), whileTyping: true, first: true);
        agentsInput.OnTab = () => TakeFirstSlash() || TakeFirstMention();
        commands.Keys.Add(Key.Escape, () =>
        {
            if (DismissSlash()) return true;
            // The window of what the harness saved goes first, wherever the keyboard is
            if (harnessWindow is { } open)
            {
                open.Close();
                return true;
            }

            // Only the person stops an agent with Escape, an agent's own Escape is for closing what it opened
            if (compositor.Focus != agentsInput || agentChat?.Running != true || agentKeysPressing > 0)
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

    /// <summary>
    /// What the row of pickers is made from, as a number: the width it's compact under, the agent, the file and what's
    /// attached, and every option with its value and its choices. The choices count, an agent system says its models as it
    /// starts (Claude Code's handshake) and the menu made with the old ones kept them. Read the way OptionsOf reads them,
    /// nothing made.
    /// </summary>
    private long AgentOptionsKey(string? agent, HexExtension? extension, bool compact)
    {
        static long Mix(long key, string? text) => key * 1_000_003 + (text is null ? 7 : string.GetHashCode(text, StringComparison.Ordinal));

        long key = Mix(compact ? 3 : 5, agent) * 31 + (agentWithFile ? 1 : 0) + LinkRunsOnKey() * 7;
        key = key * 1_000_003 + (document?.Path is { } path && document.IsText ? string.GetHashCode(Path.GetFileName(path.AsSpan()), StringComparison.Ordinal) : 0);
        if (agentDraftContext is { } context)
            key = Mix(key, context.Path) * 31 + HashCode.Combine(context.Line, context.Column, context.EndLine, context.EndColumn);
        if (agent is null || !extensions.AgentRuns.TryGetValue(agent, out var run) || !run.Run.Properties.TryGetValue("options", out IRuntimeValue? listed) || listed is not ListValue list)
            return key;

        foreach (IRuntimeValue item in list.Items)
        {
            if (item is not ObjectValue option || HexExtensions.Text(option, "id") is not { } id || !option.Properties.TryGetValue("choices", out IRuntimeValue? choices) || choices is not ListValue each)
                continue;
            key = Mix(Mix(key, id), extension is null ? string.Empty : AgentOptionValue(extension, id));
            foreach (IRuntimeValue choice in each.Items)
            {
                if (choice is not ObjectValue made)
                    continue;
                key = Mix(Mix(key, Values.Text(made.Properties.GetValueOrDefault("value") ?? Values.Null)), HexExtensions.Text(made, "label"));
            }
        }

        return key;
    }

    private string AgentOptionValue(HexExtension extension, string key) =>
        agentChat?.Options.GetValueOrDefault(key) ?? Values.Text(extensions.SettingOf(extension, key));

    private void SetAgentOption(HexExtension extension, string key, string value)
    {
        if (agentChat is { } chat) chat.Options[key] = value;
        else extensions.SetSetting(extension, key, new StringValue(value), tell: true);
        agentsOptionsDrawn = string.Empty;
    }

    /// <summary>The row of pickers over the box, made again when the agent, a choice or the file on screen changes.</summary>
    private void DrawAgentOptions()
    {
        string? agent = AgentFor(agentChat);
        HexExtension? extension = agent is not null && extensions.AgentRuns.TryGetValue(agent, out var run) ? run.Extension : null;
        bool compact = agentsCompose.Bounds.Width < 300;

        // What the row is made from as a number, worked out from the extension's values where they are. Ten times a second
        // the options were made again from them and written out as a string with every choice in it, to find nothing moved
        long said = AgentOptionsKey(agent, extension, compact);
        if (agentsOptionsDrawn.Length > 0 && said == agentsOptionsKey) return;
        agentsOptionsDrawn = "drawn";
        agentsOptionsKey = said;
        List<AgentOption> options = OptionsOf(agent);
        string file = document?.Path is { } path && document.IsText ? Path.GetFileName(path) : string.Empty;
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
        // Where a new chat runs, and another device's own pickers in place of these when it's one of those (DawnScene.LinkRunsOn)
        if (DrawLinkRunsOn())
            return;
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
                        SetAgentOption(extension, option.Key, chosen);
                        agentsOptionsDrawn = string.Empty;
                    }).IsChecked = () => AgentOptionValue(extension, option.Key) == chosen;
                }
            }
            ContextMenu.Show(chrome, settings.Bounds.PointAt(Origin.TopLeft), menu);
        };
        // The model the next turn runs on, a picker of its own by the box as the thinking is, it was only in the gear's menu
        // and the person had to go into it to see which one the chat would ask
        float taken = 0.0f;
        if (options.FirstOrDefault(option => option.Key == "model") is { } models && models.Choices.Length > 0)
        {
            string value = AgentOptionValue(extension, models.Key);
            string current = value.Length == 0 ? Said("agents_panel.model_default")
                : models.Choices.FirstOrDefault(choice => choice.Value == value).Label ?? value;
            float wide = MathF.Ceiling((Skin?.Font.Measure(current, 0.135f).X ?? current.Length * 7.0f) + 44.0f);
            float room = MathF.Max(32, MathF.Min(MathF.Min(wide, 180), agentsCompose.Bounds.Width - context.Size.X - settings.Size.X - 150));
            Button modelButton = agentsOptions.Add(Picker(room >= 80 ? current : string.Empty, "sparkle", false,
                Said("agents_panel.model_tip", ("model", current)), () => { }));
            modelButton.Size = new System.Numerics.Vector2(room, 32);
            taken = room;
            modelButton.OnPressed = () =>
            {
                var menu = new Menu(models.Title);
                foreach (var (choiceValue, label) in models.Choices)
                {
                    string selected = choiceValue;
                    menu.Add(label, () =>
                    {
                        SetAgentOption(extension, models.Key, selected);
                        agentsOptionsDrawn = string.Empty;
                    }).IsChecked = () => AgentOptionValue(extension, models.Key) == selected;
                }
                ContextMenu.Show(chrome, modelButton.Bounds.PointAt(Origin.TopLeft), menu);
            };
        }

        if (thinking is not null)
        {
            string value = AgentOptionValue(extension, thinking.Key);
            string current = value.Length == 0 ? Said("agents_panel.thinking_auto")
                : thinking.Choices.FirstOrDefault(choice => choice.Value == value).Label ?? value;
            float room = MathF.Max(32, MathF.Min(96, agentsCompose.Bounds.Width - context.Size.X - settings.Size.X - taken - 48));
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
                        SetAgentOption(extension, thinking.Key, selected);
                        agentsOptionsDrawn = string.Empty;
                    }).IsChecked = () => AgentOptionValue(extension, thinking.Key) == selected;
                }
                ContextMenu.Show(chrome, thinkingButton.Bounds.PointAt(Origin.TopLeft), menu);
            };
            taken += room;
        }

        // What the agent may do without asking, out of the gear's menu, where nobody saw whether the next turn would ask or
        // just act. Its word when there's room, the shield always, a warning in its place when it asks for nothing at all
        if (options.FirstOrDefault(option => PERMISSION_KEYS.Contains(option.Key)) is { Choices.Length: > 0 } permission)
        {
            string value = AgentOptionValue(extension, permission.Key);
            string current = value.Length == 0 ? Said("agents_panel.model_default")
                : permission.Choices.FirstOrDefault(choice => choice.Value == value).Label ?? value;
            bool open = UNGUARDED.Contains(value);
            float wide = MathF.Ceiling((Skin?.Font.Measure(current, 0.135f).X ?? current.Length * 7.0f) + 44.0f);
            float room = MathF.Max(32, MathF.Min(MathF.Min(wide, 140), agentsCompose.Bounds.Width - context.Size.X - settings.Size.X - taken - 48));
            Button permissionButton = agentsOptions.Add(Picker(room >= 80 ? current : string.Empty, open ? "warning" : "shield", false,
                Said("agents_panel.permission_tip", ("mode", current)), () => { }));
            permissionButton.Size = new System.Numerics.Vector2(room, 32);
            // A button's label is plain text, the warning's colour is its own, not markup in the words
            if (open && Skin is { } skin)
            {
                permissionButton.LabelColor = skin.Palette.Warning;
                permissionButton.IconColor = skin.Palette.Warning;
            }
            agentsPermission = permissionButton;
            permissionButton.OnPressed = () =>
            {
                var menu = new Menu(permission.Title);
                foreach (var (choiceValue, label) in permission.Choices)
                {
                    string selected = choiceValue;
                    menu.Add(label, () =>
                    {
                        SetAgentOption(extension, permission.Key, selected);
                        agentsOptionsDrawn = string.Empty;
                    }, icon: UNGUARDED.Contains(choiceValue) ? "warning" : string.Empty).IsChecked = () => AgentOptionValue(extension, permission.Key) == selected;
                }
                ContextMenu.Show(chrome, permissionButton.Bounds.PointAt(Origin.TopLeft), menu);
            };
        }
    }

    // The option keys agent systems name what may be done without asking by, and the values that ask for nothing at all
    private static readonly string[] PERMISSION_KEYS = ["permission_mode", "permissionMode", "mode", "sandbox", "approval_policy"];
    private static readonly string[] UNGUARDED = ["bypassPermissions", "anything", "danger-full-access", "never"];

    // The permission picker as last drawn, for the tests
    private Button? agentsPermission;

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
        // Words with nothing a place could start with are as they are, nothing split, matched or joined for them
        if (text.AsSpan().IndexOfAny(PlaceMarks) < 0)
            return text;

        var lines = text.Split('\n');
        char fence = '\0';
        int fenceLength = 0;
        bool changed = false;
        MatchEvaluator linked = match =>
        {
            if (match.Groups["link"].Success) return match.Value;
            Match place = match.Groups["code"].Success ? CodePlace.Match(match.Groups["code"].Value) : match;
            if (!place.Success) return match.Value;
            string path = place.Groups["path"].Value, line = place.Groups["line"].Value;
            return AgentFilePath(path) is not null ? $"[{path}:{line}]({path}:{line})" : match.Value;
        };
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
            if (fence != '\0' || lines[i].AsSpan().IndexOfAny(PlaceMarks) < 0) continue;

            string was = lines[i];
            lines[i] = PlaceInText.Replace(was, linked);
            changed |= !ReferenceEquals(lines[i], was);
        }

        return changed ? string.Join('\n', lines) : text;
    }

    // What every match of PlaceInText has in it, a link's bracket, a backtick or the colon before a line
    private static readonly System.Buffers.SearchValues<char> PlaceMarks = System.Buffers.SearchValues.Create("[`:");

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

    // The window of what the harness saved while it's up, so a second press closes it
    private WindowPanel? harnessWindow;
}
