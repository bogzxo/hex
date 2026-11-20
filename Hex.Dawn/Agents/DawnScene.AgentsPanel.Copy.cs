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

        // A message still held for a working agent can be taken back to the box and changed, sent again once it's right
        if (entry.Queued && IsHeld(entry))
        {
            menu.Add(Said("agents_panel.send_now"), () => SendHeldNow(entry), icon: "send");
            menu.Add(Said("agents_panel.edit_queued"), () => EditHeld(entry), icon: "edit");
            menu.AddSeparator();
        }

        // The files a tool was called on, each to open and read or to find among the others
        if (entry.Kind == HexAgentEntryKind.Tool && ToolFiles(entry) is { Count: > 0 } files)
        {
            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                menu.Add(Said("agents_panel.file_open", ("name", name)), () => { ShowFace(Hex.Shell.HexFace.Dawn); OpenAt(file, 0, 0); }, icon: "go-to-file");
                menu.Add(Said("agents_panel.file_reveal", ("name", name)), () => { ShowFace(Hex.Shell.HexFace.Dawn); RevealInExplorer(file); }, icon: "files");
                menu.Add(Said("agents_panel.file_reveal_system", ("name", name)), () => explorer.RevealInSystem(file), icon: "link-external");
            }
            menu.AddSeparator();
        }

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

    // The most files of one call its menu offers, a call on a list of them names the first few
    private const int TOOL_FILES_SHOWN = 4;

    // What the agent systems call a file a tool works on, Claude Code's file_path and notebook_path, Hex's path and paths
    private static readonly string[] FILE_KEYS = ["file_path", "path", "notebook_path", "filePath", "target_file", "paths", "files"];

    /// <summary>The files on the disk a tool call was given, by the names agents give them, the first few that are there.</summary>
    private List<string> ToolFiles(HexAgentEntry entry)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(entry.Input) || entry.Input[0] != '{')
            return found;

        System.Text.Json.Nodes.JsonObject? input;
        try
        {
            input = System.Text.Json.Nodes.JsonNode.Parse(entry.Input) as System.Text.Json.Nodes.JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return found;
        }
        if (input is null)
            return found;

        foreach (string key in FILE_KEYS)
        {
            switch (input[key])
            {
                case System.Text.Json.Nodes.JsonValue one when one.TryGetValue(out string? path):
                    Keep(path);
                    break;
                case System.Text.Json.Nodes.JsonArray many:
                    foreach (var each in many)
                    {
                        if (each is System.Text.Json.Nodes.JsonValue value && value.TryGetValue(out string? path))
                            Keep(path);
                    }
                    break;
            }
        }

        return found;

        void Keep(string path)
        {
            if (found.Count < TOOL_FILES_SHOWN && path.Length > 0 && AgentFilePath(path) is { } full && !found.Contains(full, PlatformPaths.Comparer))
                found.Add(full);
        }
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
