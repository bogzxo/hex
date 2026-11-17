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
    }

    private SystemsPanel? noonSystems, agentsSystems;

    // What each program says its version is, asked once off the editor's thread, empty while it's being asked
    private readonly ConcurrentDictionary<string, string> systemVersions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The systems the panel lists, every agent profile an extension gave a way to run.</summary>
    private HexAgentProfile[] AgentSystems() => [.. HexAgentProfiles.All.Where(profile => extensions.AgentRuns.ContainsKey(profile.Id))];

    /// <summary>A panel made empty in <paramref name="into"/>, drawn by <see cref="DrawSystems"/>.</summary>
    private SystemsPanel MakeSystemsPanel(UIComponent into, bool open)
    {
        var root = into.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 2, Padding = new UIEdges(0, 6, 0, 0) });
        return new SystemsPanel(root) { Open = open };
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

            // The section's head as the Details column has its own, how many systems and how many working on its right
            var head = panel.Root.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 22), Color = Vector4.Zero });
            head.Add(new Button
            {
                Style = "button_flat", Fill = UIFill.Both, Animated = false, Label = string.Empty, Tooltip = Said("noon.systems_tip"),
                OnPressed = () =>
                {
                    panel.Open = !panel.Open;
                    panel.Made = "\0";
                }
            });
            head.Add(new Label
            {
                Text = $"$({(panel.Open ? "chevron-down" : "chevron-right")}) {Said("noon.systems")}", TextScale = 0.125f, Bold = true, Dim = true,
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
                panel.Made = "\0";
            }
        });
        head.Add(new Label { Text = $"$({(open ? "chevron-down" : "chevron-right")})", TextScale = 0.1f, Dim = true, Anchor = Origin.Left, Pivot = Origin.Left, Position = new Vector2(4, 0) });
        var badge = head.Add(new Panel { Anchor = Origin.Left, Pivot = Origin.Left, Position = new Vector2(18, 0), Size = new Vector2(22, 14), Radius = 3, Color = tint with { W = 0.22f } });
        badge.Add(new Label { Text = $"[color:{HexColour(tint)}]{Initials(system.Name)}[/color]", TextScale = 0.085f, Bold = true, Anchor = Origin.Center, Pivot = Origin.Center });
        var name = head.Add(new Label { Anchor = Origin.Left, Pivot = Origin.Left, Position = new Vector2(SYSTEM_INDENT, 0), TextScale = 0.13f, Bold = true, Align = Origin.Left });
        var state = head.Add(new Label { Anchor = Origin.Right, Pivot = Origin.Right, Position = new Vector2(-6, 0), TextScale = 0.11f, Align = Origin.Right });

        // Its lines, an icon each as the Details column has them, each hidden while it has nothing to say, so a system not
        // used yet is its head alone
        Label Line() => into.Add(new Label { TextScale = 0.115f, Dim = true, Align = Origin.Left, Fill = UIFill.Horizontal, Padding = new UIEdges(SYSTEM_INDENT, 0, 6, 0), Visible = false });
        Label? install = open && missing ? Line() : null;
        Label? used = open ? Line() : null, models = open ? Line() : null, context = open ? Line() : null;
        Panel? track = null, fill = null;
        if (open)
        {
            track = into.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 4), Padding = new UIEdges(SYSTEM_INDENT, 0, 6, 0), Color = Vector4.Zero, Visible = false });
            var groove = track.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 4), Radius = 2, Color = skin?.Palette.Track ?? new Vector4(0, 0, 0, 0.35f) });
            fill = groove.Add(new Panel { Anchor = Origin.Left, Pivot = Origin.Left, Size = new Vector2(3, 4), Radius = 2 });
        }
        Label? compacted = open ? Line() : null, limit = open ? Line() : null, harness = open ? Line() : null;

        static void Put(Label? label, string? text)
        {
            if (label is null)
                return;
            bool shown = !string.IsNullOrEmpty(text);
            if (label.Visible != shown)
                label.Visible = shown;
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
                ? Said("noon.system_chats", ("chats", chats.Length.ToString(CultureInfo.InvariantCulture)), ("tokens", HexAgentLimits.Tokens(tokens)), ("cost", cost.ToString("0.00", CultureInfo.InvariantCulture)))
                : Said("noon.system_chats_free", ("chats", chats.Length.ToString(CultureInfo.InvariantCulture)), ("tokens", HexAgentLimits.Tokens(tokens)))));

            string[] inUse = [.. chats.Select(chat => chat.Options.GetValueOrDefault("model") is { Length: > 0 } picked ? picked : chat.Model?.Replace("claude-", string.Empty))
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

            Put(limit, nearest is null ? null : LimitText(nearest, skin));

            var (calls, back, plain) = Harness.Stats.TotalsFor(system.Clients);
            Put(harness, calls == 0 ? null : "$(zap) " + Said("noon.system_harness", ("tokens", HexAgentLimits.Tokens((plain - back) / 4)),
                ("share", plain <= 0 ? "0" : (100.0 * (plain - back) / plain).ToString("0", CultureInfo.InvariantCulture)), ("calls", calls.ToString(CultureInfo.InvariantCulture))));
        };
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
