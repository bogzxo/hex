using System.Globalization;
using System.Numerics;

using Hex.Core.Git;
using Hex.Shell;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

/// <summary>What Agentic's middle shows, the chat on screen, or what of every agent waits on the person, or what the chat's agent changed, or its plan.</summary>
internal enum NoonStage
{
    Chat,
    Inbox,
    Changes,
    Plan
}

// Agentic's middle as tabs over it. Chat is the chat as ever. Inbox is everything of every agent that waits on the
// person, its requests to do something with Allow and Deny there, its questions with their answers, and the chats that
// finished or failed while they were elsewhere. Changes is what the chat's agent wrote, turn by turn, each file to see
// against how it was before the turn and to put back from the checkpoint Hex took as the turn began (HexCheckpoints).
// Plan is the to-do list the agent keeps as it works. Each is drawn again only when the agents moved, and the wheel over
// the margins scrolls whichever is up.
internal sealed partial class DawnScene
{
    // How tall the tabs over the middle are
    private const float NOON_TABS = 32.0f;

    private NoonStage noonStage = NoonStage.Chat;
    private StackPanel noonTabs = null!;
    private readonly Button[] noonTabButtons = new Button[4];
    private ScrollPanel noonInbox = null!, noonChanges = null!, noonPlan = null!;
    private StackPanel noonInboxRows = null!, noonChangesRows = null!, noonPlanRows = null!;

    // What the stage on screen was drawn from, and what the tabs say
    private long noonStageDrawn = NOT_DRAWN;

    // Which chat and tab the stage was last drawn for and when, and how often at the most it's drawn again for the same
    private int noonStageShown = -1;
    private long noonStageAt;
    private const long STAGE_REDRAW_MS = 400;
    private (int Inbox, int Changes, int Done, int All) noonTabsSaid = (-1, -1, -1, -1);

    private static readonly string[] STAGE_KEYS = ["Alt+1", "Alt+2", "Alt+3", "Alt+4"];

    private void BuildNoonStage()
    {
        noonTabs = noonCenter.Add(new StackPanel
        {
            Direction = UIDirection.Horizontal, Spacing = 2, Fill = UIFill.Horizontal, Size = new Vector2(0, NOON_TABS),
            Padding = new UIEdges(0, 0, 0, 6), Color = Vector4.Zero
        });
        // The fleet brought back from here while it's away
        noonFleetBack = noonTabs.Add(new Button
        {
            Icon = "layout-sidebar-left", Style = "button_flat", Size = new Vector2(30, 26), Animated = false, Visible = false,
            Tooltip = Said("noon.fleet_show", ("key", FLEET_KEY)), OnPressed = () => ShowFleet(true)
        });
        commands.Add("Agents/chats", Said("noon.command_fleet"), () => ShowFleet(noonFleetHidden), FLEET_KEY, "layout-sidebar-left", UIKeyUse.BoundWhileTyping).IsEnabled = InAgentic;
        foreach (NoonStage stage in Enum.GetValues<NoonStage>())
        {
            NoonStage shown = stage;
            noonTabButtons[(int)stage] = noonTabs.Add(new Button
            {
                Icon = StageIcon(stage), Label = Said($"noon.stage_{stage.ToString().ToLowerInvariant()}"), Style = "button_flat",
                Size = new Vector2(118, 26), LabelScale = 0.13f, Animated = false,
                Tooltip = Said("noon.stage_tip", ("name", Said($"noon.stage_{stage.ToString().ToLowerInvariant()}")), ("key", STAGE_KEYS[(int)stage])),
                OnPressed = () => ShowStage(shown)
            });
        }

        (noonInbox, noonInboxRows) = StagePanel();
        (noonChanges, noonChangesRows) = StagePanel();
        (noonPlan, noonPlanRows) = StagePanel();

        commands.Add("Agents/chats", Said("noon.command_stage_chat"), () => ShowStage(NoonStage.Chat), STAGE_KEYS[0], "comment-discussion", UIKeyUse.BoundWhileTyping).IsEnabled = InAgentic;
        commands.Add("Agents/chats", Said("noon.command_stage_inbox"), () => ShowStage(NoonStage.Inbox), STAGE_KEYS[1], "inbox", UIKeyUse.BoundWhileTyping).IsEnabled = InAgentic;
        commands.Add("Agents/chats", Said("noon.command_stage_changes"), () => ShowStage(NoonStage.Changes), STAGE_KEYS[2], "diff", UIKeyUse.BoundWhileTyping).IsEnabled = InAgentic;
        commands.Add("Agents/chats", Said("noon.command_stage_plan"), () => ShowStage(NoonStage.Plan), STAGE_KEYS[3], "checklist", UIKeyUse.BoundWhileTyping).IsEnabled = InAgentic;
        commands.Add("Agents/chats", Said("noon.command_allow"), () => DecideWaiting(true), ALLOW_KEY, "check", UIKeyUse.BoundWhileTyping);
        commands.Add("Agents/chats", Said("noon.command_deny"), () => DecideWaiting(false), DENY_KEY, "close", UIKeyUse.BoundWhileTyping);
    }

