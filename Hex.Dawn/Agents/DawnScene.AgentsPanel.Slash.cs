using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI.Drawing;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private const int SLASH_MOST = 8;
    private const float SLASH_ROW = 24.0f, SLASH_DESCRIPTION_ROW = 42.0f;

    private ScrollPanel? agentsSlash;
    private string agentsSlashFor = string.Empty, agentsSlashDismissed = string.Empty;
    private readonly List<(HexAgentCommand Command, Button Button)> agentsSlashRows = [];
    private int agentsSlashSelected;

    /// <summary>Native commands and the actions Hex can perform for this provider.</summary>
    private List<HexAgentCommand> SlashCommandsOf(string? agent)
    {
        if (agent is null || !extensions.AgentRuns.TryGetValue(agent, out var run))
            return [];
        var listed = HexAgentCommand.Read(run.Run.Properties.GetValueOrDefault("commands"));
        void Add(HexAgentCommand command)
        {
            // Host actions take precedence over terminal-only commands with the same name.
            listed.RemoveAll(each => string.Equals(each.Name, command.Name, StringComparison.OrdinalIgnoreCase));
            listed.Add(command);
        }
        Add(new("help", "List the commands available in this chat.", Action: "help"));
        Add(new("new", "Start a new chat; keep this conversation in history.", Action: "new"));
        Add(new("clear", "Start a fresh chat without deleting this conversation.", Action: "new"));
        Add(new("resume", "Open chat history.", Action: "resume"));
        Add(new("stop", "Stop the current turn.", Action: "stop"));
        Add(new("status", "Show this chat's provider, model and context usage.", Action: "status"));
        foreach (AgentOption option in OptionsOf(agent))
        {
            string name = option.Key is "permission_mode" or "mode" or "sandbox" ? "permissions" : option.Key;
            Add(new(name, $"Choose {option.Title.ToLowerInvariant()} for subsequent turns.", "[value]", "option:" + option.Key));
        }
        if (run.Run.Properties.GetValueOrDefault("compact") is StringValue { Value: ['/', .. string named] }
            && !listed.Any(each => each.Name == named))
            listed.Add(new(named, Said("noon.command_compact")));
        return listed;
    }

    /// <summary>A bounded viewport over every match; late discovery and resize both invalidate it.</summary>
    private void DrawAgentSlash()
    {
        // Clicking a non-text control clears compositor focus. Keep the composer focused while its popup
        // owns the pointer, otherwise the scrollbar and rows disappear before the release reaches them.
        if (agentsSlash is { } popup && compositor.Focus is null)
        {
            bool inside = popup.IsDraggingBar;
            for (UIComponent? over = compositor.HoveredComponent; over is not null; over = over.Parent)
                inside |= ReferenceEquals(over, popup);
            if (inside) agentsInput.Focus();
        }
        // The /word the caret is in, anywhere in what's written: it only came up while the box held nothing else
        SlashTyped? slashed = compositor.Focus == agentsInput ? SlashAtCaret() : null;
        if (slashed is null && agentsSlashDismissed.Length == 0 && agentsSlashFor.Length == 0)
            return;
        string text = agentsInput.Text;
        bool typing = slashed is not null;
        if (text != agentsSlashDismissed)
            agentsSlashDismissed = string.Empty;
        typing &= text != agentsSlashDismissed;
        if (!typing)
        {
            if (agentsSlashFor.Length == 0)
                return;
            agentsSlashFor = string.Empty;
            agentsSlash?.Parent?.Remove(agentsSlash);
            agentsSlash = null;
            agentsSlashRows.Clear();
            agentsSlashSelected = 0;
            return;
        }
        var matches = HexAgentCommand.Matching(SlashCommandsOf(AgentFor(agentChat)), slashed!.Value.Typed);
        float room = MathF.Max(0, agentsView.Bounds.Height - agentsComposeTall - 12);
        string key = $"{AgentFor(agentChat)}|{text}|{slashed}|{agentsComposeTall}|{agentsView.Bounds.Width}|{room}|{string.Join("\n", matches)}";
        if (key == agentsSlashFor)
            return;
        agentsSlashFor = key;
        RebuildAgentSlash(matches, room);
    }

    private void RebuildAgentSlash(List<HexAgentCommand> matches, float room)
    {
        agentsSlash?.Parent?.Remove(agentsSlash);
        agentsSlash = null;
        agentsSlashRows.Clear();
        agentsSlashSelected = 0;
        if (matches.Count == 0 || room < SLASH_ROW)
            return;

        float height = matches.Sum(command => command.About.Length > 0 ? SLASH_DESCRIPTION_ROW : SLASH_ROW) + 8;
        Vector4 ground = Skin?.Palette.Raised ?? new Vector4(0.2f, 0.2f, 0.22f, 1.0f);
        agentsSlash = agentsView.Add(new ScrollPanel
        {
            Anchor = Origin.Bottom, Pivot = Origin.Bottom, Fill = UIFill.Horizontal,
            Position = new Vector2(0, agentsComposeTall + 4),
            Size = new Vector2(0, MathF.Min(room, MathF.Min(height, SLASH_MOST * SLASH_DESCRIPTION_ROW + 8))),
            Padding = new UIEdges(4), Color = ground with { W = 1.0f }, Radius = 8, Fade = 0, WheelStep = SLASH_DESCRIPTION_ROW
        });
        var rows = agentsSlash.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 0 });
        foreach (HexAgentCommand command in matches)
        {
            bool described = command.About.Length > 0;
            var row = rows.Add(new Button
            {
                Label = string.Empty, Style = "button_flat", Fill = UIFill.Horizontal,
                Size = new Vector2(0, described ? SLASH_DESCRIPTION_ROW : SLASH_ROW),
                Animated = false, Selected = agentsSlashRows.Count == 0,
                Tooltip = $"/{command.Name} {command.Hint}\n{command.About}".Trim(),
                OnPressed = () => TakeSlash(command.Name)
            });
            row.Add(new Label
            {
                Text = UIRichText.Literal($"/{command.Name}" + (command.Hint.Length > 0 ? $" {command.Hint}" : "")),
                Align = Origin.Left, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Position = new Vector2(6, -2),
                Fill = UIFill.Horizontal, Size = new Vector2(-12, 20), TextScale = 0.13f
            });
            if (described)
                row.Add(new Label
                {
                    Text = UIRichText.Literal(OneLine(command.About, 160)), Dim = true,
                    Align = Origin.Left, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Position = new Vector2(6, -22),
                    Fill = UIFill.Horizontal, Size = new Vector2(-12, 18), TextScale = 0.115f
                });
            agentsSlashRows.Add((command, row));
        }
    }

    // The /word being typed, its line, where its slash is and what's after the slash up to the caret
    private readonly record struct SlashTyped(int Line, int Slash, int End, string Typed);

    /// <summary>The /word the caret is at the end of, at the start of the box or after a space, null for none.</summary>
    private SlashTyped? SlashAtCaret()
    {
        UITextPosition caret = agentsInput.Document.Caret;
        if (caret.Column <= 0)
            return null;
        string line = agentsInput.Document.LineAt(caret.Line);
        int column = Math.Min(caret.Column, line.Length), start = column;
        while (start > 0 && !char.IsWhiteSpace(line[start - 1]) && line[start - 1] != '/')
            start--;
        if (start == 0 || line[start - 1] != '/')
            return null;
        int slash = start - 1;
        if (slash > 0 && !char.IsWhiteSpace(line[slash - 1]))
            return null;
        // A word goes on past the caret, the whole of it is what's replaced
        int end = column;
        while (end < line.Length && !char.IsWhiteSpace(line[end]))
            end++;
        return new SlashTyped(caret.Line, slash, end, line[(slash + 1)..column]);
    }

    private void TakeSlash(string name)
    {
        agentsInput.Focus();
        if (SlashAtCaret() is { } typed)
        {
            // Only the /word up to the caret, what's written round it stays, a space put between it and anything after.
            // "/cave" typed in front of "when" came out "/cavemanwhen"
            var document = agentsInput.Document;
            string line = document.LineAt(typed.Line);
            int caret = typed.Slash + 1 + typed.Typed.Length;
            bool spaced = caret < line.Length && line[caret] == ' ';
            document.Select(new UITextPosition(typed.Line, typed.Slash), new UITextPosition(typed.Line, caret));
            document.Insert($"/{name}" + (spaced ? "" : " "));
            document.MoveTo(new UITextPosition(typed.Line, typed.Slash + name.Length + 2));
        }
        else
        {
            agentsInput.Text = $"/{name} ";
            agentsInput.Document.MoveTo(new UITextPosition(0, agentsInput.Document.LineAt(0).Length));
        }
        agentsSlashFor = "\0";
    }

    private bool MoveSlash(int by)
    {
        if (agentsSlash is null || agentsSlashRows.Count == 0 || compositor.Focus != agentsInput)
            return false;
        agentsSlashRows[agentsSlashSelected].Button.Selected = false;
        agentsSlashSelected = (agentsSlashSelected + by + agentsSlashRows.Count) % agentsSlashRows.Count;
        Button selected = agentsSlashRows[agentsSlashSelected].Button;
        selected.Selected = true;

        // Into sight by the rows' own heights, not by where they were last laid out, which a held key's repeats outran
        float top = 0.0f;
        for (int i = 0; i < agentsSlashSelected; i++)
            top += agentsSlashRows[i].Button.Size.Y;
        float bottom = top + selected.Size.Y, sight = agentsSlash.Bounds.Shrink(agentsSlash.Padding).Height;
        float at = agentsSlash.OffsetGoal;
        if (top < at)
            agentsSlash.ScrollTo(top);
        else if (bottom > at + sight)
            agentsSlash.ScrollTo(bottom - sight);
        return true;
    }

    private bool DismissSlash()
    {
        if (agentsSlash is null || compositor.Focus != agentsInput)
            return false;
        agentsSlashDismissed = agentsInput.Text;
        agentsSlashFor = "\0";
        DrawAgentSlash();
        return true;
    }

    /// <summary>
    /// Tab and Enter complete the selected command; sending follows after its arguments. Enter on a command written out
    /// in full sends it rather than completing it to itself, it took a second Enter.
    /// </summary>
    private bool TakeFirstSlash(bool sending = false)
    {
        if (agentsSlash is null || agentsSlashRows.Count == 0 || compositor.Focus != agentsInput)
            return false;
        string name = agentsSlashRows[agentsSlashSelected].Command.Name;
        // Sent as it is only when the word is the command and ends at the caret, not run into more text after it
        if (sending && SlashAtCaret() is { } typed && string.Equals(typed.Typed, name, StringComparison.OrdinalIgnoreCase)
            && typed.End == typed.Slash + 1 + typed.Typed.Length)
            return false;
        TakeSlash(name);
        return true;
    }
}
