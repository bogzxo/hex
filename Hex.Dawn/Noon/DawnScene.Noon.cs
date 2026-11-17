using System.Numerics;
using System.Text;
using System.Text.Json;

using Hex.Shell;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// Noon, the agents' face of Hex beside Dawn and Dusk on the title bar. The editor's body gives way to three columns over
// the whole window: the fleet down the left (every agent working on the project, the ones started by agents under the
// one that started them, how each is doing, what it's on and what it has waiting for it), the chat of the one picked in
// the middle (the Agents view of the side bar, brought here while Noon is up so the rows, the box, the pickers and the
// steering are the same ones), and on the right what that agent is, who it works with, the messages between them, the
// tools it reached for and the files it touched. Dawn and Noon trade places in the one scene, nothing is made again and
// the agents never stop. docs/agent-workspace-plan.md has where this goes next.
internal sealed partial class DawnScene
{
    // How wide the fleet and the context are, and the width under which the context gives its room to the chat
    private const float NOON_FLEET = 300.0f, NOON_CONTEXT = 320.0f, NOON_CONTEXT_FROM = 1180.0f;

    // How far a chat started by another is stepped in under it in the fleet
    private const float NOON_STEP = 16.0f;

    // How wide the chat's column is at most, centred in the middle
    private const float NOON_COLUMN = 920.0f;

    private StackPanel noonView = null!, noonFleet = null!, noonCenter = null!, noonContext = null!;
    private StackPanel noonFleetRows = null!, noonContextRows = null!;
    private ScrollPanel noonFleetScroll = null!, noonContextScroll = null!;
    private Label noonFleetSummary = null!;

    /// <summary>The face on screen of the editor's two, Dawn or Noon.</summary>
    internal HexFace Face { get; private set; } = HexFace.Dawn;

    // What the fleet and the context were last drawn from, they're drawn again when it moves
    private string noonFleetDrawn = string.Empty, noonContextDrawn = string.Empty;
    private readonly List<Action<float>> noonCards = [];
    private float noonWait;

    // Where the Agents view sat in the side bar, it goes back there as Noon is left
    private int agentsViewIndex = -1;

