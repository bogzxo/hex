using System.Numerics;
using System.Text;
using System.Text.Json;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// A tool call's row in the Agents view, flat like a row of the explorer, opening to what it was given and said,
// and the short names the rows call HexAgentRows' helpers by
internal sealed partial class DawnScene
{
    // A tool's row, flat like a row of the explorer. Its icon and name, what it was called with dimmed after it, how long
    // it took and a chevron at the right. Opened, a thin rule down its left with what it was given and said under it as
    // quiet text, a dozen lines of each at the most
    private UIComponent ToolRow(HexAgentChat chat, HexAgentEntry entry, UISkin? skin)
    {
        Vector4 hover = skin?.Palette.Raised ?? new Vector4(1, 1, 1, 0.05f);
        Vector4 rule = skin?.BorderColor ?? new Vector4(1, 1, 1, 0.1f);
        string seconds = entry.Seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        string state = entry.State switch
        {
            HexAgentToolState.Running => Said("agents_panel.tool_running", ("seconds", RunningSeconds(entry.Seconds))),
            HexAgentToolState.Failed => Said("agents_panel.tool_failed", ("seconds", seconds)),
            _ => Said("agents_panel.tool_done", ("seconds", seconds))
        };

        // The room the name and its input have, the rest of the row's width is the time and the chevron
        bool compact = AgentsNarrow();
        string preview = ToolPreview(entry.Input);
        string name = ToolTitle(entry.ToolName ?? "tool");
        if (name.StartsWith("hex_", StringComparison.Ordinal)) name = name[4..];
        name = name.Replace('_', ' ');
        name = string.IsNullOrWhiteSpace(name) ? "Tool" : char.ToUpperInvariant(name[0]) + name[1..];
        // A subagent is one row, what it was sent to do and where it's got to, its own steps folded into it
        if (SubagentOf(entry) is { } subagent)
        {
            name = subagent.Name;
            preview = entry.Steps > 0
                ? Said("agents_panel.subagent_steps", ("count", entry.Steps.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("step", entry.Progress ?? subagent.About))
                : subagent.About;
        }
        // A bubble of its own, a shade over the chat and a little more when it's open, its state a line along the bottom,
        // blue while it runs, green when it went and red when it failed. A failed one filled red was most of the chat red
        var holder = new Panel { Fill = UIFill.Horizontal, Color = hover with { W = hover.W * (entry.Open ? 0.75f : 0.45f) }, Radius = 8, Padding = new UIEdges(0, 0, 0, 2) };
        var stack = holder.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 0 });
        Vector4? toolColour = HexAgentRows.ToolColour(entry.ToolName ?? string.Empty, skin?.Palette);
        // How it's going said the one way every tool row says it: a thin outline round the one running or the one that
        // failed, nothing on one that went. A bar down one side and a line along the bottom was two colour codes at once,
        // and a long turn a column of green
        holder.Outline = ToolOutline(entry.State, skin);
        Button head = stack.Add(new Button
        {
            Icon = entry.State switch { HexAgentToolState.Running => settings.UiAnimation > 0 ? "loading~spin" : "loading", HexAgentToolState.Failed => "error", _ => ToolIcon(entry.ToolName ?? string.Empty) },
            IconColor = entry.State switch { HexAgentToolState.Done => toolColour, HexAgentToolState.Failed => skin?.Palette.Error, _ => skin?.ControlTextColor },
            LabelColor = toolColour,
            Style = "button_flat",
            Align = Origin.Left,
            LabelScale = 0.15f,
            Size = new Vector2(0, 30),
            Fill = UIFill.Horizontal,
            Animated = false,
            Tooltip = $"{entry.ToolName}, {state}\n{preview}",
            Padding = new UIEdges(4, 0, compact ? 24 : 90, 0),
            OnPressed = () =>
            {
                entry.Open = !entry.Open;
                chat.Touch(entry);
            }
        });
        // The file it was given after its name, with the icon of its kind as the file tree has it, when there's room
        string? file = !HexAgentRows.IsSubagent(entry) ? FileOfCall(entry) : null;
        // A button's label is written in one colour, so the file's icon here is the label's, only the button's own icon
        // takes a colour of its own
        string fileIcon = file is null ? string.Empty : $"$({HexExplorer.FileIcon(file)})";
        FollowWidth(width =>
        {
            int room = Math.Max(4, (int)((width - (compact ? 46.0f : 132.0f)) / 6.2f));
            string fitted = UIRichText.Literal(OneLine(name, room));
            if (file is not null && room - name.Length - 3 >= 6)
                fitted += $"  {fileIcon} {UIRichText.Literal(OneLine(Path.GetFileName(file), room - name.Length - 3))}";
            if (head.Label != fitted)
                head.Label = fitted;
        });

