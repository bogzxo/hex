using System.Numerics;
using System.Text;
using System.Text.Json;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// The rows of the Agents view. What you asked on a raised bubble at the right, what the agent said as
// Markdown at the left, what it thought dimmed, every tool it called as a row with an icon by its kind and how long it
// took, opening to say what it was given and what it said back. They're added as they come and only a row that changed
// is made again, so what's there stays put and what's new slides in (DawnScene.AgentsPanel.Motion).
internal sealed partial class DawnScene
{
    // The widest a bubble of what was asked is, of the width there is
    private const float AGENTS_BUBBLE = 0.86f;

    // The rows on screen, each with the entry and the version of it it was made from
    private readonly List<(HexAgentEntry Entry, int Version, UIComponent Row)> agentRowsShown = [];
    private (HexAgentChat? Chat, int Dropped, bool Narrow, object? Skin, float Motion) agentRowsFor;
    private Panel? agentsTyping;

    // Where each chat was scrolled to as another was put on screen, null for at its end (where it follows what comes),
    // and where the one put on screen goes back to once its rows are laid out
    private readonly Dictionary<int, float?> agentScrolls = [];
    private (HexAgentChat Chat, float? Offset)? agentScrollWanted;

    // The time of every tool that's still running, counted up as it goes without its row being made again
    private readonly Dictionary<HexAgentEntry, Label> agentsRunningTimes = [];

    private UISkin? Skin => compositor.Skin;

    private static string HexColour(Vector4 colour) =>
        $"#{(int)(Math.Clamp(colour.X, 0, 1) * 255):x2}{(int)(Math.Clamp(colour.Y, 0, 1) * 255):x2}{(int)(Math.Clamp(colour.Z, 0, 1) * 255):x2}";

    private static void Empty(UIComponent of)
    {
        foreach (UIComponent child in of.Children.ToArray())
            of.Remove(child);
    }

    // How wide the rows have, from the side bar's width and never the scroll's own, a scroll bar coming and going
    // changed that and the chat flickered between the two
    private int AgentsWidth() => Math.Max(32, (int)agentsView.Size.X - 16);

    // Whether the rows are laid out narrow, the one thing about the width that makes them all again. Everything else of
    // the width is the fits' (see FitAgentRows), so a drag of the side bar moves along with the mouse without a row made
    // again. A long chat used to be rebuilt dozens of times a second and stuttered, then waited for the drag to stop and
    // jumped
    private bool AgentsNarrow() => AgentsWidth() < 210;

    // What each row does with a new width, made with the row, and the width they were last fitted to
    private readonly Dictionary<UIComponent, List<Action<float>>> agentRowFits = [];
    private List<Action<float>>? agentFitsMaking;
    private int agentRowsFitted = -1;

    /// <summary>Something of the row being made that follows the width, run now and again whenever the width moves.</summary>
    private void FollowWidth(Action<float> fit)
    {
        fit(AgentsWidth());
        agentFitsMaking?.Add(fit);
    }

    // A row made with what of it follows the width kept by it
    private UIComponent FittedRowOf(HexAgentChat chat, HexAgentEntry entry)
    {
        var fits = new List<Action<float>>();
        agentFitsMaking = fits;
        try
        {
            UIComponent row = RowOf(chat, entry);
            // The last row of a turn that's over has the turn's line under it, how long it took, its calls and its files
            if (TurnFooter(chat, entry) is { } footer)
            {
                var both = new StackPanel { Fill = UIFill.Horizontal, Spacing = 6 };
                both.Add(row);
                both.Add(footer);
                row = both;
            }
            if (fits.Count > 0)
                agentRowFits[row] = fits;
            // Right-clicked, what it says can be copied, or opened as text to pick any part of it out
            row.OnContextMenu ??= at => ContextMenu.Show(chrome, at, AgentRowMenu(chat, entry));
            return row;
        }
        finally
        {
            agentFitsMaking = null;
        }
    }

    // The rows fitted to the width when it moved, a few sizes and lines set and nothing made again
    private void FitAgentRows()
    {
        int width = AgentsWidth();
        if (width == agentRowsFitted)
            return;
        agentRowsFitted = width;
        foreach (List<Action<float>> fits in agentRowFits.Values)
        {
            foreach (Action<float> fit in fits)
                fit(width);
        }
    }

