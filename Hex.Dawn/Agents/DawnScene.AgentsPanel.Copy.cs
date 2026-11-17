using System.Numerics;
using System.Text;

namespace Hex.Dawn;

// Taking text out of an agent's chat. A row right-clicked copies what it says, or the whole chat as Markdown, or opens it
// as text in a little window of its own, an editor that can't be typed into, where any part of it can be picked out and
// copied the way code is. The rows themselves are drawn text, laid out to be read rather than selected.
internal sealed partial class DawnScene
{
    /// <summary>What can be done with a row of a chat from its menu.</summary>
    private Menu AgentRowMenu(HexAgentChat chat, HexAgentEntry entry)
    {
        var menu = new Menu("agent row");
        string said = EntryText(entry);
        menu.Add(Said("agents_panel.copy_row"), () =>
        {
            UICompositor.Copy(said);
            Say(Said("agents_panel.copied"));
        }, icon: "copy").IsEnabled = () => said.Length > 0;
        menu.Add(Said("agents_panel.select_text"), () => ShowAgentText(said, chat.Shown), icon: "selection").IsEnabled = () => said.Length > 0;
        menu.AddSeparator();
        menu.Add(Said("agents_panel.copy_chat"), () =>
        {
            UICompositor.Copy(ChatAsMarkdown(chat));
            Say(Said("agents_panel.copied"));
        }, icon: "copy");
        menu.Add(Said("agents_panel.select_chat"), () => ShowAgentText(ChatAsMarkdown(chat), chat.Shown), icon: "selection");
        return menu;
    }

    /// <summary>What a row says as plain text, a tool what it was given and what it said back.</summary>
    private static string EntryText(HexAgentEntry entry) => entry.Kind switch
    {
        HexAgentEntryKind.Tool => $"{entry.ToolName}\n{entry.Input}\n\n{entry.Output}".Trim(),
        HexAgentEntryKind.User => entry.AskedText ?? entry.Text,
        _ => entry.Text
    };

    /// <summary>The whole chat as Markdown, who said what in order, the tools as a line each.</summary>
    private static string ChatAsMarkdown(HexAgentChat chat)
    {
        var said = new StringBuilder();
        foreach (HexAgentEntry entry in chat.Entries)
        {
            switch (entry.Kind)
            {
                case HexAgentEntryKind.User:
                    said.Append("**You:** ").Append(entry.AskedText ?? entry.Text).Append("\n\n");
                    break;
                case HexAgentEntryKind.Text:
                    said.Append(entry.Text).Append("\n\n");
                    break;
                case HexAgentEntryKind.Tool:
                    said.Append("- `").Append(ToolTitle(entry.ToolName ?? "tool")).Append("` ").Append(OneLine(ToolPreview(entry.Input), 120)).Append('\n');
                    break;
                case HexAgentEntryKind.Error:
                    said.Append("> ").Append(entry.Text).Append("\n\n");
                    break;
            }
        }

        return said.ToString().Trim();
    }

    /// <summary>Text in a window of its own, an editor that reads it out and can't be typed into, all of it selected to copy.</summary>
    private void ShowAgentText(string text, string title)
    {
        var window = new WindowPanel(title.Length > 0 ? OneLine(title, 60) : Said("agents_panel.select_text"))
        {
            Icon = "selection",
            Size = new Vector2(640, 420),
            MinSize = new Vector2(320, 160)
        };
        DressWindow(window);
        window.OnClosed = () => window.Parent?.Remove(window);
        var editor = window.Add(new CodeEditor
        {
            Fill = UIFill.Both,
            ReadOnly = true,
            WordWrap = true,
            LineNumbers = false,
            Minimap = false,
            StickyScroll = false,
            IndentGuides = false,
            CheckSyntax = false,
            Highlighter = CodeLanguages.ForFile("message.md"),
            Padding = new UIEdges(8, 8, 8, 8),
            CompletionSource = _ => null
        });
        editor.Text = text;
        chrome.Root.Add(window);
        editor.Focus();
        editor.Document.SelectAll();
    }
}
