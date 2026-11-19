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
    private const float TOOL_LINE = 24.0f, TOOL_GROUP_HEAD = 26.0f;

    // How strongly the calls that went are drawn, the reply round them standing out of them
    private const float TOOL_QUIET = 0.72f;

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
        while (row.Parent is { } up && up != agentsScroll)
            row = up;
        // Held for a few frames more than it takes, a busy frame laying it out a step late
        agentRowHeld = (entry, row.Bounds.Max.Y, 8);

        // At the chat's end the scroll sticks there, and what opened pushed it to the new end before the hold pulled it
        // back, a jump up and down. It doesn't stick while the row's held
        if (agentsStickHeld is null)
            agentsStickHeld = agentsScroll.StickToEnd;
        agentsScroll.StickToEnd = false;
    }

    // Whether the chat stuck to its end before a row was held, put back once the hold is over
    private bool? agentsStickHeld;

    /// <summary>Every tick, the held row put back where its top was, by scrolling the chat the difference.</summary>
    private void KeepHeldRow()
    {
        if (agentRowHeld is not { Ticks: > 0 } held)
        {
            if (agentsStickHeld is { } stuck)
            {
                agentsScroll.StickToEnd = stuck;
                agentsStickHeld = null;
            }
            return;
        }
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

        // What it opened to that's gone under the bottom of the chat is brought up into sight, as far as its top going to
        // the top, so the last group of a chat doesn't open off screen. Its place to hold moves up with it
        UIRect view = agentsScroll.Bounds;
        float under = view.Min.Y - (row.Bounds.Min.Y + moved);
        float room = view.Max.Y - held.Top;
        float up = MathF.Min(MathF.Max(0.0f, under + 6.0f), MathF.Max(0.0f, room));
        if (up > 0.5f && under > 0.0f)
        {
            moved += up;
            agentRowHeld = held with { Top = held.Top + up, Ticks = held.Ticks - 1 };
        }

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

        // Not whether it's open, a press opens it in place without the row being made again
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
        // One that failed on the way and was got past (its last call went) is a warning, a group that ended failing an error
        bool endedFailing = calls[^1].State == HexAgentToolState.Failed;
        string icon = running is not null ? (settings.UiAnimation > 0 ? "loading~spin" : "loading") : endedFailing ? "error" : failed > 0 ? "warning" : "tools";
        string head = running is not null
            ? Said("agents_panel.tools_running", ("count", calls.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("tool", ShortToolName(running)))
            : Said("agents_panel.tools_group", ("count", calls.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("kinds", kinds));

        var bar = holder.Add(new Panel
        {
            Fill = UIFill.Horizontal, Size = new Vector2(0, TOOL_GROUP_HEAD), Radius = 6, Color = raised with { W = raised.W * 0.45f },
            Opacity = running is null && failed == 0 ? TOOL_QUIET : 1.0f
        });
        // How it's going the way a tool's own row says it, an outline while one of its calls runs or once it ended failing,
        // none once it went. How many failed on the way is said in words at its right
        bar.Outline = ToolOutline(running is not null ? HexAgentToolState.Running : endedFailing ? HexAgentToolState.Failed : HexAgentToolState.Done, skin);
        var button = bar.Add(new Button
        {
            Style = "button_flat", Fill = UIFill.Both, Animated = false, Align = Origin.Left, LabelScale = 0.13f, Icon = icon,
            Padding = new UIEdges(6, 0, 70, 0), Tooltip = string.Join("\n", calls.Select(call => $"{ShortToolName(call)} {OneLine(ToolPreview(call.Input), 80)}")),
        });
        Label chevron = null!;
        StackPanel? lines = null;
        // Opened and folded in place, its lines put in under the head or taken out, the row not made again. Made again,
        // the new row wasn't laid out for a frame, the chat was held to the wrong place and jumped up and back
        void Lay(bool opening)
        {
            if (opening)
            {
                lines ??= MakeLines();
                if (lines.Parent is null)
                    holder.Add(lines);
            }
            else
                lines?.Parent?.Remove(lines);
            chevron.Text = opening ? "$(chevron-down)" : "$(chevron-right)";
        }
        StackPanel MakeLines()
        {
            var made = new StackPanel { Fill = UIFill.Horizontal, Spacing = 1, Padding = new UIEdges(10, 0, 0, 0) };
            foreach (HexAgentEntry call in calls)
                made.Add(ToolLine(chat, call, skin));
            return made;
        }
        button.OnPressed = () =>
        {
            bool opening = !toolGroupsOpen.Remove(first);
            if (opening)
                toolGroupsOpen.Add(first);
            HoldRowInPlace(first, bar);
            Lay(opening);
        };
        FollowWidth(width =>
        {
            string fitted = UIRichText.Literal(OneLine(head, Math.Max(6, (int)((width - 90.0f) / 6.0f))));
            if (button.Label != fitted)
                button.Label = fitted;
        });
        var right = bar.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 5, Anchor = Origin.Right, Pivot = Origin.Right, Position = new Vector2(-8, 0) });
        Label time = right.Add(new Label { Text = failed > 0 ? Said("agents_panel.tools_failed", ("count", failed.ToString(System.Globalization.CultureInfo.InvariantCulture))) : seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s", TextScale = 0.125f, Dim = failed == 0 });
        // Its time goes on with the call running in it, a few times a second (TickRunningTimes)
        if (running is not null && failed == 0)
            agentsGroupTimes[first] = (time, calls);
        else
            agentsGroupTimes.Remove(first);
        chevron = right.Add(new Label { Text = open ? "$(chevron-down)" : "$(chevron-right)", TextScale = 0.12f, Dim = true });
        if (open)
            Lay(true);

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
        // A call that went is quieter than the words round it, the agent's reply is what the chat is read for. One running
        // or failed stays as it is, it's what wants seeing
        var line = holder.Add(new Panel
        {
            Fill = UIFill.Horizontal, Size = new Vector2(0, TOOL_LINE), Color = Vector4.Zero,
            Opacity = call.State == HexAgentToolState.Done ? TOOL_QUIET : 1.0f
        });
        // How it went by its icon alone, a spinner while it runs and the error's mark in red once it failed, the group's
        // outline round them saying the rest
        string icon = call.State switch
        {
            HexAgentToolState.Running => settings.UiAnimation > 0 ? "loading~spin" : "loading",
            HexAgentToolState.Failed => "error",
            _ => ToolIcon(call.ToolName ?? string.Empty)
        };
        string name = ShortToolName(call), preview = ToolPreview(call.Input);
        Vector4? toolColour = HexAgentRows.ToolColour(call.ToolName ?? string.Empty, skin?.Palette);
        var button = line.Add(new Button
        {
            Style = "button_flat", Fill = UIFill.Both, Animated = false, Align = Origin.Left, LabelScale = 0.125f, Icon = icon,
            IconColor = call.State switch { HexAgentToolState.Done => toolColour, HexAgentToolState.Failed => skin?.Palette.Error, _ => skin?.ControlTextColor },
            LabelColor = toolColour,
            Padding = new UIEdges(8, 0, 56, 0), Tooltip = $"{call.ToolName}\n{preview}"
        });
        // Opened and folded in place under its line, nothing made again, as a group is
        Panel? opened = null;
        button.OnPressed = () =>
        {
            call.Open = !call.Open;
            HoldRowInPlace(call, line);
            if (call.Open)
            {
                opened ??= OpenedCall(call, skin);
                if (opened.Parent is null)
                    holder.Add(opened);
            }
            else
                opened?.Parent?.Remove(opened);
        };
        FollowWidth(width =>
        {
            int room = Math.Max(6, (int)((width - 90.0f) / 6.0f));
            // A button's label takes no colours, so what it was given follows the name after a wider gap
            string fitted = UIRichText.Literal(name + (preview.Length > 0 && room > name.Length + 4 ? "   " + OneLine(preview, room - name.Length - 3) : string.Empty));
            if (button.Label != fitted)
                button.Label = fitted;
        });

        string seconds = call.Seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        Label time = line.Add(new Label
        {
            Text = call.State == HexAgentToolState.Running ? Said("agents_panel.tool_running", ("seconds", seconds)) : $"{seconds}s",
            TextScale = 0.125f, Dim = call.State != HexAgentToolState.Failed, Anchor = Origin.Right, Pivot = Origin.Right, Position = new Vector2(-8, 0)
        });
        if (call.State == HexAgentToolState.Running)
            agentsRunningTimes[call] = time;
        else
            agentsRunningTimes.Remove(call);

        if (call.Open)
        {
            opened = OpenedCall(call, skin);
            holder.Add(opened);
        }

        return holder;
    }

    // What a call was given and said back, under its line once it's opened
    private Panel OpenedCall(HexAgentEntry call, UISkin? skin)
    {
        var opened = new Panel { Fill = UIFill.Horizontal, Padding = new UIEdges(12, 6, 12, 8), Color = skin?.FieldColor, Radius = 6 };
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

        return opened;
    }
}
