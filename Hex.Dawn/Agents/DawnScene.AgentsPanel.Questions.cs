using System.Numerics;

using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// An agent asking the person something (hex_ask), a card in its chat with the question, a button for each answer it
// offers, the one it would pick marked, and a box for an answer of the person's own. The time left counts down on it, the
// agent going on with its own pick once it's out. It waits on the person, so it stands out in the accent colour and
// counts as an agent that needs looking at. Answered, it says what was said and keeps its place in the chat.
internal sealed partial class DawnScene
{
    // The time left on every question waiting on screen, counted down without its row being made again
    private readonly Dictionary<HexAgentEntry, Label> agentsQuestionTimes = [];

    private UIComponent QuestionRow(HexAgentChat chat, HexAgentEntry entry, UISkin? skin)
    {
        Vector4 accent = skin?.Palette.Accent ?? new Vector4(0.35f, 0.6f, 1.0f, 1.0f);
        bool waiting = entry.Respond is not null;
        var card = new Panel
        {
            Fill = UIFill.Horizontal, Radius = 8, Padding = new UIEdges(12, 8, 12, 8),
            Color = accent with { W = waiting ? 0.12f : 0.05f }
        };
        var stack = card.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 5 });
        stack.Add(Text($"[color:{HexColour(accent)}]$(question)[/color] {UIRichText.Literal(entry.Text)}", 0.14f));
        string[] choices = entry.Choices ?? [];

        if (!waiting)
        {
            stack.Add(Text(entry.State == HexAgentToolState.Done
                ? $"$(check) {Said("agents_panel.question_answered", ("answer", UIRichText.Literal(entry.Output ?? string.Empty)))}"
                : $"$(clock) {Said("agents_panel.question_unanswered")}", 0.125f, dim: true));
            return card;
        }

        // A button a line, the answers being a few words that don't fit side by side in a narrow panel
        for (int at = 0; at < choices.Length; at++)
        {
            string choice = choices[at];
            bool recommended = entry.Recommended == at;
            stack.Add(new Button
            {
                Label = recommended ? $"{choice}  ({Said("agents_panel.question_recommended")})" : choice,
                Primary = recommended, Fill = UIFill.Horizontal, Size = new Vector2(0, 26), LabelScale = 0.125f, Animated = false,
                OnPressed = () => chat.Reply(entry, choice)
            });
        }

        var own = stack.Add(new StackPanel { Direction = UIDirection.Horizontal, Fill = UIFill.Horizontal, Spacing = 6, Size = new Vector2(0, 26) });
        var box = own.Add(new TextBox { Fill = UIFill.Horizontal, Size = new Vector2(0, 24), Placeholder = Said("agents_panel.question_own"), MaxLength = 2000 });
        void Own()
        {
            if (!string.IsNullOrWhiteSpace(box.Text))
                chat.Reply(entry, box.Text.Trim());
        }
        box.OnSubmitted = _ => Own();
        own.Add(new Button { Label = Said("agents_panel.question_send"), Icon = "send", Size = new Vector2(90, 24), LabelScale = 0.125f, Animated = false, OnPressed = Own });

        Label left = stack.Add(Text(QuestionLeft(entry), 0.12f, dim: true));
        agentsQuestionTimes[entry] = left;
        return card;
    }

    // How long a question has left, and what the agent goes on with when it runs out
    private string QuestionLeft(HexAgentEntry entry)
    {
        long left = Math.Max(0, (entry.Deadline ?? 0) - System.Environment.TickCount64) / 1000;
        string time = left >= 60 ? $"{left / 60}:{left % 60:00}" : $"{left}s";
        return entry.Recommended is { } pick && entry.Choices is { } choices && pick < choices.Length
            ? Said("agents_panel.question_left", ("time", time), ("choice", UIRichText.Literal(choices[pick])))
            : Said("agents_panel.question_left_own", ("time", time));
    }

    /// <summary>The time left on the questions waiting, a few times a second.</summary>
    private void TickQuestionTimes()
    {
        foreach (var (entry, label) in agentsQuestionTimes)
        {
            if (entry.Respond is null)
                continue;
            string said = QuestionLeft(entry);
            if (label.Text != said)
                label.Text = said;
        }
    }
}
