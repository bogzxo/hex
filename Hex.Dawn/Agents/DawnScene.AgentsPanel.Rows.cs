using System.Numerics;
using System.Text;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// The rows of the Agents view. What you asked on a raised bubble at the right, what the agent said as
// Markdown at the left, what it thought dimmed, every tool it called as a row with an icon by its kind and how long it
// took, opening to say what it was given and what it said back. They're added as they come and only a row that changed
// is made again, so what's there stays put and what's new slides in (DawnScene.AgentsPanel.Motion). Over them the
// agents as chips with a dot for whether they're ready or working. All in the skin's colours.
internal sealed partial class DawnScene
{
    // The widest a bubble of what was asked is, of the width there is
    private const float AGENTS_BUBBLE = 0.86f;

    // The rows on screen, each with the entry and the version of it it was made from
    private readonly List<(HexAgentEntry Entry, int Version, UIComponent Row)> agentRowsShown = [];
    private (HexAgentChat? Chat, int Dropped, int Width, object? Skin) agentRowsFor;
    private Panel? agentsTyping;

    // The time of every tool that's still running, counted up as it goes without its row being made again
    private readonly Dictionary<HexAgentEntry, Label> agentsRunningTimes = [];

    private UISkin? Skin => compositor.Skin;

    private static string HexColour(Vector4 colour) =>
        $"#{(int)(Math.Clamp(colour.X, 0, 1) * 255):x2}{(int)(Math.Clamp(colour.Y, 0, 1) * 255):x2}{(int)(Math.Clamp(colour.Z, 0, 1) * 255):x2}";

    private static void Empty(UIComponent of)
    {
        foreach (UIComponent child in of.Children.ToArray())
            of.Remove(child);
    }

    /// <summary>
    /// How wide the rows have, from the side bar's width in steps of a few pixels, never the scroll's own. A scroll bar
    /// coming and going changed that width, the rows were made again at the new one, came out a different height, the
    /// bar went the other way, and the chat flickered between the two.
    /// </summary>
    private int AgentsWidth() => (int)(treeScroll.Size.X / 8.0f) * 8 - 16;

    private static int IndexIn(UIComponent parent, UIComponent child)
    {
        for (int i = 0; i < parent.Children.Count; i++)
        {
            if (parent.Children[i] == child)
                return i;
        }

        return parent.Children.Count;
    }

    private static Label Text(string text, float scale = 0.13f, bool dim = false, bool markdown = false) =>
        new() { Text = text, TextScale = scale, Dim = dim, Wrap = true, Markdown = markdown, Fill = UIFill.Horizontal, Align = Origin.TopLeft };

    /// <summary>A little round dot, an agent's state on its chip.</summary>
    private static Panel Dot(Vector4 colour) =>
        new() { Size = new Vector2(6, 6), Radius = 3, Color = colour, Anchor = Origin.TopRight, Pivot = Origin.Center, Position = new Vector2(-5, -5) };

    /// <summary>The agents over the chat as chips, drawn again only when one of them changed.</summary>
    private void DrawAgentChips()
    {
        HexAgentProfile[] agents = [.. RunnableAgents()];
        string? current = AgentFor(agentChat);
        var said = new StringBuilder();
        foreach (HexAgentProfile agent in agents)
            said.Append(agent.Id).Append(agent.Id == current).Append(StateOf(agent)).Append('|');
        said.Append(Skin?.GetHashCode());
        if (said.ToString() == agentsChipsDrawn)
            return;
        agentsChipsDrawn = said.ToString();

        Vector4 success = Skin?.Palette.Success ?? new Vector4(0.36f, 0.85f, 0.45f, 1.0f);
        Vector4 accent = Skin?.Palette.Accent ?? new Vector4(0.4f, 0.6f, 1.0f, 1.0f);
        Vector4 dim = Skin?.Palette.DimText ?? new Vector4(1, 1, 1, 0.4f);

        Empty(agentsChips);
        foreach (HexAgentProfile agent in agents)
        {
            string id = agent.Id;
            AgentStates state = StateOf(agent);
            var chip = agentsChips.Add(new Button
            {
                Label = agent.Name,
                Icon = state == AgentStates.Running ? IconOf(agent) + "~pulse" : IconOf(agent),
                Style = "button_flat",
                LabelScale = 0.125f,
                Size = new Vector2(0, 26),
                Selected = id == current,
                Animated = false,
                Tooltip = $"{agent.Name}, {AgentState(agent)}",
                OnPressed = () =>
                {
                    agentPicked = id;
                    if (agentChat is { Entries.Count: > 0 } busy && busy.Agent != id)
                        NewAgentChat();
                    agentsChipsDrawn = string.Empty;
                    agentsInput.Focus();
                }
            });

            // Green when it's ready, the accent breathing while it works, dim when it isn't installed
            Panel dot = chip.Add(Dot(state switch { AgentStates.Running => accent, AgentStates.Missing => dim with { W = 0.5f }, _ => success }));
            if (state == AgentStates.Running)
                Breathe(dot, 0.2f);
        }

        if (agents.Length == 0)
            agentsChips.Add(new Label { Text = Said("agents_panel.none_short"), TextScale = 0.12f, Dim = true });
    }

