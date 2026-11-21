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

// Agentic's Details column, what the agent picked is, who it works with, the messages between them, the tools it
// reached for and the files it touched, and the rows, lines, bars and links the column is made of
internal sealed partial class DawnScene
{
    // What the agent picked is, who it works with, the messages between them, the tools it reached for and the files it touched
    private void DrawNoonContext()
    {
        if (!noonContext.Visible)
            return;

        HexAgentChat? chat = agentChat;
        // Not the clock, which ticks in its own line in place, Details made again every second went through every tool
        // call of the chat each time for its tools and files. Worked out without allocating, it's asked every tick
        long shape = 0, version = 0;
        if (chat is not null)
        {
            var hash = new HashCode();
            hash.Add(chat.Id);
            hash.Add(chat.Running);
            // What waits on the person shows at once, its rows gone through only when the chat moved, not every tick
            if (chat.Id != noonWaitingOf.Chat || chat.Version != noonWaitingOf.Version)
            {
                var asking = new HashCode();
                IReadOnlyList<HexAgentEntry> entries = chat.Entries;
                for (int at = 0; at < entries.Count; at++)
                {
                    HexAgentEntry entry = entries[at];
                    if ((entry.Kind == HexAgentEntryKind.Approval && entry.Answer is not null) || (entry.Kind == HexAgentEntryKind.Question && entry.Respond is not null))
                        asking.Add(entry.GetHashCode());
                }

                noonWaitingOf = (chat.Id, chat.Version, asking.ToHashCode());
            }
            hash.Add(noonWaitingOf.Hash);
            hash.Add(hub.MailOf(chat).All.Count);
            hash.Add(noonTalkShown);
            hash.Add(noonFilesAll);
            HexHubChats chats = agentChats;
            for (int at = 0; at < chats.Count; at++)
            {
                HexAgentChat each = chats[at];
                if (each.Parent != chat.Id)
                    continue;
                hash.Add(each.Id);
                hash.Add(each.Running);
            }

            shape = (uint)hash.ToHashCode() | 1L << 40;
            version = chat.Version;
        }

        // What it's made of moving draws it at once. The chat only having gone on (every word it streams) draws it a second
        // later at the most, and not under a held button, made again between the press and the letting go every click was lost
        long now = System.Environment.TickCount64;
        bool moved = shape != noonContextDrawn.Shape
            || (version != noonContextDrawn.Version && now - noonContextDrawn.At >= CONTEXT_REDRAW_MS && !Engine.Input.Mouse.IsDown(Horizon.Input.MouseButton.Left))
            // Its column dragged to another width, the bars made again as wide as it once the edge is let go of
            || (noonContextWide != noonContextDrawnWide && !Engine.Input.Mouse.IsDown(Horizon.Input.MouseButton.Left));
        if (!moved)
        {
            if (chat is not null && noonClock is not null)
            {
                string clock = ClockLine(chat);
                if (noonClock.Text != clock)
                    noonClock.Text = clock;
            }
            return;
        }
        noonContextDrawn = (shape, version, now);
        noonContextDrawnWide = noonContextWide;

        ClearRows(noonContextRows);
        if (chat is null)
        {
            noonContextRows.Add(ContextText(Said("noon.context_empty"), dim: true));
            return;
        }

        // What it waits on the person for, first, so it's never under the rest
        HexAgentEntry[] waiting = [.. chat.Pending];
        if (waiting.Length > 0)
        {
            ContextSection(Said("noon.waiting_on_you"));
            foreach (HexAgentEntry asked in waiting)
            {
                ContextLine(asked.Kind == HexAgentEntryKind.Approval ? "shield" : "question", OneLine(asked.Text, 120), wrap: true);
                if (asked.Kind != HexAgentEntryKind.Approval)
                    continue;
                var row = noonContextRows.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 6, Size = new Vector2(0, 24) });
                HexAgentEntry request = asked;
                var (allow, deny) = DecideLabels(request);
                row.Add(new Button { Label = allow, Icon = "check", Primary = true, Size = new Vector2(132, 22), LabelScale = 0.115f, Animated = false, OnPressed = () => chat.Decide(request, true) });
                row.Add(new Button { Label = deny, Icon = "close", Size = new Vector2(132, 22), LabelScale = 0.115f, Animated = false, OnPressed = () => chat.Decide(request, false) });
            }
            ContextLink("inbox", $"{Said("noon.stage_inbox")} ({STAGE_KEYS[(int)NoonStage.Inbox]})", () => ShowStage(NoonStage.Inbox));
        }

        // The agent
        ContextSection(Said("noon.agent"));
        string? model = HexAgentModels.Of(chat);
        ContextLine("robot", $"{HexAgentProfiles.All.FirstOrDefault(profile => profile.Id == chat.Agent)?.Name ?? chat.Agent}{(model is null ? string.Empty : " · " + model)}");
        noonClock = ContextLine(chat.Running ? "loading~spin" : "pass", string.Empty);
        noonClock.Text = ClockLine(chat);
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
                // Nearly full, a button beside the bar to compact it now rather than wait for the agent to
                if (chat.ContextLimit > 0 && share >= COMPACT_OFFERED && CanCompact(chat))
                {
                    var row = noonContextRows.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 24), Color = Vector4.Zero });
                    Panel bar = row.Add(ContextBar(share, noonContextWide - noonContext.Padding.Total.X - 10 - COMPACT_BUTTON - 8));
                    bar.Fill = UIFill.None;
                    bar.Anchor = Origin.Left;
                    bar.Pivot = Origin.Left;
                    bar.Size = new Vector2(noonContextWide - noonContext.Padding.Total.X - 10 - COMPACT_BUTTON - 8, 6);
                    row.Add(new Button
                    {
                        Label = Said("noon.compact_now"), Icon = "fold", Danger = true, Anchor = Origin.Right, Pivot = Origin.Right, Size = new Vector2(COMPACT_BUTTON, 22),
                        LabelScale = 0.12f, Animated = false, Tooltip = Said("noon.command_compact"), OnPressed = () => CompactAgent(chat)
                    });
                }
                else if (chat.ContextLimit > 0)
                    noonContextRows.Add(ContextBar(share));
            }

            if (chat.CacheRead > 0 && chat.InputTokens > 0)
                ContextLine("database", Said("noon.cache", ("read", Count(chat.CacheRead)), ("share", (100.0 * chat.CacheRead / chat.InputTokens).ToString("0"))));
            // Counted by their places in one go, a closure and an enumerator over the chat's entries for each count
            int turns = 0, calls = 0;
            IReadOnlyList<HexAgentEntry> said = chat.Entries;
            for (int at = 0; at < said.Count; at++)
            {
                if (said[at].Kind == HexAgentEntryKind.User)
                    turns++;
                else if (said[at].Kind == HexAgentEntryKind.Tool)
                    calls++;
            }

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
                    noonContextRows.Add(LimitBar((float)Math.Clamp(limit.Used, 0.0, 1.0), LimitColour(limit, Skin), noonContextWide - noonContext.Padding.Total.X - 10));
            }
        }

        // Who it works with, a click opens them
        HexAgentChat? parent = chat.Parent is { } up ? ChatById(up) : null;
        List<HexAgentChat> started = [];
        for (int at = 0; at < agentChats.Count; at++)
        {
            if (agentChats[at].Parent == chat.Id)
                started.Add(agentChats[at]);
        }

        if (parent is not null || started.Count > 0)
        {
            ContextSection(Said("noon.family"));
            if (parent is not null)
                ContextLink("arrow-up", Said("noon.started_by", ("name", ChatName(parent.Id))), () => OpenAgentChat(parent));
            foreach (HexAgentChat child in started)
                ContextLink(child.Running ? "loading~spin" : child.Failed ? "error" : "pass", ChatName(child.Id), () => OpenAgentChat(child));
        }

        // The messages it was sent, the latest last, only while the family's conversation has none, which has these among
        // its own and said them twice over
        if (chat.Mail.Count > 0 && hub.MailOf(chat).All.Count == 0)
        {
            ContextSection(Said("noon.messages", ("count", chat.Mail.Count.ToString())));
            long seen = chat.MailSeen;
            foreach (HexAgentMail message in chat.Mail.TakeLast(8))
                ContextLine(message.Seq > seen ? "mail" : "mail-read", $"{message.FromName} · {message.Kind}: {OneLine(message.Text, 120)}", wrap: true);
        }

        DrawFamilyTalk(chat);

        // The tools it reached for, Hex's harness against the shell, and the files it touched
        // The files newest first, the last FILES_KEPT of them, a long chat's hundreds never all gone through or drawn
        var tools = new Dictionary<string, int>();
        var files = new List<string>();
        for (int i = chat.Entries.Count - 1; i >= 0; i--)
        {
            HexAgentEntry entry = chat.Entries[i];
            if (entry.Kind != HexAgentEntryKind.Tool)
                continue;
            string tool = ToolTitle(entry.ToolName ?? "tool");
            tools[tool] = tools.GetValueOrDefault(tool) + 1;
            if (files.Count < FILES_KEPT && FileOfCall(entry) is { } file && !files.Contains(file))
                files.Add(file);
        }

        if (tools.Count > 0)
        {
            int harness = tools.Where(pair => pair.Key.StartsWith("hex_", StringComparison.Ordinal)).Sum(pair => pair.Value);
            ContextSection(Said("noon.tools", ("harness", harness.ToString()), ("all", tools.Values.Sum().ToString())));
            foreach (var (tool, count) in tools.OrderByDescending(pair => pair.Value).Take(10))
            {
                Label row = ContextLine(ToolIcon(tool), tool);
                if (HexAgentRows.ToolColour(tool, Skin?.Palette) is { } ink)
                    row.Text = $"[color:{HexColour(ink)}]$({ToolIcon(tool)}) {UIRichText.Literal(tool)}[/color]";

                // How many times, a count's badge at the line's right end in the tool's own colour, the counts one column
                // down Details rather than "× 27" run on after each name
                row.Add(new Badge
                {
                    Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture), TextScale = 0.105f,
                    Colour = HexAgentRows.ToolColour(tool, Skin?.Palette), Anchor = Origin.Right, Pivot = Origin.Right, Position = new Vector2(-4, 0)
                });
            }
        }

        if (files.Count > 0)
        {
            ContextSection(Said("noon.files"));
            string? folder = explorer.workspace.Folders.FirstOrDefault();
            // Each file by its name first and the folder it's in after it, the full path in the tooltip
            var rows = new List<(string Path, string Shown)>();
            foreach (string file in files.Take(noonFilesAll ? FILES_KEPT : FILES_SHOWN))
            {
                string path = Path.IsPathRooted(file) || folder is null ? file : Path.Combine(folder, file);
                rows.Add((path, folder is not null && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(folder, path) : file));
            }

            List<string> everyShown = rows.ConvertAll(row => row.Shown);
            // What a row's text has to fit in, as the button measures it: its icon and label at its scale in the room its
            // style leaves inside the row
            const float FILE_SCALE = 0.135f;
            float across = noonContextScroll.Size.X - noonContextScroll.Padding.Total.X - 6.0f;
            if (Skin is { } rowSkin)
                across -= (rowSkin.TryGetRegion("button_flat", out var flat) ? flat.ContentOr(rowSkin.ButtonPadding) : rowSkin.ButtonPadding).Total.X;
            foreach (var (path, shown) in rows)
            {
                string opened = path, icon = HexExplorer.FileIcon(path);
                (string name, string where) = Skin?.Font is { } font
                    ? FileRowParts(shown, everyShown, text => font.Measure($"$({icon}) {UIRichText.Literal(text)}", FILE_SCALE).X, across)
                    : (Path.GetFileName(shown), string.Empty);
                noonContextRows.Add(new Button
                {
                    Icon = icon, IconColor = explorer.FileIconColor(path),
                    // Plain text, a button's label doesn't read colour markup, the name and then its folder after a dot
                    Label = UIRichText.Literal(where.Length > 0 ? name + FILE_DOT + where : name),
                    Style = "button_flat", Align = Origin.Left, Fill = UIFill.Horizontal, Tooltip = path, OnContextMenu = at => FileRowMenu(at, opened),
                    Size = new Vector2(0, 22), LabelScale = FILE_SCALE, Animated = false, OnPressed = () =>
                    {
                        ShowFace(HexFace.Dawn);
                        Open(opened);
                    }
                });
            }

            if (files.Count > FILES_SHOWN)
                ContextLink(noonFilesAll ? "chevron-up" : "chevron-down",
                    noonFilesAll ? Said("noon.files_fewer") : Said("noon.files_more", ("count", (files.Count - FILES_SHOWN).ToString(System.Globalization.CultureInfo.InvariantCulture))), () =>
                    {
                        noonFilesAll = !noonFilesAll;
                        noonContextDrawn = CONTEXT_NOT_DRAWN;
                        noonWait = 0.0f;
                    });
        }
    }

    // What a file of Details can be shown in, from its row's menu
    private void FileRowMenu(Vector2 at, string path)
    {
        var menu = new Menu("agent file");
        menu.Add(Said("noon.file_open"), () => { ShowFace(HexFace.Dawn); Open(path); }, icon: "go-to-file");
        menu.AddSeparator();
        menu.Add(Said("noon.file_reveal"), () => { ShowFace(HexFace.Dawn); RevealInExplorer(path); }, icon: "files");
        menu.Add(Said("noon.file_reveal_system"), () => explorer.RevealInExplorer(path), icon: "link-external");
        ContextMenu.Show(chrome, at, menu);
    }

    // The files Details keeps of a chat, the newest, and how many of them show until the rest are asked for
    private const int FILES_KEPT = 25, FILES_SHOWN = 10;

    // Whether Details shows every file it keeps, or the first few
    private bool noonFilesAll;

    // What a row's name and its folder are written with between them
    internal const string FILE_DOT = " · ";

    /// <summary>
    /// A file of Details as its name and the folder it's in, whichever of these is the most that fits <paramref name="room"/>
    /// as <paramref name="width"/> measures it. The whole name always when it fits. The nearest folder after it, or when
    /// another file of the list has the same name the shortest run of folders that tells them apart, and when that
    /// doesn't fit the one folder that differs alone. A name that still doesn't fit is cut in the middle, its end kept.
    /// </summary>
    internal static (string Name, string Where) FileRowParts(string shown, IReadOnlyList<string> all, Func<string, float> width, float room)
    {
        string name = Path.GetFileName(shown);
        string[] parents = FolderNames(shown);
        bool Fits(string called, string folder) => width(folder.Length > 0 ? called + FILE_DOT + folder : called) <= room;

        // The folders to tell it from the files of its name, the nearest one at the least. Down to none for a file without
        string[][] others = [.. all.Where(other => other != shown && string.Equals(Path.GetFileName(other), name, StringComparison.OrdinalIgnoreCase)).Select(FolderNames)];
        int need = Math.Min(1, parents.Length);
        while (need < parents.Length && others.Any(up => up.Length >= need && up[^need..].SequenceEqual(parents[^need..], StringComparer.OrdinalIgnoreCase)))
            need++;
        string whole = string.Join(Path.DirectorySeparatorChar, parents[^need..]);
        if (Fits(name, whole))
            return (name, whole);

        // The folder that differs alone, nothing of the folders for a file whose name tells it from the rest
        string differing = others.Length > 0 && parents.Length > 0 ? parents[^need] : string.Empty;
        if (Fits(name, differing))
            return (name, differing);

        // The whole name before any shortening of it: the folder that differs cut in the middle to what's left
        for (int length = differing.Length - 1; length >= 6; length--)
            if (Fits(name, Elide(differing, length)))
                return (name, Elide(differing, length));

        // Only then the name cut too, the folder at its shortest
        string least = Elide(differing, 6);
        for (int length = name.Length - 1; length >= 6; length--)
            if (Fits(Elide(name, length), least))
                return (Elide(name, length), least);
        return (Elide(name, 6), least);
    }

    private static string[] FolderNames(string path) => (Path.GetDirectoryName(path) ?? string.Empty).Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

    // A text cut in the middle to this many characters with "...", more of its end kept than its start, what tells
    // DawnScene.SelfTest.NoonKeys.cs from its NoonLooks.cs is at the end, with the extension
    private static string Elide(string text, int length)
    {
        if (text.Length <= length || length < 6)
            return text;
        int keep = length - 3, tail = (keep * 11 + 19) / 20;
        return text[..(keep - tail)] + "..." + text[^tail..];
    }

    // The file each call was given, worked out once, its input never changing, rather than its JSON read again each time
    // Details is made
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HexAgentEntry, System.Runtime.CompilerServices.StrongBox<string?>> callFiles = [];

    private static string? FileOfCall(HexAgentEntry call) =>
        callFiles.GetValue(call, each => new System.Runtime.CompilerServices.StrongBox<string?>(FileOf(each.Input))).Value;

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
            .With(new Label { Text = UIRichText.Literal(title), TextScale = 0.135f, Bold = true, Dim = true, Align = Origin.Left, Fill = UIFill.Horizontal });

    private Label ContextLine(string icon, string text, bool wrap = false) =>
        noonContextRows.Add(new Label { Text = $"$({icon}) {UIRichText.Literal(text)}", TextScale = 0.135f, Align = Origin.Left, Wrap = wrap, Fill = UIFill.Horizontal });

    // The line of Details with the turn's clock, put right in place a few times a second
    private Label? noonClock;

    // How the agent is and how long its turn has gone, whole seconds while it runs and the tenths once it's done
    private string ClockLine(HexAgentChat chat) =>
        $"$({(chat.Running ? "loading~spin" : "pass")}) {UIRichText.Literal(chat.Running ? $"{StateWord(chat)} · {chat.TurnSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}s" : $"{StateWord(chat)} · {chat.TurnSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}s")}";

    /// <summary>
    /// The colour of how full a context is, green while there's plenty, turning amber past half and red as it nears its
    /// end, the same on the cards and in Details.
    /// </summary>
    private Vector4 ContextColour(float share)
    {
        UISkin? skin = Skin;
        Vector4 good = skin?.Palette.Success ?? new Vector4(0.35f, 0.8f, 0.45f, 1.0f);
        Vector4 warn = skin?.Palette.Warning ?? new Vector4(1.0f, 0.78f, 0.3f, 1.0f);
        Vector4 bad = skin?.Palette.Error ?? new Vector4(1.0f, 0.42f, 0.38f, 1.0f);
        return share < 0.5f ? good
            : share < 0.8f ? Vector4.Lerp(good, warn, (share - 0.5f) / 0.3f)
            : Vector4.Lerp(warn, bad, Math.Clamp((share - 0.8f) / 0.15f, 0.0f, 1.0f));
    }

    // A bar of how full the context is, in its colour by how full, or of anything else in a colour of its own (the Plan's
    // steps done, which went red as the last one was ticked off, the colour of a context about to run out)
    private Panel ContextBar(float share, float? across = null, Vector4? colour = null)
    {
        UISkin? skin = Skin;
        Vector4 fill = colour ?? ContextColour(share);
        float width = across ?? noonContextWide - noonContext.Padding.Total.X - 10;
        var track = new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 6), Radius = 3, Color = skin?.Palette.Track ?? new Vector4(0, 0, 0, 0.35f) };
        track.Add(new Panel { Anchor = Origin.Left, Pivot = Origin.Left, Size = new Vector2(MathF.Max(3, width * share), 6), Radius = 3, Color = fill });
        return track;
    }

    private static Label ContextText(string text, bool dim) =>
        new() { Text = UIRichText.Literal(text), TextScale = 0.14f, Dim = dim, Wrap = true, Fill = UIFill.Horizontal, Align = Origin.Left };

    private void ContextLink(string icon, string text, Action open) =>
        noonContextRows.Add(new Button
        {
            Icon = icon, Label = UIRichText.Literal(OneLine(text, 40)), Style = "button_flat", Align = Origin.Left, Fill = UIFill.Horizontal,
            Size = new Vector2(0, 22), LabelScale = 0.135f, Animated = false, OnPressed = open
        });
}