        // How long it took and the chevron that says it opens, at the right of its head
        UIComponent metadata = compact
            ? stack.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 21), Padding = new UIEdges(28, 0, 8, 3) })
            : holder;
        var right = metadata.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 5, Anchor = compact ? Origin.TopLeft : Origin.TopRight, Pivot = compact ? Origin.TopLeft : Origin.TopRight, Position = compact ? Vector2.Zero : new Vector2(-8, -9) });
        Label time = right.Add(new Label { Text = state, TextScale = 0.125f, Dim = entry.State != HexAgentToolState.Failed });
        if (entry.State == HexAgentToolState.Running)
            agentsRunningTimes[entry] = time;
        else
            agentsRunningTimes.Remove(entry);
        var chevron = new Label { Text = entry.Open ? "$(chevron-down)" : "$(chevron-right)", TextScale = 0.13f, Dim = true };
        if (compact)
        {
            chevron.Anchor = Origin.TopRight;
            chevron.Pivot = Origin.TopRight;
            chevron.Position = new Vector2(-8, -9);
            holder.Add(chevron);
        }
        else right.Add(chevron);
        if (!entry.Open && preview.Length > 0)
        {
            Label line = Text(string.Empty, 0.135f, dim: true);
            stack.Add(new Panel { Fill = UIFill.Horizontal, Padding = new UIEdges(28, 0, 10, 7) }.With(line));
            FollowWidth(width =>
            {
                string fitted = UIRichText.Literal(OneLine(preview, Math.Max(4, (int)((width - 48) / 7.0f))));
                if (line.Text != fitted)
                    line.Text = fitted;
            });
        }

        if (entry.Open)
        {
            var opened = stack.Add(new Panel { Fill = UIFill.Horizontal, Padding = new UIEdges(12, 8, 12, 10), Color = skin?.FieldColor, Radius = 6 });
            var said = opened.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 5 });
            if (!string.IsNullOrWhiteSpace(entry.Input))
            {
                said.Add(Text(Said("agents_panel.given"), 0.135f, dim: true).Also(label => label.Bold = true));
                said.Add(Text("```json\n" + Lines(ToolDetails(entry.Input!), 12) + "\n```", 0.17f, markdown: true).Also(label => label.CodeFont = code.Font));
            }
            if (!string.IsNullOrWhiteSpace(entry.Output))
            {
                said.Add(Text(Said("agents_panel.said"), 0.135f, dim: true).Also(label => label.Bold = true));
                said.Add(Text(UIRichText.Literal(Lines(entry.Output!, 12)), 0.16f));
            }
        }

        return holder;
    }

    /// <summary>
    /// The outline of a tool's row or group, the running blue while it runs, the error's red once it failed, a faint green
    /// once it went, so every call says how it went at a glance and one that went stays quieter than the others.
    /// </summary>
    internal static Vector4 ToolOutline(HexAgentToolState state, Horizon.UI.Skinning.UISkin? skin) => state switch
    {
        HexAgentToolState.Running => RunningBlue(skin) with { W = 0.55f },
        HexAgentToolState.Failed => (skin?.Palette.Error ?? new Vector4(1.0f, 0.42f, 0.38f, 1.0f)) with { W = 0.55f },
        _ => (skin?.Palette.Success ?? new Vector4(0.36f, 0.85f, 0.45f, 1.0f)) with { W = 0.28f }
    };

    // The rows' helpers live in HexAgentRows, shared by the side panel and Noon, said here by their short names
    internal static string ToolPreview(string? input) => HexAgentRows.ToolPreview(input);
    internal static string NoteText(string text) => HexAgentRows.NoteText(text);
    internal static (string Name, string About)? SubagentOf(HexAgentEntry entry) => HexAgentRows.SubagentOf(entry);
    internal static string Lines(string text, int most) => HexAgentRows.Lines(text, most);
    internal static string OneLine(string text, int most) => HexAgentRows.OneLine(text, most);
    internal static string ToolTitle(string name) => HexAgentRows.ToolTitle(name);
    internal static string ToolIcon(string name) => HexAgentRows.ToolIcon(name);

    private static string ToolDetails(string input) => HexAgentRows.Readable(input);
}
