using System.Numerics;

using Horizon.Rendering;
using Horizon.UI.Drawing;

namespace Hex.Dawn;

// "@" in the box, the workspace's files and declarations that fit what's typed after it listed over the card, from the
// harness's index as the last look left it (no walk of the disk under the keys). A press or Tab puts @ and the file's path
// in the box, a declaration's path and line, and the agent reads it from there (Claude Code takes an @path in as the
// file itself, the others read the path). Up to then it was the path typed by hand or the context button's one file.
internal sealed partial class DawnScene
{
    // The most files and declarations the list shows, and how many letters a declaration needs before it's looked for
    private const int MENTION_FILES = 6, MENTION_SYMBOLS = 4, MENTION_SYMBOL_FROM = 2;

    private StackPanel? agentsMention;
    private string agentsMentionFor = string.Empty;
    private string? agentsMentionFirst;
    private volatile bool mentionLooking;

    // The index the list looks in, the harness's, or a self-test's own over a folder it made where Hex has no workspace
    private HexCodeIndex? mentionIndex;
    private HexCodeIndex MentionIndex => mentionIndex ?? Harness;

    /// <summary>
    /// The @ and what's after it up to the caret on the caret's line, where it starts in the line, null when the caret isn't
    /// in one. An @ after a letter (an address) isn't one.
    /// </summary>
    private (int Line, int Start, string Typed)? MentionAtCaret()
    {
        if (compositor.Focus != agentsInput)
            return null;
        int line = agentsInput.Document.Caret.Line, column = agentsInput.Document.Caret.Column;
        string text = agentsInput.Document.LineAt(line);
        if (column > text.Length)
            return null;
        int at = column - 1;
        while (at >= 0 && !char.IsWhiteSpace(text[at]) && text[at] != '@')
            at--;
        if (at < 0 || text[at] != '@' || (at > 0 && !char.IsWhiteSpace(text[at - 1])))
            return null;
        return (line, at, text[(at + 1)..column]);
    }

    /// <summary>Every tick, the list over the card while the caret is in an @ and part of a name, gone otherwise.</summary>
    private void DrawAgentMention()
    {
        var mention = MentionAtCaret();
        string key = mention is { } at ? $"{at.Typed}|{agentsComposeTall}|{MentionIndex.Generation}" : string.Empty;
        if (key == agentsMentionFor)
            return;
        agentsMentionFor = key;
        if (agentsMention is not null)
        {
            agentsMention.Parent?.Remove(agentsMention);
            agentsMention = null;
        }
        agentsMentionFirst = null;
        if (mention is not { } typed || typed.Typed.Length == 0)
            return;

        HexCodeIndex looked = MentionIndex;
        HexIndexSnapshot index = looked.Latest;
        // Not looked at yet since Hex started (no agent has asked it anything), looked at off the editor's thread, the
        // list coming once it's done as the generation in the key moves
        if (!index.Files.Any())
        {
            if (!mentionLooking)
            {
                mentionLooking = true;
                Task.Run(() => looked.Look()).ContinueWith(_ => mentionLooking = false, TaskScheduler.Default);
            }
            return;
        }
        var picks = new List<(string Icon, string Label, string Taken)>();
        foreach (var (file, _) in HexCodeIndex.FindFiles(index, typed.Typed).Take(MENTION_FILES))
            picks.Add((HexExplorer.FileIcon(file.Path), file.Relative, file.Relative.Replace('\\', '/')));
        if (typed.Typed.Length >= MENTION_SYMBOL_FROM && !typed.Typed.Contains('/'))
        {
            foreach (var (file, symbol, _) in HexCodeIndex.FindSymbols(index, typed.Typed).Take(MENTION_SYMBOLS))
            {
                string where = $"{file.Relative.Replace('\\', '/')}:{symbol.Line + 1}";
                picks.Add(("symbol-class", $"{symbol.Full}  ·  {where}", where));
            }
        }
        if (picks.Count == 0)
            return;

        agentsMentionFirst = picks[0].Taken;
        Vector4 ground = Skin?.Palette.Raised ?? new Vector4(0.2f, 0.2f, 0.22f, 1.0f);
        agentsMention = agentsView.Add(new StackPanel
        {
            Anchor = Origin.Bottom, Pivot = Origin.Bottom, Fill = UIFill.Horizontal, Position = new Vector2(0, agentsComposeTall + 4),
            Padding = new UIEdges(4, 4, 4, 4), Spacing = 0, Color = ground with { W = 1.0f }, Radius = 8
        });
        agentsMention.Add(new Label { Text = UIRichText.Literal(Said("agents_panel.mention_head")), TextScale = 0.11f, Dim = true, Align = Origin.Left, Fill = UIFill.Horizontal, Size = new Vector2(0, 18) });
        foreach (var (icon, label, taken) in picks)
        {
            agentsMention.Add(new Button
            {
                Icon = icon, Label = UIRichText.Literal(OneLine(label, 110)), Style = "button_flat", Align = Origin.Left, Fill = UIFill.Horizontal,
                Size = new Vector2(0, SLASH_ROW), LabelScale = 0.13f, Animated = false, OnPressed = () => TakeMention(taken)
            });
        }
    }

    /// <summary>The @ and what was typed after it put right as the path picked, a space after it, the box keeping the keyboard.</summary>
    private void TakeMention(string taken)
    {
        if (MentionAtCaret() is not { } at)
        {
            agentsInput.Focus();
            return;
        }

        string[] lines = agentsInput.Text.Replace("\r\n", "\n").Split('\n');
        string line = lines[at.Line];
        int end = at.Start + 1 + at.Typed.Length;
        lines[at.Line] = $"{line[..at.Start]}@{taken} {line[end..]}";
        agentsInput.Text = string.Join('\n', lines);
        agentsInput.Focus();
        agentsInput.Document.MoveTo(new UITextPosition(at.Line, at.Start + taken.Length + 2));
        agentsMentionFor = "\0";
    }

    /// <summary>Tab in the box while the list is up, its first pick taken.</summary>
    private bool TakeFirstMention()
    {
        if (agentsMention is null || agentsMentionFirst is not { } first || compositor.Focus != agentsInput)
            return false;
        TakeMention(first);
        return true;
    }
}