    private void BuildNoon()
    {
        // In the body's place between the title bar and the status bar
        noonView = root.Insert(IndexIn(root, body) + 1, new StackPanel { Direction = UIDirection.Horizontal, Anchor = Origin.Left, Pivot = Origin.Left, Spacing = 0, Visible = false });

        // The fleet, a card like the side bar
        noonFleet = noonView.Add(new StackPanel { Anchor = Origin.Top, Pivot = Origin.Top, Padding = new UIEdges(10, 10, 10, 10), Spacing = 6 });
        var head = noonFleet.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 22), Color = Vector4.Zero });
        head.Add(new Label { Text = Said("noon.fleet"), TextScale = 0.15f, Bold = true, Anchor = Origin.Left, Pivot = Origin.Left });
        head.Add(new Button { Icon = "add", Style = "button_flat", Size = new Vector2(24, 22), Anchor = Origin.Right, Pivot = Origin.Right, Animated = false, Tooltip = Said("agents_panel.new_chat"), OnPressed = NewAgentChat });
        noonFleetSummary = noonFleet.Add(new Label { Text = string.Empty, TextScale = 0.115f, Dim = true, Align = Origin.Left, Fill = UIFill.Horizontal });
        noonFleetScroll = noonFleet.Add(new ScrollPanel { Fill = UIFill.Horizontal, Padding = new UIEdges(0, 2, 6, 6) });
        noonFleetRows = noonFleetScroll.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 2 });
        // The agent systems at the foot of the fleet, what each is and where it stands, the fleet's list making room
        noonSystems = MakeSystemsPanel(noonFleet, open: true);

        // The chat in the middle, the Agents view is put in it as Noon comes up
        noonCenter = noonView.Add(new StackPanel { Anchor = Origin.Top, Pivot = Origin.Top, Padding = new UIEdges(10, 10, 10, 8), Spacing = 0 });

        // What the agent picked is and does
        noonContext = noonView.Add(new StackPanel { Anchor = Origin.Top, Pivot = Origin.Top, Padding = new UIEdges(10, 10, 10, 10), Spacing = 6 });
        var contextHead = noonContext.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 22), Color = Vector4.Zero });
        contextHead.Add(new Label { Text = Said("noon.context"), TextScale = 0.15f, Bold = true, Anchor = Origin.Left, Pivot = Origin.Left });
        noonContextScroll = noonContext.Add(new ScrollPanel { Fill = UIFill.Horizontal, Padding = new UIEdges(0, 2, 6, 6) });
        noonContextRows = noonContextScroll.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 4 });

        DressChrome();
        commands.Add("View/agent", Said("noon.command"), () => ShowFace(Face == HexFace.Noon ? HexFace.Dawn : HexFace.Noon), "Ctrl+Alt+Shift+N", "robot", UIKeyUse.BoundWhileTyping);
        AddNoonCommands();
    }

    /// <summary>
    /// Dawn or Noon on screen. Noon hides the editor's body and takes the Agents view into its middle, Dawn gives it back
    /// to the side bar where it was. Nothing is made again, so it's the next frame either way.
    /// </summary>
    internal void ShowFace(HexFace face)
    {
        if (face == HexFace.Dusk)
        {
            SwapMode();
            return;
        }

        if (face == Face)
            return;

        Face = face;
        bool noon = face == HexFace.Noon;
        body.Visible = !noon;
        noonView.Visible = noon;
        if (noon)
        {
            agentsViewIndex = IndexIn(left, agentsView);
            noonCenter.Add(agentsView);
            agentsView.Visible = true;
            agentsInput.Focus();
        }
        else
        {
            left.Insert(Math.Max(0, agentsViewIndex), agentsView);
            agentsView.Visible = false;
            PlaceAgentsView(Side == HexSide.Agents);
            if (document.View == HexView.Code || document.IsText)
                code.Focus();
        }

        // Drawn again at its new width straight away, the list over the chat goes or comes back
        agentsDrawn = (-1, -1);
        agentsChipsDrawn = default;
        agentsListDrawn = string.Empty;
        noonFleetDrawn = noonContextDrawn = string.Empty;
        HexModeSwitch.Select(modeToggle, face, PickFace);
        if (Skin is { } skin)
            HexModeSwitch.Dress(modeToggle, skin);
        Engine.WindowManager.RequestFrames();
    }

    /// <summary>
    /// Noon's columns dressed the way Dawn's cards are for the look, the fleet and the details as the side bars (on glass
    /// when the glass is on), the chat in the middle solid as the editor's middle is, and what's in them the card.
    /// </summary>
    private void DressNoon(Horizon.UI.Skinning.UISkin skin, Vector4 card, float inset, float radius, bool glassy)
    {
        if (noonView is null)
            return;

        foreach (StackPanel side in (ReadOnlySpan<StackPanel>)[noonFleet, noonContext])
        {
            side.Inset = inset;
            side.Radius = radius;
            side.Color = card;
            side.Glass = glassy ? SideGlass : null;
            side.Outline = skin.BorderColor;
        }

        noonCenter.Inset = inset;
        noonCenter.Radius = radius;
        noonCenter.Color = card;
        noonCenter.Glass = null;
        noonCenter.Outline = skin.BorderColor;
        noonView.Color = card with { W = 0.0f };
        noonFleetScroll.Color = noonContextScroll.Color = card with { W = 0.0f };
    }

    /// <summary>Noon's columns fitted to the window and drawn again when what they show moved, a few times a second.</summary>
    private void UpdateNoon(float dt)
    {
        if (Face != HexFace.Noon)
            return;

        FitNoon();
        if ((noonWait -= dt) > 0.0f)
            return;
        noonWait = 0.25f;
        DrawNoonFleet();
        DrawNoonContext();
    }

    // The three columns over the body's room, the context giving way on a narrow window
    private void FitNoon()
    {
        UIRect screen = chrome.Root.Bounds;
        if (screen.IsEmpty || top.Bounds.IsEmpty)
            return;

        float width = screen.Width - root.Padding.Total.X;
        float height = screen.Height - root.Padding.Total.Y - top.Bounds.Height - statusBar.Bounds.Height;
        bool context = width >= NOON_CONTEXT_FROM;
        noonContext.Visible = context;
        float middle = width - NOON_FLEET - (context ? NOON_CONTEXT : 0.0f);
        noonView.Size = new Vector2(width, height);

        noonFleet.Size = new Vector2(NOON_FLEET, height);
        float systems = noonSystems is { Root.Bounds.IsEmpty: false } panel ? MathF.Ceiling(panel.Root.Bounds.Height) + noonFleet.Spacing : 0.0f;
        noonFleetScroll.Size = new Vector2(NOON_FLEET - noonFleet.Padding.Total.X, MathF.Max(40, height - noonFleet.Padding.Total.Y - 52 - systems));
        // The chat a column of a reading width down the middle of its card, the way a long line is hard to follow
        float column = MathF.Min(middle - 20.0f, NOON_COLUMN);
        float side = MathF.Floor((middle - column) * 0.5f);
        if (MathF.Abs(noonCenter.Padding.Left - side) > 0.5f)
            noonCenter.Padding = new UIEdges(side, 10, side, 8);
        noonCenter.Size = new Vector2(middle, height);
        agentsView.Size = new Vector2(middle - noonCenter.Padding.Total.X, height - noonCenter.Padding.Total.Y);
        noonContext.Size = new Vector2(NOON_CONTEXT, height);
        noonContextScroll.Size = new Vector2(NOON_CONTEXT - noonContext.Padding.Total.X, MathF.Max(40, height - noonContext.Padding.Total.Y - 28));
    }

    // Every agent of the project, those started by agents under the one that started them
    private void DrawNoonFleet()
    {
        foreach (HexAgentChat chat in agentChats)
            chat.Tick();

        // The cards made again only when the agents or how they're doing changed, their words put right in place otherwise
        List<(HexAgentChat Chat, int Step)> order = ChatsInOrder();
        string said = AgentCardsKey(order);
        float width = NOON_FLEET - noonFleet.Padding.Total.X - 6;
        if (said != noonFleetDrawn)
        {
            noonFleetDrawn = said;
            ClearRows(noonFleetRows);
            noonCards.Clear();
            if (agentChats.Count == 0)
                noonFleetRows.Add(new Label { Text = Said("noon.fleet_empty"), TextScale = 0.13f, Dim = true, Wrap = true, Fill = UIFill.Horizontal, Align = Origin.Left });
            foreach (var (chat, step) in order)
            {
                AgentCard card = AgentCardOf(chat, step, closable: false);
                noonFleetRows.Add(card.Root);
                noonCards.Add(card.Refresh);
            }
        }

        foreach (Action<float> refresh in noonCards)
            refresh(width);
        if (noonSystems is not null)
            DrawSystems(noonSystems, width);

        int running = agentChats.Count(chat => chat.Running);
        double cost = agentChats.Sum(chat => chat.Cost);
        int mail = agentChats.Sum(chat => chat.UnreadMail().Length);
        noonFleetSummary.Text = UIRichText.Literal(Said("noon.summary",
            ("agents", agentChats.Count.ToString()), ("running", running.ToString()),
            ("cost", cost.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)), ("mail", mail.ToString())));
    }

    // What the agent picked is, who it works with, the messages between them, the tools it reached for and the files it touched
    private void DrawNoonContext()
    {
        if (!noonContext.Visible)
            return;

        HexAgentChat? chat = agentChat;
        string said = chat is null ? "none" : $"{chat.Id}:{chat.Version}:{chat.Running}:{(int)chat.TurnSeconds}:{string.Join(',', agentChats.Where(each => each.Parent == chat.Id).Select(each => $"{each.Id}{each.Running}"))}"
            + $":{hub.MailOf(chat).All.Count}:{noonTalkShown}";
        if (said == noonContextDrawn)
            return;
        noonContextDrawn = said;

        ClearRows(noonContextRows);
        if (chat is null)
        {
            noonContextRows.Add(ContextText(Said("noon.context_empty"), dim: true));
            return;
        }

        // The agent
        ContextSection(Said("noon.agent"));
        string? model = chat.Options.GetValueOrDefault("model") is { Length: > 0 } picked ? picked : chat.Model;
        ContextLine("robot", $"{HexAgentProfiles.All.FirstOrDefault(profile => profile.Id == chat.Agent)?.Name ?? chat.Agent}{(model is null ? string.Empty : " · " + model)}");
        ContextLine(chat.Running ? "loading~spin" : "pass", $"{StateWord(chat)} · {chat.TurnSeconds:0.0}s");
        if (chat.InputTokens + chat.OutputTokens > 0)
            ContextLine("graph", $"{Count(chat.InputTokens)} in · {Count(chat.OutputTokens)} out" + (chat.Cost > 0 ? $" · ${chat.Cost.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}" : string.Empty));

        // How full its context is as of its last call, and what the cache saved it
        if (chat.ContextUsed > 0 || chat.CacheRead > 0)
        {
            ContextSection(Said("noon.context_use"));
            if (chat.ContextUsed > 0)
            {
                float share = chat.ContextLimit > 0 ? Math.Clamp((float)chat.ContextUsed / chat.ContextLimit, 0.0f, 1.0f) : 0.0f;
                ContextLine("symbol-namespace", chat.ContextLimit > 0
                    ? Said("noon.context_of", ("used", Count(chat.ContextUsed)), ("limit", Count(chat.ContextLimit)), ("share", (share * 100).ToString("0")))
                    : Said("noon.context_only", ("used", Count(chat.ContextUsed))));
                if (chat.ContextLimit > 0)
                    noonContextRows.Add(ContextBar(share));
            }

            if (chat.CacheRead > 0 && chat.InputTokens > 0)
                ContextLine("database", Said("noon.cache", ("read", Count(chat.CacheRead)), ("share", (100.0 * chat.CacheRead / chat.InputTokens).ToString("0"))));
            int turns = chat.Entries.Count(entry => entry.Kind == HexAgentEntryKind.User);
            int calls = chat.Entries.Count(entry => entry.Kind == HexAgentEntryKind.Tool);
            ContextLine("history", Said("noon.turns", ("turns", turns.ToString()), ("calls", calls.ToString())));
        }

        // What its system said along the way, how often its context was summed up, calls tried again, and how near it is
        // to the plan's limit
        if (chat.Compactions > 0 || chat.Retries > 0 || chat.Limits.Count > 0)
        {
            ContextSection(Said("noon.limits"));
            if (chat.Compactions > 0)
                ContextLine("fold", chat.CompactedFrom > 0
                    ? Said("noon.compacted_from", ("count", chat.Compactions.ToString()), ("before", HexAgentLimits.Tokens(chat.CompactedFrom)), ("after", chat.CompactedTo > 0 ? HexAgentLimits.Tokens(chat.CompactedTo) : "?"))
                    : Said("noon.compacted", ("count", chat.Compactions.ToString())));
            if (chat.Retries > 0)
                ContextLine("refresh", Said("noon.retries", ("count", chat.Retries.ToString())));
            // Each kind of limit with a bar of its own colour, the session's and the week's told apart at a glance
            foreach (HexAgentLimit limit in HexAgentLimits.Across([chat]))
            {
                ContextLine(LimitIcon(limit), HexAgentLimits.LimitLine(limit, DateTimeOffset.Now));
                if (limit.Used >= 0)
                    noonContextRows.Add(LimitBar((float)Math.Clamp(limit.Used, 0.0, 1.0), LimitColour(limit, Skin), NOON_CONTEXT - noonContext.Padding.Total.X - 10));
            }
        }

        // Who it works with, a click opens them
        HexAgentChat? parent = chat.Parent is { } up ? ChatById(up) : null;
        HexAgentChat[] started = [.. agentChats.Where(each => each.Parent == chat.Id)];
        if (parent is not null || started.Length > 0)
        {
            ContextSection(Said("noon.family"));
            if (parent is not null)
                ContextLink("arrow-up", Said("noon.started_by", ("name", ChatName(parent.Id))), () => OpenAgentChat(parent));
            foreach (HexAgentChat child in started)
                ContextLink(child.Running ? "loading~spin" : child.Failed ? "error" : "pass", ChatName(child.Id), () => OpenAgentChat(child));
        }

        // The messages it was sent, the latest last
        if (chat.Mail.Count > 0)
        {
            ContextSection(Said("noon.messages", ("count", chat.Mail.Count.ToString())));
            long seen = chat.MailSeen;
            foreach (HexAgentMail message in chat.Mail.TakeLast(8))
                ContextLine(message.Seq > seen ? "mail" : "mail-read", $"{message.FromName} · {message.Kind}: {OneLine(message.Text, 120)}", wrap: true);
        }

        DrawFamilyTalk(chat);

        // The tools it reached for, Hex's harness against the shell, and the files it touched
        var tools = new Dictionary<string, int>();
        var files = new List<string>();
        foreach (HexAgentEntry entry in chat.Entries)
        {
            if (entry.Kind != HexAgentEntryKind.Tool)
                continue;
            string tool = ToolTitle(entry.ToolName ?? "tool");
            tools[tool] = tools.GetValueOrDefault(tool) + 1;
            if (FileOf(entry.Input) is { } file && !files.Contains(file))
                files.Add(file);
        }

        if (tools.Count > 0)
        {
            int harness = tools.Where(pair => pair.Key.StartsWith("hex_", StringComparison.Ordinal)).Sum(pair => pair.Value);
            ContextSection(Said("noon.tools", ("harness", harness.ToString()), ("all", tools.Values.Sum().ToString())));
            foreach (var (tool, count) in tools.OrderByDescending(pair => pair.Value).Take(10))
                ContextLine(ToolIcon(tool), $"{tool} × {count}");
        }

        if (files.Count > 0)
        {
            ContextSection(Said("noon.files"));
            string? folder = explorer.workspace.Folders.FirstOrDefault();
            foreach (string file in files.TakeLast(12))
            {
                string path = Path.IsPathRooted(file) || folder is null ? file : Path.Combine(folder, file);
                string shown = folder is not null && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(folder, path) : file;
                ContextLink("file-code", shown, () =>
                {
                    ShowFace(HexFace.Dawn);
                    Open(path);
                });
            }
        }
    }

    // The file a tool was given, from the arguments that name one
    private static string? FileOf(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(input);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            foreach (string key in (ReadOnlySpan<string>)["file_path", "path", "notebook_path", "filename"])
            {
                if (document.RootElement.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } named && Path.HasExtension(named) && !named.Contains('*'))
                    return named;
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static void ClearRows(UIComponent rows)
    {
        foreach (UIComponent row in rows.Children.ToArray())
            rows.Remove(row);
    }

    private void ContextSection(string title) =>
        noonContextRows.Add(new Panel { Fill = UIFill.Horizontal, Padding = new UIEdges(0, 8, 0, 0), Color = Vector4.Zero })
            .With(new Label { Text = UIRichText.Literal(title), TextScale = 0.125f, Bold = true, Dim = true, Align = Origin.Left, Fill = UIFill.Horizontal });

    private void ContextLine(string icon, string text, bool wrap = false) =>
        noonContextRows.Add(new Label { Text = $"$({icon}) {UIRichText.Literal(text)}", TextScale = 0.125f, Align = Origin.Left, Wrap = wrap, Fill = UIFill.Horizontal });

    // A bar of how full the context is, the accent until it's getting full, amber past three quarters and red near the end
    private Panel ContextBar(float share)
    {
        UISkin? skin = Skin;
        Vector4 fill = share > 0.9f ? skin?.Palette.Error ?? new Vector4(1.0f, 0.42f, 0.38f, 1.0f)
            : share > 0.75f ? skin?.Palette.Warning ?? new Vector4(1.0f, 0.78f, 0.3f, 1.0f)
            : skin?.Palette.Accent ?? new Vector4(0.85f, 0.42f, 0.3f, 1.0f);
        float width = NOON_CONTEXT - noonContext.Padding.Total.X - 10;
        var track = new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 6), Radius = 3, Color = skin?.Palette.Track ?? new Vector4(0, 0, 0, 0.35f) };
        track.Add(new Panel { Anchor = Origin.Left, Pivot = Origin.Left, Size = new Vector2(MathF.Max(3, width * share), 6), Radius = 3, Color = fill });
        return track;
    }

    private static Label ContextText(string text, bool dim) =>
        new() { Text = UIRichText.Literal(text), TextScale = 0.13f, Dim = dim, Wrap = true, Fill = UIFill.Horizontal, Align = Origin.Left };

    private void ContextLink(string icon, string text, Action open) =>
        noonContextRows.Add(new Button
        {
            Icon = icon, Label = UIRichText.Literal(OneLine(text, 40)), Style = "button_flat", Align = Origin.Left, Fill = UIFill.Horizontal,
            Size = new Vector2(0, 20), LabelScale = 0.125f, Animated = false, OnPressed = open
        });
}
