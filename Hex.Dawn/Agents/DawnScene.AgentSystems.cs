using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;

using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// The agent systems Hex can run, a block each at the foot of the agents' list, in Noon under its fleet and in the Agents
// view under its chats. Whether it's installed and its version, the models its chats are on, how many are working, what
// they used and cost this session, the fullest context of them, the compactions and retries, where it stands against
// its plan's limit and when that resets, and what Hex's harness saved it. Built once for the systems there are and
// whether each is installed, its words put right in place after that, the way the cards are, so nothing under the
// pointer is made again while it's being clicked.
internal sealed partial class DawnScene
{
    // The height of a system's head, and how far its lines are put in, under its name past the chevron and the badge
    private const float SYSTEM_HEAD = 24.0f, SYSTEM_INDENT = 46.0f;

    /// <summary>The panel on screen, what puts its words right at a width, and what it was made for.</summary>
    private sealed class SystemsPanel(StackPanel root)
    {
        public StackPanel Root { get; } = root;
        public List<Action<float>> Refresh { get; } = [];
        public string Made { get; set; } = "\0";
        public bool Open { get; set; } = true;

        /// <summary>The systems folded to their head, by id, the rest showing their lines.</summary>
        public HashSet<string> Folded { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Made from the bottom up, its head the last row and each system's lines over its own head, for a panel at the
        /// foot of a column. What's pressed stays under the pointer as what it opens grows up from it.
        /// </summary>
        public bool BottomUp { get; init; }

        /// <summary>What was just opened, a system's id or * for the whole panel, its lines eased in as they're made.</summary>
        public string? Opening { get; set; }

        /// <summary>The panel's head and each system's, as made, for putting them below what they open.</summary>
        public List<UIComponent> Heads { get; } = [];
    }

    private SystemsPanel? noonSystems, agentsSystems;

    // How many ticks are drawn at once after a fold was pressed, rather than on the panels' few times a second. The fold
    // is made on the first and the room it takes is known once that's laid out, so it takes a couple
    private int systemsSettle;

    /// <summary>
    /// How tall the panel is, worked out from its rows rather than read off the last layout, so it's right on the tick the
    /// panel's made again. Read off the layout it was a frame or two behind, the list over the chat sized for what had
    /// been and the chat under it jumping as it caught up.
    /// </summary>
    private static float SystemsTall(SystemsPanel? panel)
    {
        if (panel is null)
            return 0.0f;

        float tall = panel.Root.Padding.Total.Y;
        int shown = 0;
        foreach (UIComponent row in panel.Root.Children)
        {
            if (!row.Visible)
                continue;
            tall += row.Size.Y > 0 ? row.Size.Y : row.Bounds.Height;
            shown++;
        }

        return shown == 0 ? 0.0f : MathF.Ceiling(tall + (shown - 1) * panel.Root.Spacing);
    }

    /// <summary>A fold pressed, made on the next tick and the ticks after it at once, never under the press itself.</summary>
    private void SystemsFolded(SystemsPanel panel)
    {
        panel.Made = "\0";
        systemsSettle = 3;
        Engine.WindowManager.RequestFrames();
    }

    // What each program says its version is, asked once off the editor's thread, empty while it's being asked
    private readonly ConcurrentDictionary<string, string> systemVersions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The systems the panel lists, every agent profile an extension gave a way to run.</summary>
    private HexAgentProfile[] AgentSystems() => [.. HexAgentProfiles.All.Where(profile => extensions.AgentRuns.ContainsKey(profile.Id))];