    private enum AgentStates { Ready, Running, Missing }

    private AgentStates StateOf(HexAgentProfile agent)
    {
        if (agentChats.Any(chat => chat.Running && chat.Agent == agent.Id))
            return AgentStates.Running;
        return extensions.AgentRuns.TryGetValue(agent.Id, out var run) && HexAgentProcess.Find(HexExtensions.Text(run.Run, "program") ?? string.Empty) is null
            ? AgentStates.Missing
            : AgentStates.Ready;
    }

    // Whether an agent can be asked, is busy, or isn't there to run, in words
    private string AgentState(HexAgentProfile agent) => StateOf(agent) switch
    {
        AgentStates.Running => Said("agents_panel.running"),
        AgentStates.Missing => Said("agents_panel.missing"),
        _ => Said("agents_panel.ready")
    };

    private string IconOf(HexAgentProfile agent) =>
        extensions.AgentRuns.TryGetValue(agent.Id, out var run) && HexExtensions.Text(run.Run, "icon") is { Length: > 0 } icon ? icon : "robot";

    /// <summary>
    /// The chat's rows brought up to date. Another chat, a new width or a new theme makes them all again, otherwise a
    /// row is only made again when its entry changed and new ones are added at the end, sliding in.
    /// </summary>
    private void DrawAgentRows()
    {
        HexAgentChat? chat = agentChat;
        var key = (chat, chat?.Dropped ?? 0, AgentsWidth(), (object?)Skin);
        bool fresh = key != agentRowsFor;

        // Only another chat comes in moving, rows made again for a new width or theme are just there
        bool arriving = fresh && chat != agentRowsFor.Chat;
        if (fresh)
        {
            agentRowsFor = key;
            agentRowsShown.Clear();
            agentsRunningTimes.Clear();
            Empty(agentsRows);
            agentsTyping = null;
            if (chat is not { Entries.Count: > 0 })
            {
                DrawAgentWelcome();
                return;
            }
        }

        if (chat is null)
            return;

        for (int i = 0; i < chat.Entries.Count; i++)
        {
            HexAgentEntry entry = chat.Entries[i];
            if (i < agentRowsShown.Count)
            {
                if (agentRowsShown[i].Version == entry.Version)
                    continue;

                // Made again in its place, a tool that finished giving a little pop
                UIComponent was = agentRowsShown[i].Row;
                UIComponent made = RowOf(chat, entry);
                agentsRows.Insert(IndexIn(agentsRows, was), made);
                agentsRows.Remove(was);
                agentRowsShown[i] = (entry, entry.Version, made);
                if (entry.Kind == HexAgentEntryKind.Tool && entry.State != HexAgentToolState.Running)
                    PopRow(made);
                continue;
            }

            UIComponent row = RowOf(chat, entry);
            agentsRows.Insert(agentsTyping is null ? agentsRows.Children.Count : IndexIn(agentsRows, agentsTyping), row);
            agentRowsShown.Add((entry, entry.Version, row));

            // A whole chat brought up comes in quickly a few rows at a time, a row that just came slides in on its own,
            // what was asked from the right where its bubble is
            if (arriving)
                SlideRowIn(row, Math.Min(i, 8) * 0.025f);
            else if (!fresh)
                SlideRowIn(row, 0.0f, entry.Kind == HexAgentEntryKind.User ? 16.0f : 0.0f);
        }

        ShowTyping(chat);
    }

    // When the dots were last wanted and weren't up, they come only once the agent's been quiet a moment
    private long typingWantedSince = -1;

    // How long the agent has to be quiet before the dots come, in milliseconds. Between one tool and the next they came
    // and went in a blink, which looked like a glitch
    private const long TYPING_AFTER_MS = 350;

