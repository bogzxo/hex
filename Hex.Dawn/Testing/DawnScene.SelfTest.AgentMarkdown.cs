using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestAgentMarkdown(HexPointerScript test)
    {
        float width = 0, motion = 0;
        string file = Path.Combine(testFolder, "agent-markdown-target.txt");
        HexAgentEntry? reply = null;
        const string markdown = """
            ## Results

            A **bold** answer with *emphasis* and `inline_code`.

            - First item
            - Second item

            | Feature | Result | Count |
            | :--- | :---: | ---: |
            | **Markdown** | A longer result that must wrap without hiding any of the text | 123 |
            | `a|b` | [Details](https://example.com) | 7 |

            ```cs
            string text = "**literal** | not a table";
            ```
            """;
        test.Run(() =>
        {
            width = settings.LeftWidth;
            motion = settings.UiAnimation;
            settings.UiAnimation = 0;
            settings.LeftWidth = 560;
            ApplyPanelSizes();
            File.WriteAllText(file, "A link target.\n");
            Open(file);
            ShowAgentsPanel();
            agentChat = new HexAgentChat(++agentChatsMade, "markdown-fixture");
            agentChats.Add(agentChat);
            agentChat.Ask("Show formatted Markdown and a table.");
            reply = agentChat.Say(markdown);
            agentChat.Done();
        });
        test.Settle(0.5f);
        test.Check("agents markdown: a reply uses document formatting and a code font", () =>
            Descendants(agentRowsShown[^1].Row).OfType<Label>().All(label => label.Document && label.CodeFont is not null));
        test.Check("agents markdown: pipe tables have formatted cells and no notebook toolbar", () =>
            Descendants(agentRowsShown[^1].Row).OfType<NotebookMarkdownTable>().SingleOrDefault() is { Cells.Count: 3 } grid
            && grid.Cells.All(row => row.Length == 3)
            && Descendants(grid.Cells[2][0]).OfType<Label>().Any(label => label.Text == "`a|b`")
            && Descendants(grid.Cells[1][1]).OfType<Label>().All(label => label.Align == Horizon.Rendering.Origin.Top)
            && Descendants(grid.Cells[1][2]).OfType<Label>().All(label => label.Align == Horizon.Rendering.Origin.TopRight)
            && Descendants(agentRowsShown[^1].Row).OfType<NotebookTablePanel>().All(table => !table.Strip.Visible));
        test.Check("agents markdown: existing Markdown links are preserved", () =>
            WithPlaces($"[the target]({file}:1)") == $"[the target]({file}:1)");
        test.Check("agents markdown: file references remain clickable without changing code expressions", () =>
            WithPlaces($"See `{file}:1`.") == $"See [{file}:1]({file}:1)."
            && WithPlaces($"`print('{file}:1')`") == $"`print('{file}:1')`");
        test.Check("agents markdown: code fences keep literal paths, pipes and nested shorter fences", () =>
            new[] { $"```cs\n{file}:1\n```", $"~~~\n{file}:1\n~~~", $"````md\n```\n{file}:1\n```\n````" }
                .All(source => WithPlaces(source) == source));
        SemanticShot(test, "agents-markdown-wide.png");
        test.Run(() => { settings.LeftWidth = MIN_SIDE; ApplyPanelSizes(); });
        test.Settle(0.5f);
        test.Check("agents markdown: table cells wrap inside a narrow sidebar", () =>
        {
            foreach (var label in Descendants(agentRowsShown[^1].Row).OfType<NotebookMarkdownTable>().SelectMany(grid => Descendants(grid).OfType<Label>()))
                if (label.RichText.Width > label.Bounds.Width + 0.5f)
                    Console.WriteLine($"[Hex self-test] overwide cell '{label.Text}': {label.RichText.Width} in {label.Bounds.Width}");
            return Descendants(agentRowsShown[^1].Row).OfType<NotebookMarkdownTable>().SingleOrDefault() is { } grid
            && grid.Bounds.Min.X >= agentsView.Bounds.Min.X && grid.Bounds.Max.X <= agentsView.Bounds.Max.X
            && Descendants(grid).OfType<Label>().All(label => label.RichText.Width <= label.Bounds.Width + 0.5f)
            && Descendants(grid.Cells[1][1]).OfType<Label>().Any(label => label.LineCount > 1);
        });
        test.Run(() => { reply!.Text += "\n\nA streamed continuation."; agentChat!.Touch(reply); });
        test.Settle(0.3f);
        test.Check("agents markdown: streamed updates retain tables and add the new text", () =>
            Descendants(agentRowsShown[^1].Row).OfType<NotebookMarkdownTable>().Count() == 1
            && Descendants(agentRowsShown[^1].Row).OfType<Label>().Any(label => label.Text.Contains("A streamed continuation.")));
        SemanticShot(test, "agents-markdown-narrow.png");
        test.Run(() =>
        {
            ClearAgentChat();
            settings.LeftWidth = width;
            settings.UiAnimation = motion;
            ApplyPanelSizes();
            ShowSide(HexSide.Explorer);
            CloseNow();
        });
    }
}