    private static int IndexIn(UIComponent parent, UIComponent child)
    {
        for (int i = 0; i < parent.Children.Count; i++)
        {
            if (parent.Children[i] == child)
                return i;
        }

        return parent.Children.Count;
    }

    private static Label Text(string text, float scale = 0.16f, bool dim = false, bool markdown = false) =>
        new() { Text = text, TextScale = scale, Dim = dim, Wrap = true, Markdown = markdown, Fill = UIFill.Horizontal, Align = Origin.TopLeft };

    /// <summary>
    /// The chat's rows brought up to date. Another chat, a new width or a new theme makes them all again, otherwise a
    /// row is only made again when its entry changed and new ones are added at the end, sliding in.
    /// </summary>
    private void DrawAgentRows()
    {
        HexAgentChat? chat = agentChat;
        var key = (chat, (chat?.Dropped ?? 0) + (chat?.Reshaped ?? 0) * 100_000, chat is { Entries.Count: > 0 } && AgentsNarrow(), (object?)Skin, settings.UiAnimation);
        bool fresh = key != agentRowsFor;

        // Only another chat comes in moving, rows made again for a new width or theme are just there, and so is all of
        // it the first time it's drawn after the view was opened again
        bool still = agentsStill;
        agentsStill = false;
        bool arriving = fresh && chat != agentRowsFor.Chat && !still;
        if (fresh)
        {
            // The dots stay through rows made again for a new width, made again they started their bounce over and
            // slid in on every step of a drag
            Panel? typing = chat == agentRowsFor.Chat && ReferenceEquals(Skin, agentRowsFor.Skin) ? agentsTyping : null;

            // Another chat keeps where the one leaving was read to and goes back to where it was itself, the end for
            // one not read before, so going between chats never loses the place in either
            if (chat != agentRowsFor.Chat)
            {
                if (agentRowsFor.Chat is { } leaving)
                    agentScrolls[leaving.Id] = agentsScroll.Offset >= agentsScroll.MaxOffset - 0.5f ? null : agentsScroll.Offset;
                agentScrollWanted = chat is null ? null : (chat, agentScrolls.GetValueOrDefault(chat.Id));
                agentsScroll.ScrollToEnd();
            }

            agentRowsFor = key;
            agentRowsShown.Clear();
            agentsRunningTimes.Clear();
            agentsGroupTimes.Clear();
            agentsQuestionTimes.Clear();
            agentRowFits.Clear();
            agentsRows.Clear(keep: typing);

            agentsTyping = typing;
            if (chat is not { Entries.Count: > 0 })
            {
                if (typing is not null)
                    agentsRows.Remove(typing);
                agentsTyping = null;
                DrawAgentWelcome();
                return;
            }

            // The rows it no longer keeps, a press or a scroll to the top away, over the ones it does
            DrawAgentEarlier(chat);
        }

        if (chat is null)
            return;

        for (int i = 0; i < chat.Entries.Count; i++)
        {
            HexAgentEntry entry = chat.Entries[i];

            // A call after the first of a group is drawn by the group's first row, its own place in the list kept by a row
            // of nothing so the rows still go one to an entry. The first row goes by what the whole group is
            bool member = InToolGroup(chat, i);
            int version = member ? entry.Version : Groupable(entry) ? ToolGroupKey(chat, i) : entry.Version;
            // A row made again once its turn is over, for the turn's line under it, or once it no longer ends it
            if (!member)
                version = version * 2 + (EndsTurn(chat, i) ? 1 : 0);
            if (i < agentRowsShown.Count)
            {
                if (agentRowsShown[i].Version == version)
                    continue;

                // A call that's become part of a group, or one that's left one, is made again as what it is now
                bool wasMember = agentRowsShown[i].Row is Panel { Visible: false, Children.Count: 0 };
                if (member && wasMember)
                {
                    agentRowsShown[i] = (entry, version, agentRowsShown[i].Row);
                    continue;
                }

                // One not made yet is made as its entry is whenever it comes into sight, nothing to make again
                UIComponent was = agentRowsShown[i].Row;
                if (!member && agentsRows.IsWaiting(was))
                {
                    agentRowsShown[i] = (entry, version, was);
                    continue;
                }

                // Made again in its place, a tool that finished giving a little pop
                UIComponent made = member ? GroupedPlace() : FittedRowOf(chat, entry);
                agentsRows.Replace(was, made);
                agentRowFits.Remove(was);
                agentRowsShown[i] = (entry, version, made);
                // A tool of its own that finished gives a little pop, a group changing as its calls come and go doesn't
                if (entry.Kind == HexAgentEntryKind.Tool && !Groupable(entry) && entry.State != HexAgentToolState.Running)
                    PopRow(made);
                continue;
            }

            int at = agentsTyping is null ? agentsRows.Children.Count : agentsRows.IndexOf(agentsTyping);

            // A whole chat put on screen has its rows made only as they come into sight, the dozen or two at its end
            // first, where every row it keeps was made at once
            if (fresh && !member)
            {
                UIComponent place = new Panel { Size = Vector2.Zero, Color = Vector4.Zero };
                agentsRows.InsertLater(at, place, entry, () => MadeLater(chat, entry, place));
                agentRowsShown.Add((entry, version, place));
                continue;
            }

            UIComponent row = member ? GroupedPlace() : FittedRowOf(chat, entry);
            agentsRows.Insert(at, row, entry);
            agentRowsShown.Add((entry, version, row));

            // A row that just came slides in on its own, what was asked from the right where its bubble is
            if (member)
                continue;
            if (!fresh && !still)
                SlideRowIn(row, 0.0f, entry.Kind == HexAgentEntryKind.User ? 16.0f : 0.0f);
        }

        // Rows made in the moment after another chat came on screen come in quickly a few at a time, as a whole chat did
        if (arriving)
        {
            agentsArrivingUntil = System.Environment.TickCount64 + ARRIVING_MS;
            agentsArrived = 0;
        }

        ShowTyping(chat, still);
        FitAgentRows();
    }

