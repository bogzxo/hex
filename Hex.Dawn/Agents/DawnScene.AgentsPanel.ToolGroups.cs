using System.Numerics;
using System.Text;

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

    // How much of the skin's raised shade a call's or a group's bubble is. Half of it was all but lost on the glass
    // panels' darker ground, the calls a column of loose text
    private const float TOOL_SHADE = 0.9f;

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
    private static bool Groupable(HexAgentEntry entry) => entry.Kind == HexAgentEntryKind.Tool && !HexAgentRows.IsSubagent(entry) && !ShowsPictures(entry);

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
        // Its calls gone through in place, a list of them was made for every group each time the rows were looked over,
        // with every line an agent streams
        var hash = new HashCode();
        IReadOnlyList<HexAgentEntry> entries = chat.Entries;
        for (int i = start; i < entries.Count && Groupable(entries[i]); i++)
        {
            hash.Add(entries[i]);
            hash.Add(entries[i].Version);
        }

        // Not whether it's open, a press opens it in place without the row being made again
        return hash.ToHashCode();
    }

    // Reusable scratch buffers for building tool group tooltips and summaries without per-row allocations
    private readonly StringBuilder toolGroupTooltipScratch = new(256);
    private readonly StringBuilder toolKindsBuilderScratch = new(64);
    private readonly List<(string Kind, int Count)> toolKindsCountScratch = [];

    /// <summary>
    /// A group of calls. One alone is its line. More are a head saying how many and of what, or the one running, a chevron
    /// opening it to a line for each.
    /// </summary>
    private UIComponent ToolGroupRow(HexAgentChat chat, HexAgentEntry first, UISkin? skin, int start = -1)
    {
        IReadOnlyList<HexAgentEntry> entries = chat.Entries;
        if (start < 0 || start >= entries.Count || !ReferenceEquals(entries[start], first))
        {
            start = -1;
            for (int i = 0; i < entries.Count; i++)
            {
                if (ReferenceEquals(entries[i], first))
                {
                    start = i;
                    break;
                }
            }
        }

        // Single-call fast path avoids allocating a List<HexAgentEntry> for standalone calls
        if (start < 0 || start + 1 >= entries.Count || !Groupable(entries[start + 1]))
            return ToolLine(chat, first, skin, alone: true);

        List<HexAgentEntry> calls = ToolGroupAt(chat, start);
        if (calls.Count == 1)
            return ToolLine(chat, first, skin, alone: true);

        Vector4 raised = skin?.Palette.Raised ?? new Vector4(1, 1, 1, 0.05f);
        bool open = toolGroupsOpen.Contains(first);
        var holder = new StackPanel { Fill = UIFill.Horizontal, Spacing = 1 };

        HexAgentEntry? running = null;
        int failed = 0;
        toolGroupTooltipScratch.Clear();
        for (int i = 0; i < calls.Count; i++)
        {
            HexAgentEntry call = calls[i];
            if (call.State == HexAgentToolState.Running)
                running = call;
            else if (call.State == HexAgentToolState.Failed)
                failed++;
            if (i > 0)
                toolGroupTooltipScratch.Append('\n');
            toolGroupTooltipScratch.Append(ShortToolName(call)).Append(' ').Append(OneLine(ToolPreview(call.Input), 80));
        }
        string kinds = ToolKinds(calls);
        double seconds = GroupSeconds(calls);
        // One that failed on the way and was got past (its last call went) is a warning, a group that ended failing an error
        bool endedFailing = calls[^1].State == HexAgentToolState.Failed;
        string icon = running is not null ? (settings.UiAnimation > 0 ? "loading~spin" : "loading") : endedFailing ? "error" : failed > 0 ? "warning" : "tools";
        string head = running is not null
            ? Said("agents_panel.tools_running", ("count", HexAgentRows.SmallInt(calls.Count)), ("tool", ShortToolName(running)))
            : Said("agents_panel.tools_group", ("count", HexAgentRows.SmallInt(calls.Count)), ("kinds", kinds));

        var bar = holder.Add(new Panel
        {
            Fill = UIFill.Horizontal, Size = new Vector2(0, TOOL_GROUP_HEAD), Radius = 6, Color = raised with { W = raised.W * TOOL_SHADE },
            Opacity = running is null && failed == 0 ? TOOL_QUIET : 1.0f
        });
        // How it's going the way a tool's own row says it, an outline while one of its calls runs or once it ended failing,
        // none once it went. How many failed on the way is said in words at its right
        bar.Outline = ToolOutline(running is not null ? HexAgentToolState.Running : endedFailing ? HexAgentToolState.Failed : HexAgentToolState.Done, skin);
        var button = bar.Add(new Button
        {
            Style = "button_flat", Fill = UIFill.Both, Animated = false, Align = Origin.Left, LabelScale = 0.13f, Icon = icon,
            Padding = new UIEdges(6, 0, 70, 0), Tooltip = toolGroupTooltipScratch.ToString(),
        });
        Label chevron = null!;
        StackPanel? lines = null;
        // Opened and folded in place, its lines kept attached and toggled via Visible so folding/re-opening never
        // detaches or re-attaches the subtree
        void Lay(bool opening)
        {
            if (opening)
            {
                if (lines is null)
                {
                    lines = MakeLines();
                    holder.Add(lines);
                }
                lines.Visible = true;
            }
            else if (lines is not null)
                lines.Visible = false;
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
        Label time = right.Add(new Label { Text = failed > 0 ? Said("agents_panel.tools_failed", ("count", HexAgentRows.SmallInt(failed))) : HexAgentRows.FormatTenthsSeconds(seconds), TextScale = 0.125f, Dim = failed == 0 });
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

    /// <summary>
    /// How long any of a group's calls was going, calls run side by side counted once: their times added up, several
    /// running together made the group's clock run as many times faster than the clock on the wall.
    /// </summary>
    private static double GroupSeconds(List<HexAgentEntry> calls)
    {
        int count = calls.Count;
        if (count == 0)
            return 0.0;
        long now = Environment.TickCount64;
        if (count == 1)
            return Math.Max(0, (calls[0].Ended ?? now) - calls[0].Started) / 1000.0;

        // Sort intervals by start time without LINQ OrderBy allocations
        Span<long> starts = count <= 32 ? stackalloc long[count] : new long[count];
        Span<long> ends = count <= 32 ? stackalloc long[count] : new long[count];
        for (int i = 0; i < count; i++)
        {
            long s = calls[i].Started;
            long e = Math.Max(s, calls[i].Ended ?? now);
            int j = i;
            while (j > 0 && starts[j - 1] > s)
            {
                starts[j] = starts[j - 1];
                ends[j] = ends[j - 1];
                j--;
            }
            starts[j] = s;
            ends[j] = e;
        }

        long total = 0, from = starts[0], to = ends[0];
        for (int i = 1; i < count; i++)
        {
            if (starts[i] > to)
            {
                total += to > from ? to - from : 0;
                from = starts[i];
                to = ends[i];
            }
            else if (ends[i] > to)
                to = ends[i];
        }
        total += to > from ? to - from : 0;
        return total / 1000.0;
    }

    // A call's name as a line says it, Hex's without hex_ and in words
    // The four kinds of call a group has most of, the most first and those as many in the order they came, each with how
    // many when there's more than one
    private string ToolKinds(List<HexAgentEntry> calls)
    {
        toolKindsCountScratch.Clear();
        for (int c = 0; c < calls.Count; c++)
        {
            string kind = ShortToolName(calls[c]);
            int at = -1;
            for (int j = 0; j < toolKindsCountScratch.Count; j++)
            {
                if (toolKindsCountScratch[j].Kind == kind)
                {
                    at = j;
                    break;
                }
            }
            if (at < 0)
                toolKindsCountScratch.Add((kind, 1));
            else
                toolKindsCountScratch[at] = (kind, toolKindsCountScratch[at].Count + 1);
        }

        toolKindsBuilderScratch.Clear();
        for (int taken = 0; taken < 4 && toolKindsCountScratch.Count > 0; taken++)
        {
            int most = 0;
            for (int i = 1; i < toolKindsCountScratch.Count; i++)
            {
                if (toolKindsCountScratch[i].Count > toolKindsCountScratch[most].Count)
                    most = i;
            }
            var (name, count) = toolKindsCountScratch[most];
            toolKindsCountScratch.RemoveAt(most);
            if (toolKindsBuilderScratch.Length > 0)
                toolKindsBuilderScratch.Append(", ");
            toolKindsBuilderScratch.Append(name);
            if (count > 1)
                toolKindsBuilderScratch.Append(" ×").Append(count);
        }

        return toolKindsBuilderScratch.ToString();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> shortToolNames = new(StringComparer.Ordinal);

    // A call's name as a line says it, Hex's without hex_ and in words, cached per tool name
    private static string ShortToolName(HexAgentEntry call)
    {
        string raw = call.ToolName ?? "tool";
        return shortToolNames.GetOrAdd(raw, static key =>
        {
            string name = ToolTitle(key);
            if (name.StartsWith("hex_", StringComparison.Ordinal))
                name = name[4..];
            name = name.Replace('_', ' ');
            return string.IsNullOrWhiteSpace(name) ? "Tool" : char.ToUpperInvariant(name[0]) + name[1..];
        });
    }

    /// <summary>
    /// One call as a slim line, its icon, its name and what it was given dimmed after it, its time at the right, a thin mark
    /// of how it went at its left. A press opens what it was given and said back under it.
    /// </summary>
    /// <param name="alone">A call on its own rather than one of an opened group's, a bubble of its own with its state's outline the way a group's bar is. Bare on the chat it was a line of text with nothing to say it was a call, or how it went.</param>
    private UIComponent ToolLine(HexAgentChat chat, HexAgentEntry call, UISkin? skin, bool alone = false)
    {
        var holder = new StackPanel { Fill = UIFill.Horizontal, Spacing = 0 };
        // A call that went is quieter than the words round it, the agent's reply is what the chat is read for. One running
        // or failed stays as it is, it's what wants seeing
        Vector4 raised = skin?.Palette.Raised ?? new Vector4(1, 1, 1, 0.05f);
        var line = holder.Add(new Panel
        {
            Fill = UIFill.Horizontal, Size = new Vector2(0, TOOL_LINE), Color = alone ? raised with { W = raised.W * TOOL_SHADE } : Vector4.Zero,
            Radius = alone ? 6 : 0, Outline = alone ? ToolOutline(call.State, skin) : Vector4.Zero,
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
        // Opened and folded in place under its line, kept attached once created
        Panel? opened = null;
        button.OnPressed = () =>
        {
            call.Open = !call.Open;
            HoldRowInPlace(call, line);
            if (call.Open)
            {
                if (opened is null)
                {
                    opened = OpenedCall(call, skin);
                    holder.Add(opened);
                }
                opened.Visible = true;
            }
            else if (opened is not null)
                opened.Visible = false;
        };
        FollowWidth(width =>
        {
            int room = Math.Max(6, (int)((width - 90.0f) / 6.0f));
            // A button's label takes no colours, so what it was given follows the name after a wider gap
            string fitted = UIRichText.Literal(name + (preview.Length > 0 && room > name.Length + 4 ? "   " + OneLine(preview, room - name.Length - 3) : string.Empty));
            if (button.Label != fitted)
                button.Label = fitted;
        });

        Label time = line.Add(new Label
        {
            Text = call.State == HexAgentToolState.Running ? Said("agents_panel.tool_running", ("seconds", RunningSeconds(call.Seconds))) : HexAgentRows.FormatTenthsSeconds(call.Seconds),
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