    private bool InAgentic() => Face == HexFace.Noon;

    private static string StageIcon(NoonStage stage) => stage switch
    {
        NoonStage.Inbox => "inbox",
        NoonStage.Changes => "diff",
        NoonStage.Plan => "checklist",
        _ => "comment-discussion"
    };

    // A stage's scroll and the rows in it, over the middle in the chat's place
    private (ScrollPanel, StackPanel) StagePanel()
    {
        var scroll = noonCenter.Add(new ScrollPanel { Fill = UIFill.Horizontal, Visible = false, Padding = new UIEdges(2, 4, 6, 8) });
        var rows = scroll.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 8 });
        return (scroll, rows);
    }

    /// <summary>A stage of Agentic's middle on screen, drawn on the next tick.</summary>
    internal void ShowStage(NoonStage stage)
    {
        noonStage = stage;
        ApplyStage();
        noonStageDrawn = NOT_DRAWN;
        noonWait = 0.0f;
    }

    // The stage's panel up and the rest down, its tab marked
    private void ApplyStage()
    {
        if (Face != HexFace.Noon)
            return;
        agentsView.Visible = noonStage == NoonStage.Chat;
        noonInbox.Visible = noonStage == NoonStage.Inbox;
        noonChanges.Visible = noonStage == NoonStage.Changes;
        noonPlan.Visible = noonStage == NoonStage.Plan;
        for (int i = 0; i < noonTabButtons.Length; i++)
            noonTabButtons[i].Selected = i == (int)noonStage;
        if (noonStage == NoonStage.Chat)
            agentsInput.Focus();
    }

    /// <summary>The scroll the wheel over the middle's margins goes to, the stage's that's up.</summary>
    private ScrollPanel? NoonWheeled() => noonStage switch
    {
        NoonStage.Inbox => noonInbox,
        NoonStage.Changes => noonChanges,
        NoonStage.Plan => noonPlan,
        _ => agentsScroll
    };

    /// <summary>The tabs' counts put right and the stage on screen drawn again when the agents moved, a few times a second.</summary>
    private void DrawNoonStage()
    {
        long key = (hub.Version * 31 + (agentChat?.Id ?? -1)) * 31 + (int)noonStage;
        if (key == noonStageDrawn)
            return;
        // Another chat or tab is drawn at once. The agents only having gone on (one streaming moves it every word) waits a
        // little, and for a held button to be let go, an Allow made again under the press was lost
        long now = System.Environment.TickCount64;
        int shown = (agentChat?.Id ?? -1) * 8 + (int)noonStage;
        if (shown == noonStageShown && noonStageDrawn != NOT_DRAWN && (now - noonStageAt < STAGE_REDRAW_MS || Engine.Input.Mouse.IsDown(Horizon.Input.MouseButton.Left)))
            return;
        noonStageDrawn = key;
        noonStageShown = shown;
        noonStageAt = now;

        // Counted again only as the agents move, going through every turn's tool calls each tick allocated while nothing did
        var (_, done, all) = PlanOf(agentChat);
        // The changes' count, the files the latest turn that wrote any wrote
        var counts = (InboxCount(), agentChat is null ? 0 : ChangedFilesOf(agentChat, latestOnly: true).FirstOrDefault().Files?.Count ?? 0, done, all);
        if (counts != noonTabsSaid)
        {
            noonTabsSaid = counts;
            noonTabButtons[(int)NoonStage.Inbox].Label = counts.Item1 > 0 ? $"{Said("noon.stage_inbox")}  {counts.Item1}" : Said("noon.stage_inbox");
            noonTabButtons[(int)NoonStage.Changes].Label = counts.Item2 > 0 ? $"{Said("noon.stage_changes")}  {counts.Item2}" : Said("noon.stage_changes");
            noonTabButtons[(int)NoonStage.Plan].Label = all > 0 ? $"{Said("noon.stage_plan")}  {done}/{all}" : Said("noon.stage_plan");
            // The inbox's count in the accent while something waits, it's what wants the person
            noonTabButtons[(int)NoonStage.Inbox].Primary = counts.Item1 > 0;
        }

        switch (noonStage)
        {
            case NoonStage.Inbox:
                DrawInbox();
                break;
            case NoonStage.Changes:
                DrawChanges();
                break;
            case NoonStage.Plan:
                DrawPlan();
                break;
        }
    }

    /* Inbox */

    // How many things wait on the person, every agent's requests and questions and the chats to look at
    private int InboxCount()
    {
        int count = 0;
        for (int i = 0; i < agentChats.Count; i++)
        {
            HexAgentChat chat = agentChats[i];
            foreach (HexAgentEntry _ in chat.Pending)
                count++;
            if (!chat.Running && chat.Unread && chat != agentChat)
                count++;
        }

        return count;
    }

    private void DrawInbox()
    {
        ClearRows(noonInboxRows);
        bool any = false;
        foreach (HexAgentChat chat in FleetOrder())
        {
            foreach (HexAgentEntry asked in chat.Pending.ToArray())
            {
                any = true;
                if (asked.Kind == HexAgentEntryKind.Approval)
                    InboxApproval(chat, asked);
                else
                    InboxQuestion(chat, asked);
            }
        }

        foreach (HexAgentChat chat in FleetOrder())
        {
            if (chat.Running || !chat.Unread || chat == agentChat)
                continue;
            any = true;
            var card = InboxCard(chat, chat.Failed ? "error" : "pass", Said(chat.Failed ? "noon.inbox_failed" : "noon.inbox_done", ("name", ChatName(chat.Id))));
            card.Add(ContextText(OneLine(chat.LastReply(400), 300), dim: true));
            InboxButtons(card, chat, []);
        }

        if (!any)
            StageEmpty(noonInboxRows, "inbox", Said("noon.inbox_empty"));
    }

    /// <summary>
    /// A tab with nothing in it, what comes there and what can be done next. A dim line alone left the person wondering
    /// what to do, so the chat is always a key away and Plan can ask the agent for one.
    /// </summary>
    private void StageEmpty(StackPanel rows, string icon, string text, (string Label, string Icon, Action Then)? also = null)
    {
        rows.Add(new Label { Text = $"$({icon})", TextScale = 0.3f, Dim = true, Align = Origin.Left, Size = new Vector2(0, 34) });
        rows.Add(ContextText(text, dim: true));
        var buttons = rows.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 6, Size = new Vector2(0, 28) });
        if (also is var (label, mark, then))
            buttons.Add(new Button { Label = label, Icon = mark, Primary = true, Size = new Vector2(MathF.Ceiling(label.Length * 6.6f) + 40, 26), LabelScale = 0.125f, Animated = false, OnPressed = then });
        string back = $"{Said("noon.stage_back")} ({STAGE_KEYS[(int)NoonStage.Chat]})";
        buttons.Add(new Button { Label = back, Icon = "comment-discussion", Size = new Vector2(MathF.Ceiling(back.Length * 6.6f) + 40, 26), LabelScale = 0.125f, Animated = false, OnPressed = () => ShowStage(NoonStage.Chat) });
    }

    // What asking an agent for a plan puts in its box, left there to be changed before it's sent
    private void AskForPlan()
    {
        ShowStage(NoonStage.Chat);
        agentsInput.Text = Said("noon.plan_ask_text");
        agentsInput.Focus();
    }

    // A card of the inbox, the agent's badge and who it is over what it is
    private StackPanel InboxCard(HexAgentChat chat, string icon, string title)
    {
        var card = noonInboxRows.Add(new StackPanel
        {
            Fill = UIFill.Horizontal, Spacing = 5, Padding = new UIEdges(12, 10, 12, 10), Radius = 8,
            Color = (Skin?.Palette.Field ?? new Vector4(0.1f, 0.1f, 0.12f, 1.0f)) with { W = 0.6f }, Outline = Skin?.BorderColor ?? Vector4.Zero
        });
        Vector4 tint = AgentTint(chat.Agent, Skin);
        card.Add(new Label
        {
            Text = $"[color:{HexColour(tint)}]$({icon})[/color] {UIRichText.Literal(title)}", TextScale = 0.14f, Bold = true, Align = Origin.Left, Fill = UIFill.Horizontal
        });
        return card;
    }

    // The buttons along a card's foot, its own and Open to go to the chat
    private void InboxButtons(StackPanel card, HexAgentChat chat, (string Label, string Icon, bool Primary, Action Pressed)[] own)
    {
        var row = card.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 6, Size = new Vector2(0, 26) });
        foreach (var (label, icon, primary, pressed) in own)
            row.Add(new Button { Label = label, Icon = icon, Primary = primary, Size = new Vector2(MathF.Max(80, label.Length * 7.0f + 34), 24), LabelScale = 0.125f, Animated = false, OnPressed = pressed });
        row.Add(new Button
        {
            Label = Said("noon.inbox_open"), Icon = "go-to-file", Style = "button_flat", Size = new Vector2(80, 24), LabelScale = 0.125f, Animated = false,
            OnPressed = () =>
            {
                OpenAgentChat(chat);
                ShowStage(NoonStage.Chat);
            }
        });
    }

    private void InboxApproval(HexAgentChat chat, HexAgentEntry asked)
    {
        var card = InboxCard(chat, "shield", Said("noon.inbox_asks", ("name", ChatName(chat.Id))));
        card.Add(ContextText(asked.Text, dim: false));
        if (!string.IsNullOrWhiteSpace(asked.Input))
            card.Add(ContextText(OneLine(asked.Input!, 400), dim: true)).Also(label => label.CodeFont = code.Font);
        var (allow, deny) = DecideLabels(asked);
        InboxButtons(card, chat,
        [
            (allow, "check", true, () => chat.Decide(asked, true)),
            (deny, "close", false, () => chat.Decide(asked, false))
        ]);
    }

    private void InboxQuestion(HexAgentChat chat, HexAgentEntry asked)
    {
        var card = InboxCard(chat, "question", Said("noon.inbox_question", ("name", ChatName(chat.Id))));
        card.Add(ContextText(asked.Text, dim: false));
        // Its answers a button each, the one it would pick marked. One that takes several, or an answer of the person's
        // own, is answered in its chat where the card has its toggles and its box
        var choices = new List<(string, string, bool, Action)>();
        string[] offered = asked.Choices ?? [];
        for (int i = 0; !asked.Multiple && i < offered.Length && i < 4; i++)
        {
            string choice = offered[i];
            choices.Add((choice, asked.Recommended == i ? "star-full" : "circle-large-outline", asked.Recommended == i, () => chat.Reply(asked, choice)));
        }
        InboxButtons(card, chat, [.. choices]);
    }

    /* Requests answered by key */

    // The keys for the request waiting, answered without the mouse from anywhere in Hex
    private const string ALLOW_KEY = "Ctrl+Alt+Y", DENY_KEY = "Ctrl+Alt+N";

    /// <summary>
    /// The request waiting answered, the open chat's oldest first, then the oldest of any chat in the fleet's order. Said
    /// so when nothing waits.
    /// </summary>
    private void DecideWaiting(bool allow)
    {
        if (KeyedRequest() is not var (asking, request))
        {
            Say(Said("noon.nothing_to_allow"));
            return;
        }

        asking.Decide(request, allow);
        // Which agent it was for said too, with several waiting the keys answered one the person couldn't tell from the rest
        Say($"{(allow ? Said("agents_panel.allowed") : Said("agents_panel.denied"))} {ChatName(asking.Id)}: {OneLine(request.Text, 80)}");
    }

    /// <summary>
    /// The request the keys answer, the open chat's oldest, then the oldest of any chat in the fleet's order. Its buttons
    /// are the only ones that say the keys, so it's plain which one they're for.
    /// </summary>
    private (HexAgentChat Chat, HexAgentEntry Request)? KeyedRequest()
    {
        HexAgentEntry? Oldest(HexAgentChat chat) => HexAgentHub.Approvals(chat).FirstOrDefault();
        HexAgentChat? asking = agentChat is { } open && Oldest(open) is not null ? open : FleetOrder().FirstOrDefault(chat => Oldest(chat) is not null);
        return asking is not null && Oldest(asking) is { } request ? (asking, request) : null;
    }

    // Allow and Deny for a request, saying their keys when they're the ones the keys answer
    private (string Allow, string Deny) DecideLabels(HexAgentEntry request) => KeyedRequest()?.Request == request
        ? (Said("noon.allow_key", ("key", ALLOW_KEY)), Said("noon.deny_key", ("key", DENY_KEY)))
        : (Said("agents_panel.allow"), Said("agents_panel.deny"));

    /* Changes */

    // The checkpoint taken as each turn began, by what was asked then, a repository's top and its tree for each folder of
    // the workspace that's one. Kept for as long as the row is
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HexAgentEntry, List<(string Top, string Tree)>> checkpoints = [];

    /// <summary>
    /// A checkpoint of every repository of the workspace as a turn begins, taken off the editor's thread, for that turn's
    /// changes to be seen against and put back from.
    /// </summary>
    private void TakeCheckpoint(HexAgentEntry asked, Action then)
    {
        string[] folders = [.. explorer.workspace.Folders];
        var taken = new List<(string, string)>();
        checkpoints.AddOrUpdate(asked, taken);
        InBackground("checkpoint", () =>
        {
            var tops = new HashSet<string>(PlatformPaths.Comparer);
            var made = new List<(string, string)>();
            foreach (string folder in folders)
            {
                foreach (string top in HexCheckpoints.ReposOf(folder))
                {
                    if (tops.Add(top) && HexCheckpoints.Take(top) is { } tree)
                        made.Add((top, tree));
                }
            }

            return made;
        }, made =>
        {
            lock (taken)
                taken.AddRange(made);
            then();
        }, quiet: true);
    }

    // Whether a tool writes files by its name, the edits and writes of every agent system
    private static bool Writes(string? tool)
    {
        string name = (tool ?? string.Empty).ToLowerInvariant();
        return name.Contains("edit", StringComparison.Ordinal) || name.Contains("write", StringComparison.Ordinal) || name.Contains("patch", StringComparison.Ordinal)
            || name.Contains("replace", StringComparison.Ordinal) || name.Contains("filechange", StringComparison.Ordinal) || name.Contains("sed_file", StringComparison.Ordinal);
    }

    // Each turn of a chat that wrote files, newest first, what was asked and the files, at most the turns a chat keeps
    private List<(HexAgentEntry Asked, int Number, List<string> Files)> ChangedFilesOf(HexAgentChat chat, bool latestOnly = false)
    {
        var turns = new List<(HexAgentEntry, int, List<string>)>();
        HexAgentEntry? asked = null;
        List<string>? files = null;
        int number = 0;
        foreach (HexAgentEntry entry in chat.Entries)
        {
            if (entry.Kind == HexAgentEntryKind.User)
            {
                if (asked is not null && files is { Count: > 0 })
                    turns.Add((asked, number, files));
                asked = entry;
                files = [];
                number++;
            }
            else if (entry.Kind == HexAgentEntryKind.Tool && files is not null && Writes(entry.ToolName) && FileOfCall(entry) is { } file && !files.Contains(file))
                files.Add(file);
        }

        if (asked is not null && files is { Count: > 0 })
            turns.Add((asked, number, files));
        turns.Reverse();
        if (latestOnly && turns.Count > 1)
            turns.RemoveRange(1, turns.Count - 1);
        return turns;
    }

    // A file as a whole path, from the workspace's first folder for one an agent named from there
    private string FullPathOf(string file)
    {
        string? folder = explorer.workspace.Folders.FirstOrDefault();
        return Path.IsPathRooted(file) || folder is null ? file : Path.GetFullPath(Path.Combine(folder, file));
    }

    // The checkpoint a file of a turn goes back to, the repository it's in, null when the turn has none for it
    private static (string Top, string Tree)? CheckpointFor(HexAgentEntry asked, string path)
    {
        if (!checkpoints.TryGetValue(asked, out var taken))
            return null;
        lock (taken)
        {
            (string Top, string Tree)? best = null;
            foreach (var each in taken)
            {
                if (PlatformPaths.IsUnder(path, each.Top) && (best is null || each.Top.Length > best.Value.Top.Length))
                    best = each;
            }

            return best;
        }
    }

    private void DrawChanges()
    {
        ClearRows(noonChangesRows);
        if (agentChat is not { } chat || ChangedFilesOf(chat) is not { Count: > 0 } turns)
        {
            StageEmpty(noonChangesRows, "diff", Said("noon.changes_empty"));
            return;
        }

        string? folder = explorer.workspace.Folders.FirstOrDefault();
        foreach (var (asked, number, files) in turns)
        {
            var card = noonChangesRows.Add(new StackPanel
            {
                Fill = UIFill.Horizontal, Spacing = 3, Padding = new UIEdges(12, 10, 12, 10), Radius = 8,
                Color = (Skin?.Palette.Field ?? new Vector4(0.1f, 0.1f, 0.12f, 1.0f)) with { W = 0.6f }, Outline = Skin?.BorderColor ?? Vector4.Zero
            });
            var head = card.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 26), Color = Vector4.Zero });
            head.Add(new Label
            {
                Text = $"$(history) {UIRichText.Literal(Said("noon.changes_turn", ("number", number.ToString(CultureInfo.InvariantCulture)), ("asked", OneLine(asked.AskedText ?? asked.Text, 70))))}",
                TextScale = 0.135f, Bold = true, Anchor = Origin.Left, Pivot = Origin.Left, Align = Origin.Left
            });
            bool checkpointed = files.Any(file => CheckpointFor(asked, FullPathOf(file)) is not null);
            if (checkpointed)
                head.Add(new Button
                {
                    Label = Said("noon.changes_restore_turn"), Icon = "discard", Danger = true, Size = new Vector2(150, 24), LabelScale = 0.12f, Animated = false,
                    Anchor = Origin.Right, Pivot = Origin.Right, OnPressed = () => AskRestore(asked, [.. files.Select(FullPathOf)])
                });
            else
                card.Add(ContextText(Said("noon.changes_no_checkpoint"), dim: true));

            foreach (string file in files)
            {
                string path = FullPathOf(file);
                string shown = folder is not null && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(folder, path) : file;
                var row = card.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 24), Color = Vector4.Zero });
                row.Add(new Button
                {
                    Icon = HexExplorer.FileIcon(path), IconColor = explorer.FileIconColor(path), Label = UIRichText.Literal(OneLine(shown, 70)),
                    Style = "button_flat", Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left, Size = new Vector2(420, 22), LabelScale = 0.13f, Animated = false,
                    OnPressed = () =>
                    {
                        ShowFace(HexFace.Dawn);
                        Open(path);
                    }
                });
                if (CheckpointFor(asked, path) is null)
                    continue;
                row.Add(new Button
                {
                    Label = Said("noon.changes_restore"), Icon = "discard", Style = "button_flat", Size = new Vector2(90, 22), LabelScale = 0.12f, Animated = false,
                    Anchor = Origin.Right, Pivot = Origin.Right, OnPressed = () => AskRestore(asked, [path])
                });
                row.Add(new Button
                {
                    Label = Said("noon.changes_diff"), Icon = "diff", Style = "button_flat", Size = new Vector2(70, 22), LabelScale = 0.12f, Animated = false,
                    Anchor = Origin.Right, Pivot = Origin.Right, Position = new Vector2(-94, 0), OnPressed = () => DiffSinceTurn(asked, path)
                });
            }
        }
    }

    /// <summary>A file of a turn against how it was before the turn, in a diff tab of the editor, read off the editor's thread.</summary>
    private void DiffSinceTurn(HexAgentEntry asked, string path)
    {
        if (CheckpointFor(asked, path) is not { } at)
            return;
        InBackground("checkpoint", () => (Before: HexCheckpoints.TextAt(at.Top, at.Tree, path) ?? string.Empty, Now: File.Exists(path) ? File.ReadAllText(path) : string.Empty), texts =>
        {
            if (texts.Before.Replace("\r\n", "\n") == texts.Now.Replace("\r\n", "\n"))
            {
                Say(Said("noon.changes_unchanged", ("name", Path.GetFileName(path))));
                return;
            }

            ShowFace(HexFace.Dawn);
            OpenDiff(path, "checkpoint", Said("noon.changes_diff_title", ("name", Path.GetFileName(path))), GitDiffText.Of(texts.Before, texts.Now), 0);
        }, quiet: true);
    }

    /// <summary>Files of a turn put back as they were before it, once the person says so, it can't be undone.</summary>
    private void AskRestore(HexAgentEntry asked, string[] paths)
    {
        string yes = Said("noon.changes_restore_button");
        string what = paths.Length == 1 ? Path.GetFileName(paths[0]) : HexText.Count("file", paths.Length);
        string text = Said("noon.changes_restore_text", ("files", what));
        // Changed again after the turn, by a later one, another agent or by hand, it's said, that's lost as well
        string[] since = [.. paths.Where(path => ChangedSince(asked, path)).Select(Path.GetFileName).OfType<string>()];
        if (since.Length > 0)
            text += "\n\n" + Said("noon.changes_restore_since", ("files", string.Join(", ", since.Take(5))));
        Context.Windows.Ask(Said("noon.changes_restore_title"), text, [Said("git.discard_no"), yes], answer =>
        {
            if (answer != yes)
                return;
            // Looked at again as it's said, a file changed while the question was up is asked about once more
            if (paths.Any(path => ChangedSince(asked, path) && !since.Contains(Path.GetFileName(path))))
            {
                AskRestore(asked, paths);
                return;
            }

            var each = paths.Select(path => (Path: path, At: CheckpointFor(asked, path))).Where(pair => pair.At is not null).ToArray();
            InBackground("restore", () => each.Select(pair => HexCheckpoints.Restore(pair.At!.Value.Top, pair.At.Value.Tree, pair.Path)).Count(why => why is null), restored =>
            {
                Say(Said("noon.changes_restored", ("count", HexText.Count("file", restored))));
                noonStageDrawn = NOT_DRAWN;
            });
        }, risky: yes);
    }

    // A little longer than a write takes to reach the disk, a file's time within it of the agent's last write is that write
    private const long WRITE_SLACK_MS = 3000;

    /// <summary>
    /// Whether a file of a turn was changed after the turn last wrote it, by a write of any agent after it or on the disk
    /// later than that, which putting it back would lose as well.
    /// </summary>
    private bool ChangedSince(HexAgentEntry asked, string path)
    {
        // When the turn last wrote it, from the turn's own rows, and whether any agent wrote it after that
        var writes = new List<(long Started, long At, bool InTurn)>();
        foreach (HexAgentChat chat in agentChats)
        {
            bool inTurn = false;
            foreach (HexAgentEntry entry in chat.Entries)
            {
                if (entry == asked)
                    inTurn = true;
                else if (entry.Kind == HexAgentEntryKind.User)
                    inTurn = false;
                if (entry.Kind == HexAgentEntryKind.Tool && Writes(entry.ToolName) && FileOfCall(entry) is { } file
                    && PlatformPaths.Comparer.Equals(FullPathOf(file), path))
                    writes.Add((entry.Started, entry.Ended ?? entry.Started, inTurn));
            }
        }

        long last = -1;
        foreach (var write in writes)
        {
            if (write.InTurn)
                last = Math.Max(last, write.At);
        }

        if (last < 0)
            return false;
        foreach (var write in writes)
        {
            if (!write.InTurn && write.Started > last)
                return true;
        }
        try
        {
            // The ticks of the write turned into the clock's time, a file written on the disk well after it changed since
            DateTime wrote = DateTime.UtcNow.AddMilliseconds(last - System.Environment.TickCount64);
            return File.Exists(path) && File.GetLastWriteTimeUtc(path) > wrote.AddMilliseconds(WRITE_SLACK_MS);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /* Plan */

    // The agent's to-do list as it last wrote it, a row of [x], [~] or [ ] a line (DawnScene's note of Claude Code's
    // TodoWrite and Codex's plan), how many are done and how many there are
    private static (string[] Items, int Done, int All) PlanOf(HexAgentChat? chat)
    {
        if (chat is null)
            return ([], 0, 0);
        for (int i = chat.Entries.Count - 1; i >= 0; i--)
        {
            HexAgentEntry entry = chat.Entries[i];
            if (entry.Kind != HexAgentEntryKind.Note || entry.Text.Length < 4)
                continue;
            string[] lines = entry.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0 || !lines.All(line => line.StartsWith("[x] ", StringComparison.Ordinal) || line.StartsWith("[~] ", StringComparison.Ordinal) || line.StartsWith("[ ] ", StringComparison.Ordinal)))
                continue;
            return (lines, lines.Count(line => line.StartsWith("[x]", StringComparison.Ordinal)), lines.Length);
        }

        return ([], 0, 0);
    }

    private void DrawPlan()
    {
        ClearRows(noonPlanRows);
        var (items, done, all) = PlanOf(agentChat);
        if (all == 0)
        {
            StageEmpty(noonPlanRows, "checklist", Said("noon.plan_empty"), CanComposeAgent() ? (Said("noon.plan_ask"), "edit", AskForPlan) : null);
            return;
        }

        noonPlanRows.Add(ContextText(Said("noon.plan_progress", ("done", done.ToString(CultureInfo.InvariantCulture)), ("all", all.ToString(CultureInfo.InvariantCulture))), dim: true));
        noonPlanRows.Add(ContextBar((float)done / all, MathF.Max(120, noonPlan.Bounds.Width - 30)));
        Vector4 accent = Skin?.Palette.Accent ?? new Vector4(0.29f, 0.69f, 0.71f, 1.0f);
        foreach (string item in items)
        {
            string text = item[4..];
            string line = item[1] switch
            {
                'x' => $"[color:{HexColour(Skin?.Palette.Success ?? new Vector4(0.36f, 0.85f, 0.45f, 1.0f))}]$(pass-filled)[/color] [color:{HexColour(Skin?.Palette.DimText ?? new Vector4(0.6f, 0.6f, 0.6f, 1.0f))}]{UIRichText.Literal(text)}[/color]",
                '~' => $"[color:{HexColour(accent)}]$(loading~spin)[/color] [b]{UIRichText.Literal(text)}[/b]",
                _ => $"$(circle-large-outline) {UIRichText.Literal(text)}"
            };
            noonPlanRows.Add(new Label { Text = line, TextScale = 0.145f, Wrap = true, Fill = UIFill.Horizontal, Align = Origin.Left });
        }
    }
}