    // How long after a chat comes on screen the rows made for it slide in, and how many have so far
    private const long ARRIVING_MS = 300;
    private long agentsArrivingUntil;
    private int agentsArrived;

    /// <summary>A row that waited for its place to come into sight made, and put in the place's stead among the rows shown.</summary>
    private UIComponent MadeLater(HexAgentChat chat, HexAgentEntry entry, UIComponent place)
    {
        UIComponent made = FittedRowOf(chat, entry);
        int at = agentRowsShown.FindIndex(shown => ReferenceEquals(shown.Row, place));
        if (at >= 0)
            agentRowsShown[at] = agentRowsShown[at] with { Row = made };
        if (System.Environment.TickCount64 < agentsArrivingUntil)
            SlideRowIn(made, Math.Min(agentsArrived++, 8) * 0.025f);
        return made;
    }

    // Whether the next rows drawn are just put there, the view having been opened again, see PlaceAgentsView
    private bool agentsStill;

    // When the dots were last wanted and weren't up, they come only once the agent's been quiet a moment
    private long typingWantedSince = -1;

    // How long the agent has to be quiet before the dots come, in milliseconds. Between one tool and the next they came
    // and went in a blink, which looked like a glitch
    private const long TYPING_AFTER_MS = 350;

    // The bouncing dots at the end while the agent's working and nothing of it is going on a row already
    private void ShowTyping(HexAgentChat chat, bool still = false)
    {
        bool quiet = chat.Running && chat.Entries[^1] is not { Kind: HexAgentEntryKind.Tool, State: HexAgentToolState.Running };
        long now = System.Environment.TickCount64;
        if (!quiet)
            typingWantedSince = -1;
        else if (typingWantedSince < 0)
            typingWantedSince = now;
        bool shown = quiet && now - typingWantedSince >= TYPING_AFTER_MS;
        // The row keeps its room the whole time the agent runs and only the dots come and go in it: a row put in and
        // taken out between one tool and the next moved everything above it by its height
        if (chat.Running && agentsTyping is null)
        {
            agentsTyping = agentsRows.Add(MakeTypingDots());
            BounceDots(agentsTyping);
            if (!still)
                SlideRowIn(agentsTyping);
        }
        else if (!chat.Running && agentsTyping is not null)
        {
            agentsRows.Remove(agentsTyping);
            agentsTyping = null;
        }
        else if (agentsTyping is not null && agentsRows.Children[^1] != agentsTyping)
        {
            agentsRows.MoveChild(agentsTyping, agentsRows.Children.Count);
        }
        if (agentsTyping is not null)
            agentsTyping.Children[0].Visible = shown;
    }

