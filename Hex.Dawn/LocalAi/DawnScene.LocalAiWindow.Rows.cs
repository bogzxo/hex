using System.Globalization;
using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The Local AI window's explained row (docs/local-ai-window-plan.md, 6): a title, the control, a short line beneath, a "?" that
// opens the long text and a "Recommended" mark. Every row of both modes is one, and the search filters them.
internal sealed partial class DawnScene
{
    private const float LOCAL_AI_TITLE = 300.0f;

    // Every row the Advanced pages can show and the page it is on, which the search goes through
    // Made when used, not by the static constructor (it read the glossary's keys before the field after it was set)
    private static LocalAiRowDef[] localAiRows => localAiRowsMade ??= MakeLocalAiRows();

    private static LocalAiRowDef[]? localAiRowsMade;

    private static LocalAiRowDef[] MakeLocalAiRows() =>
    [
        new("role_predict", LocalAiPage.Overview), new("role_chat", LocalAiPage.Overview),
        new("profile", LocalAiPage.Presets), new("context", LocalAiPage.Presets), new("temperature", LocalAiPage.Presets), new("top_p", LocalAiPage.Presets),
        new("top_k", LocalAiPage.Presets), new("min_p", LocalAiPage.Presets), new("repeat_penalty", LocalAiPage.Presets), new("thinking", LocalAiPage.Presets),
        new("offload", LocalAiPage.Presets), new("extra_args", LocalAiPage.Presets),
        new("invocation", LocalAiPage.Predictions), new("mode", LocalAiPage.Predictions), new("kind", LocalAiPage.Predictions), new("debounce", LocalAiPage.Predictions),
        new("renames", LocalAiPage.Predictions), new("far", LocalAiPage.Predictions), new("disabled_languages", LocalAiPage.Predictions),
        new("engine_choice", LocalAiPage.Engine), new("engine_build", LocalAiPage.Engine), new("engine_folder", LocalAiPage.Engine), new("engine_port", LocalAiPage.Engine),
        new("engine_autostart", LocalAiPage.Engine), new("engine_ollama_url", LocalAiPage.Engine), new("extra_args", LocalAiPage.Engine),
        new("exclude_globs", LocalAiPage.Privacy), new("link_chat_answers", LocalAiPage.Privacy), new("serve_predictions", LocalAiPage.Privacy),
        .. LocalAiGlossaryKeys.Select(key => new LocalAiRowDef(key, LocalAiPage.Glossary)),
    ];

    private static string[] LocalAiGlossaryKeys { get; } =
    [
        "glossary_model", "glossary_gguf", "glossary_quantisation", "glossary_context", "glossary_vram", "glossary_offload", "glossary_temperature",
        "glossary_top_p", "glossary_top_k", "glossary_min_p", "glossary_repeat_penalty", "glossary_thinking", "glossary_fim", "glossary_swapping"
    ];

    private (string Title, string Short, string Long) LocalAiHelpOf(string key) =>
        (Said("local_ai_help." + key + "_title"), Said("local_ai_help." + key + "_short"), Said("local_ai_help." + key + "_long"));

    // Whether a search is filtering the rows now
    private bool LocalAiSearching => LocalAiAdvanced && localAiQuery.Trim().Length > 0;

    // Whether a row passes the search (every row does when nothing is searched for)
    private bool LocalAiShows(string key)
    {
        if (!LocalAiSearching)
            return true;
        var (title, shortLine, longText) = LocalAiHelpOf(key);
        return LocalAiWindowState.Matches(localAiQuery, title, shortLine, longText);
    }

    // The search's rows, the page it jumps to
    private void LocalAiSearchChanged(string typed)
    {
        localAiQuery = typed.Trim();
        if (localAiQuery.Length > 0)
        {
            IReadOnlyList<LocalAiRowDef> found = LocalAiWindowState.Filter(localAiQuery, localAiRows, LocalAiHelpOf);
            if (LocalAiWindowState.JumpTo(found, localAiPage) is { } page && page != localAiPage)
            {
                localAiPage = page;
                localAiOpenHelp = null;
            }
        }
        RefreshLlamaSetup();
    }

    // A heading, unless the rows are being searched (then it's only the rows that matched)
    private void LocalAiHeading(StackPanel body, string text)
    {
        if (!LocalAiSearching)
            body.Add(LinkHeading(text));
    }

