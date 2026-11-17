using System.Numerics;

using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// Tool calls one after another, with nothing said between them, as one row folded by default. Its head says how many
// there were and of what, or the one running now, and opened it lists each as a slim line, which opens in turn to what it
// was given and said back. A turn of forty reads and searches was forty bubbles down the chat, the words around them lost
// between. A subagent keeps a row of its own, what it was sent to do matters more than a call.
internal sealed partial class DawnScene
{
    // How tall a call's line is in an opened group, and the group's head
    private const float TOOL_LINE = 22.0f, TOOL_GROUP_HEAD = 26.0f;

    // The groups opened, by the first call of each, the rest are folded
    private readonly HashSet<HexAgentEntry> toolGroupsOpen = [];

    // The time of each group with a call running in it, by its first call, the label and the calls it adds up
    private readonly Dictionary<HexAgentEntry, (Label Time, List<HexAgentEntry> Calls)> agentsGroupTimes = [];

    // A row pressed open or shut and where its top was on screen then, held there for a few ticks while it's made again
    private (HexAgentEntry Entry, float Top, int Ticks)? agentRowHeld;

    /// <summary>
    /// The row of an entry kept where it is on screen as it opens or folds, the chat scrolled as much as what's above it
    /// moved. Made again in its place it's laid out a frame later, and the chat jumped and came back meanwhile.
    /// </summary>
    private void HoldRowInPlace(HexAgentEntry entry, UIComponent pressed)
    {
        // Measured as the chat's own row it's in, a call's line in a group being part of the group's row
        UIComponent row = pressed;
        while (row.Parent is { } up && up != agentsRows)
            row = up;
        // Held for a few frames more than it takes, a busy frame laying it out a step late
        agentRowHeld = (entry, row.Bounds.Max.Y, 8);
    }

    /// <summary>Every tick, the held row put back where its top was, by scrolling the chat the difference.</summary>
    private void KeepHeldRow()
    {
        if (agentRowHeld is not { Ticks: > 0 } held)
            return;
        agentRowHeld = held with { Ticks = held.Ticks - 1 };

        // The row it's in now, or the group's that it belongs to
        UIComponent? row = null;
        for (int i = 0; i < agentRowsShown.Count && row is null; i++)
        {
            if (!ReferenceEquals(agentRowsShown[i].Entry, held.Entry))
                continue;
            // A call drawn by its group's row has a row of nothing of its own, the group's is the one before it that shows
            int at = i;
            while (at > 0 && !agentRowsShown[at].Row.Visible)
                at--;
            row = agentRowsShown[at].Row;
        }

        if (row is null || row.Bounds.IsEmpty || !row.Visible)
            return;
        float moved = held.Top - row.Bounds.Max.Y;
        if (MathF.Abs(moved) >= 0.5f)
            agentsScroll.Offset = Math.Clamp(agentsScroll.Offset + moved, 0.0f, agentsScroll.MaxOffset);
    }

    /// <summary>The place in the list of a call drawn by its group's first row, a row of nothing taking no room.</summary>
    private static Panel GroupedPlace() => new() { Visible = false, Size = Vector2.Zero };

    /// <summary>Whether an entry goes in a group of calls, a tool call that isn't a subagent.</summary>
    private static bool Groupable(HexAgentEntry entry) => entry.Kind == HexAgentEntryKind.Tool && SubagentOf(entry) is null;

    /// <summary>The calls of the group that starts at an entry, it and the groupable ones straight after it.</summary>
    private static List<HexAgentEntry> ToolGroupAt(HexAgentChat chat, int start)
    {
        var group = new List<HexAgentEntry>();
        for (int i = start; i < chat.Entries.Count && Groupable(chat.Entries[i]); i++)
            group.Add(chat.Entries[i]);
        return group;
    }

    /// <summary>Whether an entry is one of a group that started before it, drawn by the group's first row.</summary>
    private static bool InToolGroup(HexAgentChat chat, int index) =>
        index > 0 && Groupable(chat.Entries[index]) && Groupable(chat.Entries[index - 1]);