    /// <summary>A panel made empty in <paramref name="into"/>, drawn by <see cref="DrawSystems"/>.</summary>
    private SystemsPanel MakeSystemsPanel(UIComponent into, bool open, bool bottomUp = false)
    {
        var root = into.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 2, Padding = new UIEdges(0, 6, 0, 0) });
        return new SystemsPanel(root) { Open = open, BottomUp = bottomUp };
    }

    /// <summary>
    /// The panel brought up to date, made again only when the systems, whether they're installed or what's folded
    /// changed, its words put right otherwise. A fold asked for by a click is made on the next tick, never under it.
    /// </summary>
    private void DrawSystems(SystemsPanel panel, float width)
    {
        HexAgentProfile[] systems = AgentSystems();
        string made = $"{panel.Open}|{string.Join(';', systems.Select(system => $"{system.Id}.{StateOf(system) == AgentStates.Missing}.{panel.Folded.Contains(system.Id)}"))}";
        if (made != panel.Made)
        {
            panel.Made = made;
            Empty(panel.Root);
            panel.Refresh.Clear();
            panel.Heads.Clear();

            // The section's head as the Details column has its own, how many systems and how many working on its right
            var head = panel.Root.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 22), Color = Vector4.Zero });
            panel.Heads.Add(head);
            head.Add(new Button
            {
                Style = "button_flat", Fill = UIFill.Both, Animated = false, Label = string.Empty, Tooltip = Said("noon.systems_tip"),
                OnPressed = () =>
                {
                    panel.Open = !panel.Open;
                    panel.Opening = panel.Open ? "*" : null;
                    SystemsFolded(panel);
                }
            });
            head.Add(new Label
            {
                Text = $"$({(panel.Open ? (panel.BottomUp ? "chevron-up" : "chevron-down") : "chevron-right")}) {Said("noon.systems")}", TextScale = 0.125f, Bold = true, Dim = true,
                Anchor = Origin.Left, Pivot = Origin.Left, Position = new Vector2(4, 0), Align = Origin.Left
            });
            var summary = head.Add(new Label { Anchor = Origin.Right, Pivot = Origin.Right, Position = new Vector2(-6, 0), TextScale = 0.11f, Dim = true, Align = Origin.Right });
            panel.Refresh.Add(_ =>
            {
                int working = agentChats.Count(chat => chat.Running && systems.Any(system => system.Id == chat.Agent));
                string said = working > 0
                    ? Said("noon.systems_summary_working", ("count", systems.Length.ToString(CultureInfo.InvariantCulture)), ("working", working.ToString(CultureInfo.InvariantCulture)))
                    : systems.Length.ToString(CultureInfo.InvariantCulture);
                if (summary.Text != said)
                    summary.Text = said;
            });

            if (systems.Length == 0)
                panel.Root.Add(new Label { Text = Said("noon.system_none"), TextScale = 0.115f, Dim = true, Wrap = true, Fill = UIFill.Horizontal, Align = Origin.Left, Padding = new UIEdges(6, 0, 6, 0) });
            // Folded, the section is a line a system, open each shows its lines unless it's been folded on its own
            foreach (HexAgentProfile system in systems)
                panel.Refresh.Add(SystemBlock(panel, system));

            // At the foot of a column the panel's own head is its last row, so it stays under the pointer as the systems
            // open over it. Each system keeps its head over its lines, the lines under another's head read as that one's
            if (panel.BottomUp)
            {
                UIComponent[] rows = [.. panel.Root.Children];
                int systemsFrom = Array.FindIndex(rows, row => panel.Heads.Contains(row) && row != panel.Heads[0]);
                UIComponent[] own = systemsFrom < 0 ? rows : rows[..systemsFrom];
                int at = 0;
                foreach (UIComponent row in (systemsFrom < 0 ? [] : rows[systemsFrom..]).Concat(own))
                    panel.Root.MoveChild(row, at++);
            }
            panel.Opening = null;
        }

        foreach (Action<float> refresh in panel.Refresh)
            refresh(width);
    }

    // A system's block, its head that folds it and its lines while it's open, and what puts its words right
    private Action<float> SystemBlock(SystemsPanel panel, HexAgentProfile system)
    {
        StackPanel into = panel.Root;
        UISkin? skin = Skin;
        Vector4 tint = AgentTint(system.Id, skin);
        Vector4 dim = skin?.Palette.DimText ?? new Vector4(0.6f, 0.6f, 0.6f, 1.0f), error = skin?.Palette.Error ?? new Vector4(1.0f, 0.42f, 0.38f, 1.0f);
        bool missing = StateOf(system) == AgentStates.Missing, open = panel.Open && !panel.Folded.Contains(system.Id);
        string program = extensions.AgentRuns.TryGetValue(system.Id, out var run) ? HexExtensions.Text(run.Run, "program") ?? string.Empty : string.Empty;
        AskVersion(program, missing);

        // The head is a flat button under its words, so the whole row lights up under the pointer and folds the system
        var head = into.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, SYSTEM_HEAD), Color = Vector4.Zero });
        panel.Heads.Add(head);
        int linesFrom = into.Children.Count;
        head.Add(new Button
        {
            Style = "button_flat", Fill = UIFill.Both, Animated = false, Label = string.Empty,
            OnPressed = () =>
            {
                // A system opened from the folded section opens the section with it alone showing its lines
                if (!panel.Open)
                {
                    panel.Open = true;
                    panel.Folded.UnionWith(AgentSystems().Select(each => each.Id));
                    panel.Folded.Remove(system.Id);
                }
                else if (!panel.Folded.Remove(system.Id))
                    panel.Folded.Add(system.Id);
                panel.Opening = panel.Folded.Contains(system.Id) ? null : system.Id;
                SystemsFolded(panel);
            }
        });
        head.Add(new Label { Text = $"$({(open ? "chevron-down" : "chevron-right")})", TextScale = 0.1f, Dim = true, Anchor = Origin.Left, Pivot = Origin.Left, Position = new Vector2(4, 0) });
        var badge = head.Add(new Panel { Anchor = Origin.Left, Pivot = Origin.Left, Position = new Vector2(18, 0), Size = new Vector2(22, 14), Radius = 3, Color = tint with { W = 0.22f } });
        badge.Add(new Label { Text = $"[color:{HexColour(tint)}]{Initials(system.Name)}[/color]", TextScale = 0.085f, Bold = true, Anchor = Origin.Center, Pivot = Origin.Center });
        var name = head.Add(new Label { Anchor = Origin.Left, Pivot = Origin.Left, Position = new Vector2(SYSTEM_INDENT, 0), TextScale = 0.13f, Bold = true, Align = Origin.Left });
        var state = head.Add(new Label { Anchor = Origin.Right, Pivot = Origin.Right, Position = new Vector2(-6, 0), TextScale = 0.11f, Align = Origin.Right });

        // Its lines, an icon each as the Details column has them, each hidden while it has nothing to say, so a system not
        // used yet is its head alone
        // A line is a label in a row put in under the name, a label's own padding isn't kept by the stack it's in
        Label Line() => into.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 16), Padding = new UIEdges(SYSTEM_INDENT, 0, 6, 0), Color = Vector4.Zero, Visible = false })
            .Add(new Label { TextScale = 0.115f, Dim = true, Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left });
        Label? install = open && missing ? Line() : null;
        Label? used = open ? Line() : null, models = open ? Line() : null, context = open ? Line() : null;
        Panel? track = null, fill = null;
        if (open)
        {
            track = into.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 4), Padding = new UIEdges(SYSTEM_INDENT, 0, 6, 0), Color = Vector4.Zero, Visible = false });
            var groove = track.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 4), Radius = 2, Color = skin?.Palette.Track ?? new Vector4(0, 0, 0, 0.35f) });
            fill = groove.Add(new Panel { Anchor = Origin.Left, Pivot = Origin.Left, Size = new Vector2(3, 4), Radius = 2 });
        }
        Label? compacted = open ? Line() : null;

        // Its limits, a line and a bar for each kind it was told of (the session's, the week's), up to three
        var limitSlots = new List<(Label Line, Panel Track, Panel Fill)>();
        for (int i = 0; open && i < 3; i++)
        {
            Label line = Line();
            var limitTrack = into.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 4), Padding = new UIEdges(SYSTEM_INDENT, 0, 6, 0), Color = Vector4.Zero, Visible = false });
            var limitGroove = limitTrack.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 4), Radius = 2, Color = skin?.Palette.Track ?? new Vector4(0, 0, 0, 0.35f) });
            limitSlots.Add((line, limitTrack, limitGroove.Add(new Panel { Anchor = Origin.Left, Pivot = Origin.Left, Size = new Vector2(3, 4), Radius = 2 })));
        }
        Label? harness = open ? Line() : null;

        // What was just opened eases in, a step one after another, rather than being there all at once
        if (panel.Opening is "*" || panel.Opening == system.Id)
        {
            for (int i = linesFrom; i < into.Children.Count; i++)
                SlideRowIn(into.Children[i], (i - linesFrom) * 0.015f);
        }

        static void Put(Label? label, string? text)
        {
            if (label is null)
                return;
            bool shown = !string.IsNullOrEmpty(text);
            UIComponent row = label.Parent ?? label;
            if (row.Visible != shown)
                row.Visible = shown;
            if (shown && label.Text != text)
                label.Text = text!;
        }

        // How many characters a line has room for after its indent and its icon
        static int Fits(float width) => Math.Max(8, (int)((width - SYSTEM_INDENT - 22) / 5.5f));

        return width =>
        {
            HexAgentChat[] chats = [.. agentChats.Where(chat => chat.Agent == system.Id)];
            int working = chats.Count(chat => chat.Running);
            string version = systemVersions.GetValueOrDefault(program) ?? string.Empty;
            string said = UIRichText.Literal(system.Name) + (version.Length > 0 ? $" [color:{HexColour(dim)}]{UIRichText.Literal(version)}[/color]" : string.Empty);
            if (name.Text != said)
                name.Text = said;

            // The limit it's nearest, the latest any of its chats was told, since a plan's limit is the account's
            HexAgentLimit? nearest = chats.Select(chat => chat.Limit).Where(each => each is not null)
                .OrderByDescending(each => each!.State).ThenByDescending(each => each!.Used).FirstOrDefault();
            string status = missing ? $"[color:{HexColour(error)}]$(error) {Said("noon.system_missing")}[/color]"
                : nearest is { State: HexAgentLimitState.Hit } ? $"[color:{HexColour(error)}]$(warning) {UIRichText.Literal(HexAgentLimits.KindName(nearest.Kind))}[/color]"
                : working > 0 ? $"[color:{HexColour(RUNNING_BLUE)}]$(loading~spin) {Said("noon.system_working", ("count", working.ToString(CultureInfo.InvariantCulture)))}[/color]"
                : $"[color:{HexColour(dim)}]$(pass) {Said("noon.system_ready")}[/color]";
            if (state.Text != status)
                state.Text = status;

            if (used is null)
                return;
            Put(install, program.Length == 0 ? null : $"$(info) {UIRichText.Literal(OneLine(Said("noon.system_install", ("program", program)), Fits(width)))}");

            long tokens = chats.Sum(chat => chat.InputTokens + chat.OutputTokens);
            double cost = chats.Sum(chat => chat.Cost);
            Put(used, chats.Length == 0 ? null : "$(comment-discussion) " + (cost > 0
                ? Said(chats.Length == 1 ? "noon.system_chats_one" : "noon.system_chats", ("chats", chats.Length.ToString(CultureInfo.InvariantCulture)), ("tokens", HexAgentLimits.Tokens(tokens)), ("cost", cost.ToString("0.00", CultureInfo.InvariantCulture)))
                : Said(chats.Length == 1 ? "noon.system_chats_free_one" : "noon.system_chats_free", ("chats", chats.Length.ToString(CultureInfo.InvariantCulture)), ("tokens", HexAgentLimits.Tokens(tokens)))));

            string[] inUse = [.. chats.Select(HexAgentModels.Of)
                .Where(model => !string.IsNullOrEmpty(model)).Distinct(StringComparer.OrdinalIgnoreCase)!];
            Put(models, inUse.Length == 0 ? null : $"$(hubot) {UIRichText.Literal(OneLine(string.Join(", ", inUse), Fits(width)))}");

            // The fullest context of its chats, the one nearest to compacting
            HexAgentChat? fullest = chats.Where(chat => chat.ContextLimit > 0 && chat.ContextUsed > 0).MaxBy(chat => (double)chat.ContextUsed / chat.ContextLimit);
            float share = fullest is null ? 0.0f : Math.Clamp((float)fullest.ContextUsed / fullest.ContextLimit, 0.0f, 1.0f);
            Put(context, fullest is null ? null : $"$(dashboard) {UIRichText.Literal(OneLine(Said("noon.system_context", ("share", (share * 100).ToString("0", CultureInfo.InvariantCulture)), ("chat", ChatName(fullest.Id))), Fits(width)))}");
            if (track is not null && fill is not null)
            {
                if (track.Visible != (fullest is not null))
                    track.Visible = fullest is not null;
                fill.Size = new Vector2(MathF.Max(3, (width - SYSTEM_INDENT - 6) * share), 4);
                fill.Color = share > 0.9f ? error
                    : share > 0.75f ? skin?.Palette.Warning ?? new Vector4(1.0f, 0.78f, 0.3f, 1.0f)
                    : tint;
            }

            int compactions = chats.Sum(chat => chat.Compactions), retries = chats.Sum(chat => chat.Retries);
            Put(compacted, compactions + retries == 0 ? null : "$(fold) " + Said("noon.system_compacted", ("count", compactions.ToString(CultureInfo.InvariantCulture)), ("retries", retries.ToString(CultureInfo.InvariantCulture))));

            HexAgentLimit[] limits = KnownLimits(system.Id, chats);
            for (int i = 0; i < limitSlots.Count; i++)
            {
                var (line, limitTrack, limitFill) = limitSlots[i];
                HexAgentLimit? each = i < limits.Length ? limits[i] : null;
                Put(line, each is null ? null : LimitText(each, skin));
                bool bar = each is { Used: >= 0 };
                if (limitTrack.Visible != bar)
                    limitTrack.Visible = bar;
                if (each is not null && bar)
                {
                    limitFill.Size = new Vector2(MathF.Max(3, (width - SYSTEM_INDENT - 6) * (float)Math.Clamp(each.Used, 0.0, 1.0)), 4);
                    limitFill.Color = LimitColour(each, skin);
                }
            }

            var (calls, back, plain) = Harness.Stats.TotalsFor(system.Clients);
            Put(harness, calls == 0 ? null : "$(zap) " + Said(calls == 1 ? "noon.system_harness_one" : "noon.system_harness", ("tokens", HexAgentLimits.Tokens((plain - back) / 4)),
                ("share", plain <= 0 ? "0" : (100.0 * (plain - back) / plain).ToString("0", CultureInfo.InvariantCulture)), ("calls", calls.ToString(CultureInfo.InvariantCulture))));
        };
    }

    /// <summary>
    /// The colour of a kind of limit's bar, the session's blue and the week's violet so the two are told apart at a glance,
    /// whatever the look. Red once it's reached, whichever it is.
    /// </summary>
    internal static Vector4 LimitColour(HexAgentLimit limit, UISkin? skin) =>
        limit.State == HexAgentLimitState.Hit ? skin?.Palette.Error ?? new Vector4(1.0f, 0.42f, 0.38f, 1.0f)
        : limit.Kind switch
        {
            "five_hour" => RUNNING_BLUE,
            "seven_day" => new Vector4(0.66f, 0.52f, 0.98f, 1.0f),
            "seven_day_opus" or "seven_day_sonnet" => new Vector4(0.36f, 0.78f, 0.74f, 1.0f),
            _ => skin?.Palette.Warning ?? new Vector4(1.0f, 0.78f, 0.3f, 1.0f)
        };

    /// <summary>A bar of how much of a limit is gone, as wide as it's given, in the limit's own colour.</summary>
    private Panel LimitBar(float share, Vector4 colour, float width)
    {
        var track = new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 6), Radius = 3, Color = Skin?.Palette.Track ?? new Vector4(0, 0, 0, 0.35f) };
        track.Add(new Panel { Anchor = Origin.Left, Pivot = Origin.Left, Size = new Vector2(MathF.Max(3, width * share), 6), Radius = 3, Color = colour });
        return track;
    }

    // A limit in its line, in the colour of how near it is
    private static string LimitText(HexAgentLimit limit, UISkin? skin)
    {
        string line = $"$({LimitIcon(limit)}) {UIRichText.Literal(HexAgentLimits.LimitLine(limit, DateTimeOffset.Now))}";
        return limit.State switch
        {
            HexAgentLimitState.Hit => $"[color:{HexColour(skin?.Palette.Error ?? new Vector4(1.0f, 0.42f, 0.38f, 1.0f))}]{line}[/color]",
            HexAgentLimitState.Warning => $"[color:{HexColour(skin?.Palette.Warning ?? new Vector4(1.0f, 0.78f, 0.3f, 1.0f))}]{line}[/color]",
            _ => line
        };
    }

    /// <summary>The icon of where an agent stands against a limit.</summary>
    private static string LimitIcon(HexAgentLimit limit) => limit.State switch
    {
        HexAgentLimitState.Hit => "error",
        HexAgentLimitState.Warning => "warning",
        _ => "watch"
    };

    /// <summary>
    /// A program's version asked of it once, off the editor's thread, its first line of --version kept short. Nothing is
    /// asked of a program that isn't there.
    /// </summary>
    private void AskVersion(string program, bool missing)
    {
        if (missing || program.Length == 0 || !systemVersions.TryAdd(program, string.Empty))
            return;
        if (HexAgentProcess.Find(program) is not { } path)
            return;

        Task.Run(() =>
        {
            try
            {
                bool script = path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
                var start = new ProcessStartInfo(script ? "cmd.exe" : path)
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
                };
                if (script)
                {
                    start.ArgumentList.Add("/d");
                    start.ArgumentList.Add("/c");
                    start.ArgumentList.Add(path);
                }
                start.ArgumentList.Add("--version");
                using Process? process = Process.Start(start);
                if (process is null)
                    return;
                Task<string> said = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(8000))
                {
                    process.Kill(entireProcessTree: true);
                    return;
                }

                string first = said.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
                systemVersions[program] = VersionOf(first);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
            }
        });
    }

    /// <summary>The version number out of what a program says, 2.1.326 out of "2.1.326 (Claude Code)", the line otherwise.</summary>
    internal static string VersionOf(string said)
    {
        foreach (string word in said.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = word.TrimStart('v', 'V');
            if (trimmed.Length > 0 && char.IsDigit(trimmed[0]) && trimmed.Contains('.'))
                return trimmed;
        }

        return said.Length > 24 ? said[..24] : said;
    }
}