    // Plain text that isn't a row (a card, a note), shown unless a search is going
    private void LocalAiNote(StackPanel body, string text)
    {
        if (!LocalAiSearching)
            body.Add(LinkLine(text));
    }

    /// <summary>
    /// A row of the window: the help's title, <paramref name="control"/> beside it, the short line under and a "?" for the long
    /// text. Null (and nothing added) when a search leaves the row out. <paramref name="current"/> is the value the control has,
    /// compared with the recommended one for the chip and the reset button.
    /// </summary>
    private StackPanel? LocalAiExplainedRow(StackPanel body, string key, UIComponent control, float height = LINK_ROW, string? current = null, LocalModel? model = null, Action? reset = null)
    {
        if (!LocalAiShows(key))
            return null;
        var (title, shortLine, longText) = LocalAiHelpOf(key);
        string? recommended = LocalAiHelp.RecommendedValue(key, model, settings);
        bool isRecommended = recommended is not null && current is not null && string.Equals(recommended, current, StringComparison.OrdinalIgnoreCase);

        var row = body.Add(new StackPanel { Spacing = 2, Fill = UIFill.Horizontal, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Padding = new UIEdges(0, 0, 0, 6) });
        StackPanel line = row.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8, Fill = UIFill.Horizontal, Size = new Vector2(0, height) });
        // Three columns at fixed proportions of the row: the title, the controls (shrunk to fit) and the "?", always at the right edge
        const float HELP = 30.0f, GAP = 8.0f;
        float chipGutter = LocalAiChipGutter();
        float titleWidth = LocalAiTitleWidth();
        float controlsWidth = LocalAiControlsWidth();
        // The title's cell has a gutter after it for the "Recommended" chip, so the chip is always in the same place, right after the title
        var titleCell = line.Add(new StackPanel
        {
            Direction = UIDirection.Horizontal, Spacing = 6, Size = new Vector2(titleWidth + chipGutter, height),
            Anchor = height > LINK_ROW ? Origin.TopLeft : Origin.Left, Pivot = height > LINK_ROW ? Origin.TopLeft : Origin.Left
        });
        titleCell.Add(new Label
        {
            Text = $"[b]{UIRichText.Literal(title)}[/b]", TextScale = LINK_TEXT, Size = new Vector2(titleWidth, height), Align = height > LINK_ROW ? Origin.TopLeft : Origin.Left,
            Anchor = height > LINK_ROW ? Origin.TopLeft : Origin.Left, Pivot = height > LINK_ROW ? Origin.TopLeft : Origin.Left
        });
        if (isRecommended)
            titleCell.Add(LocalAiBadge(Said("local_ai.recommended_chip"), UIBadgeTone.Success, Said("local_ai.recommended_chip_help")));
        // The "?" sits right after the title, not at the far edge of the row
        Button more = line.Add(LinkButton("?", string.Empty, () =>
        {
            localAiOpenHelp = localAiOpenHelp == key ? null : key;
            RefreshLlamaSetup();
        }));
        more.Size = new Vector2(HELP, LINK_ROW);
        if (height > LINK_ROW)
            (more.Anchor, more.Pivot) = (Origin.TopLeft, Origin.TopLeft);
        more.Style = "button_flat";
        more.Selected = localAiOpenHelp == key;
        more.Tooltip = Said(localAiOpenHelp == key ? "local_ai.less" : "local_ai.more");
        var controls = line.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = GAP, Size = new Vector2(controlsWidth, height), Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
        bool resetShown = !isRecommended && recommended is not null && reset is not null;
        // Reset is a button with words when there is room beside the control, else an icon with a tooltip
        // Always an icon with a tooltip: the page has one "Reset these to recommended" button, so the words aren't said on every row
        bool resetIcon = resetShown;
        // The chip (or the reset icon) sits right after its control
        float sideWidth = resetShown ? HELP : 0.0f;
        float cell = sideWidth > 0 ? Math.Max(120.0f, controlsWidth - GAP - sideWidth) : controlsWidth;
        if (sideWidth > 0 && control.Size.X > 0)
            cell = Math.Min(cell, control.Size.X);
        if (control.Size.X > 0 && control.Size.X > cell)
        {
            control.Size = new Vector2(Math.Max(80.0f, cell), control.Size.Y);
            // A row of boxes and buttons gives its text boxes the room it lacks, so no button is pushed past the edge
            if (control is StackPanel { Direction: UIDirection.Horizontal } inner)
                LocalAiShrinkBoxes(inner, control.Size.X);
        }
        if (sideWidth > 0)
        {
            var holder = controls.Add(new StackPanel { Size = new Vector2(cell, height), Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
            holder.Add(control);
        }
        else
            controls.Add(control);
        if (resetShown)
        {
            Button back = controls.Add(LinkButton(resetIcon ? string.Empty : Said("local_ai.reset_recommended"), "discard", reset!));
            back.Style = "button_flat";
            back.Tooltip = Said("local_ai.reset_recommended_help", ("value", recommended!));
            if (resetIcon)
                back.Size = new Vector2(HELP, LINK_ROW);
        }
        bool open = localAiOpenHelp == key;
        row.Add(LinkLine(shortLine));
        if (open)
        {
            var box = row.Add(new Panel { Fill = UIFill.Horizontal, Radius = 6, Padding = new UIEdges(10, 8, 10, 8), Color = Skin?.Palette.Field });
            StackPanel text = box.Add(new StackPanel { Spacing = 4, Fill = UIFill.Horizontal, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
            text.Add(new Label { Text = UIRichText.Literal(longText), TextScale = LINK_SMALL, Wrap = true, Align = Origin.TopLeft, Fill = UIFill.Horizontal });
            if (recommended is not null)
                text.Add(new Label { Text = $"[b]{UIRichText.Literal(Said("local_ai.recommended_is", ("value", recommended)))}[/b]", TextScale = LINK_SMALL, Wrap = true, Align = Origin.TopLeft, Fill = UIFill.Horizontal });
        }
        return row;
    }

    // The text boxes of a horizontal row made narrower, together, until the row's children fit in <paramref name="width"/>
    private static void LocalAiShrinkBoxes(StackPanel row, float width)
    {
        var boxes = row.Children.OfType<TextBox>().ToList();
        if (boxes.Count == 0)
            return;
        float used = row.Children.Sum(child => child.Size.X) + row.Spacing * Math.Max(0, row.Children.Count - 1);
        float over = used - width;
        if (over <= 0)
            return;
        float cut = over / boxes.Count;
        foreach (TextBox box in boxes)
            box.Size = new Vector2(Math.Max(80.0f, box.Size.X - cut), box.Size.Y);
        // Still too wide: the plain notes beside the boxes (the free space) go, their words moved into the box's tooltip
        used = row.Children.Sum(child => child.Size.X) + row.Spacing * Math.Max(0, row.Children.Count - 1);
        foreach (Label note in row.Children.OfType<Label>().Where(label => label.Dim).ToList())
        {
            if (used <= width)
                break;
            used -= note.Size.X + row.Spacing;
            boxes[0].Tooltip = note.Text;
            row.Remove(note);
        }
    }

    // The row's columns: the title a share of the width, the "?" a fixed 30 and 8 between, the controls what is left
    private float LocalAiTitleWidth() => Math.Clamp(Math.Max(LocalAiRowWidth(), 400.0f) * 0.36f, 150.0f, LOCAL_AI_TITLE);

    private float LocalAiControlsWidth() => Math.Max(LocalAiRowWidth(), 400.0f) - LocalAiTitleWidth() - LocalAiChipGutter() - 30.0f - 24.0f;

    // The room after a title for its "Recommended" chip: the chip as wide as its words come to (the same guess its badges
    // are fitted by), the cell's spacing before it and a little after, so the controls never start under it
    private float LocalAiChipGutter() => Math.Max(82.0f, LocalAiTextWidth(Said("local_ai.recommended_chip"), 6.5f, 18) + 6.0f + 8.0f);

    // A row with a box for text or a number: submitted on Enter. The value shown is the current one, the placeholder what an empty box means
    private StackPanel? LocalAiTextRow(StackPanel body, string key, string current, Action<string> submitted, float width = 110, string placeholder = "", LocalModel? model = null, Action? reset = null, string? shownAs = null, string unit = "")
    {
        var box = new TextBox { Text = current, Placeholder = placeholder, Size = new Vector2(width, LINK_ROW), Anchor = Origin.Left, Pivot = Origin.Left };
        box.OnSubmitted = typed => submitted(typed.Trim());
        if (unit.Length == 0)
            return LocalAiExplainedRow(body, key, box, LINK_ROW, shownAs ?? current, model, reset);
        // A unit after the box, so a bare number says what it counts
        float unitWidth = LocalAiTextWidth(unit, 7.0f, 6);
        var withUnit = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 6, Size = new Vector2(width + 6 + unitWidth, LINK_ROW), Anchor = Origin.Left, Pivot = Origin.Left };
        withUnit.Add(box);
        withUnit.Add(new Label { Text = UIRichText.Literal(unit), TextScale = LINK_TEXT, Dim = true, Size = new Vector2(unitWidth, LINK_ROW), Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left });
        return LocalAiExplainedRow(body, key, withUnit, LINK_ROW, shownAs ?? current, model, reset);
    }

    // A row with an on / off switch
    private StackPanel? LocalAiToggleRow(StackPanel body, string key, bool state, Action<bool> changed, Action? reset = null)
    {
        var toggle = new ToggleButton(Said(state ? "local_ai.on" : "local_ai.off"), state) { Anchor = Origin.Left, Pivot = Origin.Left, LabelScale = LINK_TEXT };
        toggle.OnPressed = () => changed(toggle.State);
        return LocalAiExplainedRow(body, key, toggle, LINK_ROW, state ? "true" : "false", null, reset);
    }

    // A row with a button for each choice, the current one chosen and the recommended one marked; the buttons that don't fit go on lines below
    private StackPanel? LocalAiChoicesRow(StackPanel body, string key, IReadOnlyList<string> choices, string current, Func<string, string> textKey, Action<string> picked, LocalModel? model = null, Action? reset = null, Func<string, string>? tipKey = null, bool dropdown = false)
    {
        if (!LocalAiShows(key))
            return null;
        string? recommended = LocalAiHelp.RecommendedValue(key, model, settings);
        if (dropdown)
        {
            // Many words per choice: the Settings window's own dropdown, one line whatever the number of choices
            string[] values = [.. choices];
            string Spoken(string choice) => Said(textKey(choice));
            float widest = values.Select(choice => LocalAiTextWidth(Spoken(choice), 7.0f, 44)).DefaultIfEmpty(120).Max();
            var list = new Dropdown(values) { Size = new Vector2(Math.Clamp(widest, 160, 380), LINK_ROW), TextScale = LINK_TEXT, Anchor = Origin.Left, Pivot = Origin.Left, OptionText = Spoken };
            list.Value = current;
            list.OnChanged = chosen => picked(chosen);
            return LocalAiExplainedRow(body, key, list, LINK_ROW, current, model, reset);
        }
        // The recommended one is marked by the chip beside the row, once, not in its button's words as well
        string[] texts = [.. choices.Select(choice => Said(textKey(choice)))];
        float[] widths = [.. texts.Select(text => LocalAiTextWidth(text, 7.0f, 28))];
        // What is left of the row for the buttons: the controls' column less the chip or the reset button beside them
        float room = LocalAiControlsWidth() - (recommended is not null ? 42 : 0);
        IReadOnlyList<int> lines = LocalAiSetupFlow.RowBreaks(widths, room, 8);
        var column = new StackPanel { Direction = UIDirection.Vertical, Spacing = 4, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft };
        int at = 0;
        foreach (int count in lines)
        {
            StackPanel line = column.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8, Size = new Vector2(Math.Max(room, 120), LINK_ROW) });
            for (int i = at; i < at + count; i++)
            {
                string choice = choices[i];
                Button button = line.Add(LinkButton(texts[i], string.Empty, () => picked(choice)));
                button.Selected = choice == current;
                button.Tooltip = Said(tipKey is null ? textKey(choice) + "_help" : tipKey(choice));
            }
            at += count;
        }
        float height = lines.Count * LINK_ROW + Math.Max(0, lines.Count - 1) * 4;
        // As wide as its widest line of buttons, so the chip beside it is right after them
        float used = 0, from = 0;
        foreach (int count in lines)
        {
            float line = 0;
            for (int i = (int)from; i < from + count; i++)
                line += widths[i] + (i > from ? 8 : 0);
            used = Math.Max(used, line);
            from += count;
        }
        column.Size = new Vector2(Math.Max(used, 120), height);
        return LocalAiExplainedRow(body, key, column, height, current, model, reset);
    }

    // A path as the system writes it: backslashes on Windows
    private static string LocalAiPathText(string path) => OperatingSystem.IsWindows() ? path.Replace('/', '\\') : path;

    // Numbers as the fields show them
    private static string LocalAiNum(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