    /// <summary>What a group is drawn from, its calls and how each is, and whether it's open, so it's made again when that moves.</summary>
    private int ToolGroupKey(HexAgentChat chat, int start)
    {
        var hash = new HashCode();
        foreach (HexAgentEntry call in ToolGroupAt(chat, start))
        {
            hash.Add(call);
            hash.Add(call.Version);
        }

        hash.Add(toolGroupsOpen.Contains(chat.Entries[start]));
        return hash.ToHashCode();
    }

    /// <summary>
    /// A group of calls. One alone is its line. More are a head saying how many and of what, or the one running, a chevron
    /// opening it to a line for each.
    /// </summary>
    private UIComponent ToolGroupRow(HexAgentChat chat, HexAgentEntry first, UISkin? skin)
    {
        int start = -1;
        for (int i = 0; i < chat.Entries.Count; i++)
        {
            if (ReferenceEquals(chat.Entries[i], first))
            {
                start = i;
                break;
            }
        }

        List<HexAgentEntry> calls = start < 0 ? [first] : ToolGroupAt(chat, start);
        if (calls.Count == 1)
            return ToolLine(chat, first, skin);

        Vector4 raised = skin?.Palette.Raised ?? new Vector4(1, 1, 1, 0.05f);
        bool open = toolGroupsOpen.Contains(first);
        var holder = new StackPanel { Fill = UIFill.Horizontal, Spacing = 1 };

        HexAgentEntry? running = calls.LastOrDefault(call => call.State == HexAgentToolState.Running);
        int failed = calls.Count(call => call.State == HexAgentToolState.Failed);
        string kinds = string.Join(", ", calls.GroupBy(call => ShortToolName(call)).OrderByDescending(kind => kind.Count())
            .Take(4).Select(kind => kind.Count() > 1 ? $"{kind.Key} ×{kind.Count()}" : kind.Key));
        double seconds = calls.Sum(call => call.Seconds);
        string icon = running is not null ? (settings.UiAnimation > 0 ? "loading~spin" : "loading") : failed > 0 ? "error" : "tools";
        string head = running is not null
            ? Said("agents_panel.tools_running", ("count", calls.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("tool", ShortToolName(running)))
            : Said("agents_panel.tools_group", ("count", calls.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("kinds", kinds));

        var bar = holder.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, TOOL_GROUP_HEAD), Radius = 6, Color = raised with { W = raised.W * 0.45f } });
        // A blue gutter down its left while one of its calls is running, so what's going on now stands out of what's done
        if (running is not null)
            bar.Add(new Panel { Anchor = Origin.Left, Pivot = Origin.Left, Size = new Vector2(3, TOOL_GROUP_HEAD - 6), Position = new Vector2(1, 0), Radius = 1.5f, Color = RUNNING_BLUE });
        var button = bar.Add(new Button
        {
            Style = "button_flat", Fill = UIFill.Both, Animated = false, Align = Origin.Left, LabelScale = 0.13f, Icon = icon,
            Padding = new UIEdges(6, 0, 70, 0), Tooltip = string.Join("\n", calls.Select(call => $"{ShortToolName(call)} {OneLine(ToolPreview(call.Input), 80)}")),
            OnPressed = () =>
            {
                if (!toolGroupsOpen.Remove(first))
                    toolGroupsOpen.Add(first);
                HoldRowInPlace(first, bar);
                chat.Touch(first);
            }
        });
        FollowWidth(width =>
        {
            string fitted = UIRichText.Literal(OneLine(head, Math.Max(6, (int)((width - 90.0f) / 6.0f))));
            if (button.Label != fitted)
                button.Label = fitted;
        });
        var right = bar.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 5, Anchor = Origin.Right, Pivot = Origin.Right, Position = new Vector2(-8, 0) });
        Label time = right.Add(new Label { Text = failed > 0 ? Said("agents_panel.tools_failed", ("count", failed.ToString(System.Globalization.CultureInfo.InvariantCulture))) : $"{seconds:0.0}s", TextScale = 0.115f, Dim = failed == 0 });
        // Its time goes on with the call running in it, a few times a second (TickRunningTimes)
        if (running is not null && failed == 0)
            agentsGroupTimes[first] = (time, calls);
        else
            agentsGroupTimes.Remove(first);
        right.Add(new Label { Text = open ? "$(chevron-down)" : "$(chevron-right)", TextScale = 0.12f, Dim = true });

        if (open)
        {
            var lines = holder.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 1, Padding = new UIEdges(10, 0, 0, 0) });
            foreach (HexAgentEntry call in calls)
                lines.Add(ToolLine(chat, call, skin));
        }

        return holder;
    }

    // A call's name as a line says it, Hex's without hex_ and in words
    private static string ShortToolName(HexAgentEntry call)
    {
        string name = ToolTitle(call.ToolName ?? "tool");
        if (name.StartsWith("hex_", StringComparison.Ordinal))
            name = name[4..];
        name = name.Replace('_', ' ');
        return string.IsNullOrWhiteSpace(name) ? "Tool" : char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>
    /// One call as a slim line, its icon, its name and what it was given dimmed after it, its time at the right, a thin mark
    /// of how it went at its left. A press opens what it was given and said back under it.
    /// </summary>
    private UIComponent ToolLine(HexAgentChat chat, HexAgentEntry call, UISkin? skin)
    {
        var holder = new StackPanel { Fill = UIFill.Horizontal, Spacing = 0 };
        var line = holder.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, TOOL_LINE), Color = Vector4.Zero });
        // How it went, a thin mark at its left, a wider blue gutter while it's running
        bool going = call.State == HexAgentToolState.Running;
        line.Add(new Panel { Anchor = Origin.Left, Pivot = Origin.Left, Size = new Vector2(going ? 3 : 2, TOOL_LINE - 6), Radius = 1, Color = going ? RUNNING_BLUE : ToolStateColour(call.State, skin) });
        string icon = call.State switch
        {
            HexAgentToolState.Running => settings.UiAnimation > 0 ? "loading~spin" : "loading",
            HexAgentToolState.Failed => "error",
            _ => ToolIcon(call.ToolName ?? string.Empty)
        };
        string name = ShortToolName(call), preview = ToolPreview(call.Input);
        var button = line.Add(new Button
        {
            Style = "button_flat", Fill = UIFill.Both, Animated = false, Align = Origin.Left, LabelScale = 0.125f, Icon = icon,
            Padding = new UIEdges(8, 0, 56, 0), Tooltip = $"{call.ToolName}\n{preview}",
            OnPressed = () =>
            {
                call.Open = !call.Open;
                HoldRowInPlace(call, line);
                chat.Touch(call);
            }
        });
        FollowWidth(width =>
        {
            int room = Math.Max(6, (int)((width - 90.0f) / 6.0f));
            // A button's label takes no colours, so what it was given follows the name after a wider gap
            string fitted = UIRichText.Literal(name + (preview.Length > 0 && room > name.Length + 4 ? "   " + OneLine(preview, room - name.Length - 3) : string.Empty));
            if (button.Label != fitted)
                button.Label = fitted;
        });

        string seconds = call.Seconds.ToString("0.0");
        Label time = line.Add(new Label
        {
            Text = call.State == HexAgentToolState.Running ? Said("agents_panel.tool_running", ("seconds", seconds)) : $"{seconds}s",
            TextScale = 0.11f, Dim = call.State != HexAgentToolState.Failed, Anchor = Origin.Right, Pivot = Origin.Right, Position = new Vector2(-8, 0)
        });
        if (call.State == HexAgentToolState.Running)
            agentsRunningTimes[call] = time;
        else
            agentsRunningTimes.Remove(call);

        if (call.Open)
        {
            var opened = holder.Add(new Panel { Fill = UIFill.Horizontal, Padding = new UIEdges(12, 6, 12, 8), Color = skin?.FieldColor, Radius = 6 });
            var said = opened.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 4 });
            if (!string.IsNullOrWhiteSpace(call.Input))
            {
                said.Add(Text(Said("agents_panel.given"), 0.125f, dim: true).Also(label => label.Bold = true));
                said.Add(Text("```json\n" + Lines(ToolDetails(call.Input!), 12) + "\n```", 0.16f, markdown: true).Also(label => label.CodeFont = code.Font));
            }
            if (!string.IsNullOrWhiteSpace(call.Output))
            {
                said.Add(Text(Said("agents_panel.said"), 0.125f, dim: true).Also(label => label.Bold = true));
                said.Add(Text(UIRichText.Literal(Lines(call.Output!, 12)), 0.15f));
            }
        }

        return holder;
    }
}