    // An empty chat, what it's for and a few things to ask to start from, coming in one after another
    private void DrawAgentWelcome()
    {
        if (!CanComposeAgent())
        {
            agentsRows.Add(Text(Said("agents_panel.setup_title"), 0.17f));
            string? selected = AgentFor(agentChat);
            string explanation = selected is not null && extensions.AgentRuns.TryGetValue(selected, out var run)
                ? Said("agents_panel.setup_program", ("program", HexExtensions.Text(run.Run, "program") ?? selected))
                : Said("agents_panel.no_agents");
            agentsRows.Add(Text(UIRichText.Literal(explanation), 0.125f, dim: true));
            var browse = WelcomeAction(Said("agents_panel.browse"), "extensions", BrowseAgentExtensions);
            browse.Fill = UIFill.None;
            browse.Size = new Vector2(144, 34);
            browse.Anchor = Origin.Left;
            browse.Pivot = Origin.Left;
            browse.Primary = true;
            agentsRows.Add(browse);
            agentsRows.Add(Text(Said("agents_panel.draft_hint"), 0.125f, dim: true));
            return;
        }
        agentsRows.Add(Text(Said("agents_panel.welcome_title"), 0.17f));
        agentsRows.Add(Text(Said("agents_panel.welcome_hint"), 0.15f, dim: true));
        foreach (var (key, icon) in new[] { ("agents_panel.try_map", "map"), ("agents_panel.try_explain", "file-code"), ("agents_panel.try_tests", "beaker") })
        {
            string asked = Said(key);
            agentsRows.Add(WelcomeAction(asked, icon, () =>
            {
                agentDraftContext = null;
                agentsInput.Text = asked;
                agentsInput.Focus();
            }));
        }
    }

    private static Button WelcomeAction(string label, string icon, Action pressed) => new()
    {
        Label = label, Icon = icon, Style = "button_flat", Align = Origin.Left,
        LabelScale = 0.135f, Size = new Vector2(0, 36), Fill = UIFill.Horizontal,
        Animated = false, OnPressed = pressed
    };