    // The bouncing dots at the end while the agent's working and nothing of it is going on a row already
    private void ShowTyping(HexAgentChat chat)
    {
        bool quiet = chat.Running && chat.Entries[^1] is not { Kind: HexAgentEntryKind.Tool, State: HexAgentToolState.Running };
        long now = System.Environment.TickCount64;
        if (!quiet)
            typingWantedSince = -1;
        else if (typingWantedSince < 0)
            typingWantedSince = now;
        bool wanted = quiet && (agentsTyping is not null || now - typingWantedSince >= TYPING_AFTER_MS);
        if (wanted && agentsTyping is null)
        {
            agentsTyping = agentsRows.Add(MakeTypingDots());
            BounceDots(agentsTyping);
            SlideRowIn(agentsTyping);
        }
        else if (!wanted && agentsTyping is not null)
        {
            agentsRows.Remove(agentsTyping);
            agentsTyping = null;
        }
        else if (agentsTyping is not null && agentsRows.Children[^1] != agentsTyping)
        {
            agentsRows.MoveChild(agentsTyping, agentsRows.Children.Count);
        }
    }

    // An empty chat, what it's for and a few things to ask to start from, coming in one after another
    private void DrawAgentWelcome()
    {
        if (!RunnableAgents().Any())
        {
            SlideRowIn(agentsRows.Add(Text(Said("agents_panel.no_agents"), 0.13f, dim: true)));
            return;
        }

        SlideRowIn(agentsRows.Add(Text(Said("agents_panel.welcome"), 0.13f, markdown: true)));
        int i = 1;
        foreach (string key in new[] { "agents_panel.try_map", "agents_panel.try_explain", "agents_panel.try_tests" })
        {
            string asked = Said(key);
            Button suggestion = agentsRows.Add(new Button
            {
                Label = asked,
                Icon = "sparkle",
                Style = "button_flat",
                Align = Origin.Left,
                LabelScale = 0.125f,
                Size = new Vector2(0, 28),
                Fill = UIFill.Horizontal,
                OnPressed = () =>
                {
                    agentsInput.Text = asked;
                    SendToAgent();
                }
            });
            SlideRowIn(suggestion, 0.06f * i++);
        }
    }

    private UIComponent RowOf(HexAgentChat chat, HexAgentEntry entry)
    {
        UISkin? skin = Skin;
        switch (entry.Kind)
        {
            case HexAgentEntryKind.User:
                return Bubble(entry, skin);

            case HexAgentEntryKind.Text:
                {
                Label said = Text(WithPlaces(entry.Text), 0.13f, markdown: true);
                said.OnLink = OpenAgentLink;
                return new Panel { Fill = UIFill.Horizontal, Padding = new UIEdges(2, 2, 2, 2) }.With(said);
            }

            case HexAgentEntryKind.Thinking:
                return new Panel { Fill = UIFill.Horizontal, Padding = new UIEdges(2, 0, 2, 0) }.With(Text(entry.Text, 0.12f, dim: true, markdown: true));

            case HexAgentEntryKind.Note:
                return new Panel { Fill = UIFill.Horizontal, Padding = new UIEdges(2, 0, 2, 0) }.With(Text(UIRichText.Literal(entry.Text), 0.11f, dim: true));

            case HexAgentEntryKind.Error:
            {
                Vector4 error = skin?.Palette.Error ?? new Vector4(1, 0.35f, 0.35f, 1);
                var card = new Panel { Fill = UIFill.Horizontal, Color = error with { W = 0.1f }, Radius = 6, Padding = new UIEdges(9, 6, 9, 6) };
                card.Add(Text($"[color:{HexColour(error)}]{UIRichText.Literal(entry.Text)}[/color]", 0.125f));
                return card;
            }

            default:
                return ToolRow(chat, entry, skin);
        }
    }