    private UIComponent RowOf(HexAgentChat chat, HexAgentEntry entry)
    {
        UISkin? skin = Skin;
        switch (entry.Kind)
        {
            case HexAgentEntryKind.User:
                return Bubble(entry, skin);

            case HexAgentEntryKind.Text:
            {
                // A picture it links under its words, small, a click opening it full screen
                UIComponent said = AgentMarkdown(entry.Text, thinking: false);
                if (PictureStrip(PicturesIn(entry.Text, asked: false), Origin.Left) is not { } pictures)
                    return said;
                var both = new StackPanel { Fill = UIFill.Horizontal, Spacing = 4 };
                both.Add(said);
                both.Add(pictures);
                return both;
            }

            case HexAgentEntryKind.Thinking:
                return AgentMarkdown(entry.Text, thinking: true);

            case HexAgentEntryKind.Note:
                return new Panel { Fill = UIFill.Horizontal, Padding = new UIEdges(2, 0, 2, 0) }.With(Text(NoteText(entry.Text), 0.125f, dim: true));

            case HexAgentEntryKind.Error:
            {
                Vector4 error = skin?.Palette.Error ?? new Vector4(1, 0.35f, 0.35f, 1);
                // The words in the text colour, readable on the tint, the icon saying it's an error, and what to do about it
                // when it's what the chat ended on
                var card = new Panel { Fill = UIFill.Horizontal, Color = error with { W = 0.1f }, Radius = 6, Padding = new UIEdges(9, 6, 9, 6) };
                var inside = card.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 4 });
                inside.Add(Text($"[color:{HexColour(error)}]$(error)[/color] {UIRichText.Literal(entry.Text)}", 0.135f));
                if (chat.Entries.LastOrDefault() == entry)
                {
                    var actions = inside.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 4, Size = new Vector2(0, 26), Anchor = Origin.Left, Pivot = Origin.Left });
                    actions.Add(new Button { Label = Said("agents_panel.error_retry"), Icon = "refresh", Style = "button_flat", Size = new Vector2(104, 26), LabelScale = 0.125f, Animated = false, OnPressed = () => { if (agentChat == chat) RetryAgent(); } });
                    actions.Add(new Button { Label = Said("agents_panel.error_copy"), Icon = "copy", Style = "button_flat", Size = new Vector2(108, 26), LabelScale = 0.125f, Animated = false, OnPressed = () => { UICompositor.Copy(entry.Text); Say(Said("agents_panel.error_copied")); } });
                }
                return card;
            }

            case HexAgentEntryKind.Approval:
                return ApprovalRow(chat, entry, skin);

            case HexAgentEntryKind.Question:
                return QuestionRow(chat, entry, skin);

            default:
                return Groupable(entry) ? ToolGroupRow(chat, entry, skin) : ToolRow(chat, entry, skin);
        }
    }

    private UIComponent AgentMarkdown(string text, bool thinking)
    {
        var padding = thinking ? new UIEdges(10, 3, 10, 6) : new UIEdges(6, 8, 8, 10);
        var panel = new Panel { Fill = UIFill.Horizontal, Padding = padding };
        var written = panel.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 4, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
        // Replies are documents, including their tables and code blocks, rather than one compact label.
        WriteMarkdown(written, WithPlaces(text), string.Empty);
        foreach (var child in Descendants(written))
        {
            if (child is Label label)
            {
                label.OnLink = OpenAgentLink;
                label.Dim = thinking;
            }
            else if (child is NotebookTablePanel table) table.Strip.Visible = false;
        }
        FollowWidth(width => FitWritten(written, MathF.Max(32, width - padding.Total.X), keepWidth: true));
        return panel;
    }

    // What was asked, a raised bubble at the right, as wide as its longest line and no wider than most of the room
    private UIComponent Bubble(HexAgentEntry entry, UISkin? skin)
    {
        Vector4 raised = skin?.Palette.Raised ?? new Vector4(1, 1, 1, 0.08f);
        (string asked, string? with) = entry.AskedText is { } instruction
            ? (instruction, entry.ContextLocation) : SplitAsked(entry.Text);
        string shown = UIRichText.Literal(asked);
        string? under = with is null ? null : UIRichText.Literal(Said("agents_panel.with_file", ("where", with)));

        // Asked while the agent worked, it says so until the agent takes it in, or that it never did
        string? state = entry.Queued ? Said("agents_panel.queued") : entry.Unheard ? Said("agents_panel.unheard") : null;
        if (state is not null)
            under = under is null ? UIRichText.Literal(state) : $"{under}  ·  {UIRichText.Literal(state)}";
        const float scale = 0.17f, padding = 36.0f;
        float longest = float.MaxValue;
        if (skin is not null)
        {
            longest = shown.Split('\n').Max(line => skin.Font.MeasureLine(line, scale, UITextStyle.None, default)) + padding;
            if (under is not null)
                longest = Math.Max(longest, skin.Font.MeasureLine(under, 0.125f, UITextStyle.None, default) + padding);
        }

        var holder = new Panel { Fill = UIFill.Horizontal };
        var bubble = holder.Add(new Panel
        {
            Anchor = Origin.Right,
            Pivot = Origin.Right,
            Size = Vector2.Zero,
            Color = skin is null ? raised : Vector4.Lerp(raised, skin.AccentColor with { W = 1 }, 0.08f),
            Radius = 10,
            Padding = new UIEdges(13, 10, 13, 10)
        });
        // The widest it may be is kept over the narrowest, a side bar narrower than that as Hex comes up made the clamp throw
        FollowWidth(width => bubble.Size = new Vector2(Math.Clamp(longest, 60.0f, Math.Max(64.0f, (width - 12.0f) * AGENTS_BUBBLE)), 0));
        var stack = bubble.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 2 });
        stack.Add(Text(shown, scale));
        if (under is not null)
            stack.Add(Text(under, 0.125f, dim: true));

        // The pictures pasted with it, small under the words, the bubble as wide as they need
        if (PictureStrip(PicturesIn(entry.Text, asked: true), Origin.Left) is StackPanel pictures)
        {
            float wide = pictures.Children.Sum(each => each.Size.X) + pictures.Spacing * (pictures.Children.Count - 1);
            longest = Math.Max(longest == float.MaxValue ? 0 : longest, wide + padding);
            stack.Add(pictures);
        }

        // Held by Hex for the agent, it can go now rather than when the agent next says something, or back to the box
        if (entry.Queued && IsHeld(entry))
        {
            var actions = stack.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 4, Size = new Vector2(0, 24) });
            actions.Add(new Button { Label = Said("agents_panel.send_now"), Icon = "send", Style = "button_flat", Size = new Vector2(104, 24), LabelScale = 0.125f, Animated = false, OnPressed = () => SendHeldNow(entry) });
            actions.Add(new Button { Label = Said("agents_panel.edit_short"), Icon = "edit", Style = "button_flat", Size = new Vector2(70, 24), LabelScale = 0.125f, Animated = false, OnPressed = () => EditHeld(entry) });
        }

        if (entry.Queued || entry.Unheard)
            bubble.Opacity = 0.6f;
        return holder;
    }

    /// <summary>The times of the tools still running brought up to now, a few times a second.</summary>
    private void TickRunningTimes()
    {
        foreach (var (entry, label) in agentsRunningTimes)
        {
            if (entry.State == HexAgentToolState.Running)
                label.Text = Said("agents_panel.tool_running", ("seconds", entry.Seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)));
        }

        // A group's time, all of its calls together, going on while one of them runs
        foreach (var (time, calls) in agentsGroupTimes.Values)
        {
            double seconds = 0.0;
            foreach (HexAgentEntry call in calls)
                seconds += call.Seconds;
            string said = seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s";
            if (time.Text != said)
                time.Text = said;
        }

        TickQuestionTimes();
    }

    // What was asked split from the file put in front of it, which the bubble says under it rather than in it
    private static (string Asked, string? With) SplitAsked(string text)
    {
        const string marker = "\n\n---\n";
        int at = text.IndexOf(marker, StringComparison.Ordinal);
        if (!text.StartsWith("[from ", StringComparison.Ordinal) || at < 0)
            return (text, null);
        int close = text.IndexOf(']');
        return (text[(at + marker.Length)..], close > 6 ? text[6..close] : null);
    }

    private void OpenAgentLink(string target)
    {
        target = target.Trim();
        if (Uri.TryCreate(target, UriKind.Absolute, out Uri? url) && url.Scheme is "http" or "https")
        {
            OpenInBrowser(url.AbsoluteUri);
            return;
        }

        int line = 0, column = 0;
        if (TakeAgentLinkNumber(ref target, ':', out int last))
        {
            if (TakeAgentLinkNumber(ref target, ':', out line))
                column = last;
            else
                line = last;
        }
        else
            TakeAgentLinkNumber(ref target, '#', out line);

        if (AgentFilePath(target) is { } full)
        {
            ShowFace(Hex.Shell.HexFace.Dawn);
            OpenAt(full, Math.Max(0, line - 1), Math.Max(0, column - 1));
        }
    }

    private static bool TakeAgentLinkNumber(ref string path, char separator, out int number)
    {
        number = 0;
        int at = path.LastIndexOf(separator);
        int first = separator == '#' ? at + 2 : at + 1;
        if (at <= 1 || first >= path.Length || (separator == '#' && path[at + 1] != 'L')
            || !int.TryParse(path.AsSpan(first), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out number))
            return false;

        path = path[..at];
        return true;
    }

    private string? AgentFilePath(string path)
    {
        if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(path, UriKind.Absolute, out Uri? file) || !file.IsFile)
                return null;
            path = file.LocalPath;
        }

        // The Markdown file links emitted by agents on Windows can start with a slash before the drive.
        if (OperatingSystem.IsWindows() && path.Length > 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':')
            path = path[1..];

        try
        {
            string full = McpPath(path);
            if (File.Exists(full))
                return full;
            if (path.Contains('%'))
            {
                full = McpPath(Uri.UnescapeDataString(path));
                if (File.Exists(full))
                    return full;
            }
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        {
            // The link is neither a web URL nor a valid path on this platform.
        }

        return null;
    }
}

/// <summary>A component with a child put in it, for building rows in one line.</summary>
internal static class UIComponentWith
{
    public static T With<T>(this T parent, UIComponent child) where T : UIComponent
    {
        parent.Add(child);
        return parent;
    }

    public static T Also<T>(this T made, Action<T> change)
    {
        change(made);
        return made;
    }
}