    // What was asked, a raised bubble at the right, as wide as its longest line and no wider than most of the room
    private UIComponent Bubble(HexAgentEntry entry, UISkin? skin)
    {
        Vector4 raised = skin?.Palette.Raised ?? new Vector4(1, 1, 1, 0.08f);
        (string asked, string? with) = SplitAsked(entry.Text);
        string shown = UIRichText.Literal(asked);
        string? under = with is null ? null : UIRichText.Literal(Said("agents_panel.with_file", ("where", with)));
        const float scale = 0.13f, padding = 36.0f;
        float room = Math.Max(140.0f, AgentsWidth() - 12.0f);
        float longest = room;
        if (skin is not null)
        {
            longest = shown.Split('\n').Max(line => skin.Font.MeasureLine(line, scale, UITextStyle.None, default)) + padding;
            if (under is not null)
                longest = Math.Max(longest, skin.Font.MeasureLine(under, 0.11f, UITextStyle.None, default) + padding);
        }

        var holder = new Panel { Fill = UIFill.Horizontal };
        var bubble = holder.Add(new Panel
        {
            Anchor = Origin.Right,
            Pivot = Origin.Right,
            Size = new Vector2(Math.Clamp(longest, 60.0f, room * AGENTS_BUBBLE), 0),
            Color = raised,
            Radius = 10,
            Padding = new UIEdges(11, 7, 11, 7)
        });
        var stack = bubble.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 2 });
        stack.Add(Text(shown, scale));
        if (under is not null)
            stack.Add(Text(under, 0.11f, dim: true));
        return holder;
    }

    // A tool's row, flat like a row of the explorer. Its icon and name, what it was called with dimmed after it, how long
    // it took and a chevron at the right. Opened, a thin rule down its left with what it was given and said under it as
    // quiet text, a dozen lines of each at the most
    private UIComponent ToolRow(HexAgentChat chat, HexAgentEntry entry, UISkin? skin)
    {
        Vector4 hover = skin?.Palette.Raised ?? new Vector4(1, 1, 1, 0.05f);
        Vector4 rule = skin?.BorderColor ?? new Vector4(1, 1, 1, 0.1f);
        string seconds = entry.Seconds.ToString("0.0");
        string state = entry.State switch
        {
            HexAgentToolState.Running => Said("agents_panel.tool_running", ("seconds", seconds)),
            HexAgentToolState.Failed => Said("agents_panel.tool_failed", ("seconds", seconds)),
            _ => Said("agents_panel.tool_done", ("seconds", seconds))
        };

        // The room the name and its input have, the rest of the row's width is the time and the chevron
        int room = Math.Max(18, (int)((AgentsWidth() - 90.0f) / 6.2f));
        string name = ToolTitle(entry.ToolName ?? "tool");
        var holder = new Panel { Fill = UIFill.Horizontal, Color = entry.Open ? hover with { W = hover.W * 0.6f } : null, Radius = 6 };
        var stack = holder.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 0 });
        var head = stack.Add(new Button
        {
            Label = OneLine($"{name}  {entry.Input}", room),
            Icon = entry.State switch { HexAgentToolState.Running => "loading~spin", HexAgentToolState.Failed => "error~shake", _ => ToolIcon(entry.ToolName ?? string.Empty) },
            Style = "button_flat",
            Align = Origin.Left,
            LabelScale = 0.12f,
            Size = new Vector2(0, 24),
            Fill = UIFill.Horizontal,
            Danger = entry.State == HexAgentToolState.Failed,
            Animated = false,
            Tooltip = $"{entry.ToolName}, {state}",
            Padding = new UIEdges(0, 0, 70, 0),
            OnPressed = () =>
            {
                entry.Open = !entry.Open;
                chat.Touch(entry);
            }
        });

        // How long it took and the chevron that says it opens, at the right of its head
        var right = holder.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 3, Anchor = Origin.TopRight, Pivot = Origin.TopRight, Position = new Vector2(-4, -5) });
        Label time = right.Add(new Label { Text = state, TextScale = 0.1f, Dim = true });
        if (entry.State == HexAgentToolState.Running)
            agentsRunningTimes[entry] = time;
        else
            agentsRunningTimes.Remove(entry);
        right.Add(new Label { Text = entry.Open ? "$(chevron-down)" : "$(chevron-right)", TextScale = 0.11f, Dim = true });
        if (entry.State == HexAgentToolState.Running)
            Breathe(head, 0.6f);

        if (entry.Open)
        {
            var opened = stack.Add(new Panel { Fill = UIFill.Horizontal, Padding = new UIEdges(26, 0, 8, 8) });
            opened.Add(new Panel { Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Position = new Vector2(13, 0), Size = new Vector2(1, 0), Fill = UIFill.Vertical, Color = rule });
            var said = opened.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 2 });
            if (!string.IsNullOrWhiteSpace(entry.Input))
            {
                said.Add(Text(Said("agents_panel.given"), 0.1f, dim: true).Also(label => label.Bold = true));
                said.Add(Text(UIRichText.Literal(Lines(entry.Input!, 12)), 0.11f, dim: true));
            }
            if (!string.IsNullOrWhiteSpace(entry.Output))
            {
                said.Add(Text(Said("agents_panel.said"), 0.1f, dim: true).Also(label => label.Bold = true));
                said.Add(Text(UIRichText.Literal(Lines(entry.Output!, 12)), 0.11f, dim: true));
            }
        }

        return holder;
    }

    /// <summary>The times of the tools still running brought up to now, a few times a second.</summary>
    private void TickRunningTimes()
    {
        foreach (var (entry, label) in agentsRunningTimes)
        {
            if (entry.State == HexAgentToolState.Running)
                label.Text = Said("agents_panel.tool_running", ("seconds", entry.Seconds.ToString("0.0")));
        }
    }

    /// <summary>A text cut to so many lines, the rest said as how many more there were.</summary>
    internal static string Lines(string text, int most)
    {
        string[] lines = text.Replace("\r", string.Empty).Trim('\n').Split('\n');
        return lines.Length <= most ? string.Join('\n', lines) : string.Join('\n', lines.Take(most)) + $"\n... {lines.Length - most} more lines";
    }


    /// <summary>A text as one line of so many characters at the most.</summary>
    internal static string OneLine(string text, int most)
    {
        string line = string.Join(' ', text.Split(['\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(part => part.Trim())).Trim();
        return line.Length > most ? line[..(most - 3)] + "..." : line;
    }

    /// <summary>A tool's name as a row says it, Hex's without the mcp__hex__ a host puts in front.</summary>
    internal static string ToolTitle(string name)
    {
        int at = name.LastIndexOf("__", StringComparison.Ordinal);
        return name.StartsWith("mcp__", StringComparison.Ordinal) && at > 4 ? name[(at + 2)..] : name;
    }

    /// <summary>The icon of a tool by what kind of tool it is, a command, a file changed or read, a search, Hex's own.</summary>
    internal static string ToolIcon(string name)
    {
        string tool = ToolTitle(name).ToLowerInvariant();
        return tool switch
        {
            "bash" or "command" or "command_execution" or "shell" or "hex_terminal_run" or "hex_terminal" or "hex_terminal_send" or "hex_run_task" => "terminal",
            "edit" or "write" or "multiedit" or "file_change" or "apply_patch" or "notebookedit" => "diff",
            "read" or "hex_read" or "hex_skim" or "hex_editor" or "hex_open" => "file-code",
            "grep" or "glob" or "hex_search" or "hex_find_in_files" or "hex_files" or "hex_refs" or "hex_references" => "search",
            "hex_symbols" or "hex_outline" or "hex_definition" or "hex_symbol_info" => "symbol-class",
            "hex_map" => "list-tree",
            "websearch" or "webfetch" or "web_search" => "globe",
            "todowrite" or "todo_list" => "checklist",
            "task" or "agent" => "organization",
            _ when tool.StartsWith("hex_notebook", StringComparison.Ordinal) => "notebook",
            _ when tool.StartsWith("hex_", StringComparison.Ordinal) => "robot",
            _ => "tools"
        };
    }

    // What was asked split from the file put in front of it, which the bubble says under it rather than in it
    private static (string Asked, string? With) SplitAsked(string text)
    {
        const string marker = "\n\n---\n";
        int at = text.IndexOf(marker, StringComparison.Ordinal);
        if (!text.StartsWith("[from ", StringComparison.Ordinal) || at < 0)
            return (text, null);
        int close = text.IndexOf(']');
        return (text[(at + marker.Length)..], close > 6 ? text[6..close] : null);
    }

    // A link in what an agent said, a file of the workspace opened (at a line when it says one), anything else left alone
    private void OpenAgentLink(string target)
    {
        string path = target;
        int line = 0;
        int colon = target.LastIndexOf(':');
        if (colon > 2 && int.TryParse(target[(colon + 1)..], out int number))
            (path, line) = (target[..colon], number);

        string full = McpPath(path);
        if (File.Exists(full))
            OpenAt(full, Math.Max(0, line - 1), 0);
    }
}

/// <summary>A component with a child put in it, for building rows in one line.</summary>
internal static class UIComponentWith
{
    public static T With<T>(this T parent, UIComponent child) where T : UIComponent
    {
        parent.Add(child);
        return parent;
    }

    public static T Also<T>(this T made, Action<T> change)
    {
        change(made);
        return made;
    }
}
