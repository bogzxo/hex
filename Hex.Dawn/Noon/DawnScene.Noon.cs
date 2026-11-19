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

    // How tall the fleet's filter is, the key that hides and shows the fleet, and the width of the window under which
    // the fleet hides by itself for the chat to have the room
    private const float NOON_FILTER = 26.0f;
    private const string FLEET_KEY = "Ctrl+Alt+B";
    private const float NOON_FLEET_FROM = 900.0f;

    private TextBox noonFleetFilter = null!;

    // Whether the person put the fleet away on a wide window, and the button over the middle that brings it back
    private bool noonFleetHidden;
    private Button noonFleetBack = null!;

    // On a window too narrow for the fleet beside the chat, whether it's been opened in the chat's place. Any pick (a
    // card, even the chat already open, a key, a new chat) puts the middle back (FleetPicked). The wide window's choice is
    // left alone, so widening the window again shows the fleet the way it was
    private bool noonFleetOpened;
    private bool noonNarrow;

    /// <summary>
    /// The fleet shown or put away. On a wide window the middle takes its room and that's remembered. On a narrow one it
    /// takes the middle's place for a pick, the middle coming back as one is picked or it's put away again.
    /// </summary>
    private void ShowFleet(bool shown)
    {
        if (noonNarrow)
        {
            noonFleetOpened = shown;
            if (Face == HexFace.Noon)
            {
                // Laid out now, so the keyboard only goes to what's on screen: the filter while the fleet is the page,
                // the chat's box once it's gone if the chat is the stage up (the inbox or the changes keep no box)
                FitNoon();
                if (shown)
                    noonFleetFilter.Focus();
                else if (noonStage == NoonStage.Chat)
                    agentsInput.Focus();
                else if (compositor.Focus == noonFleetFilter)
                    compositor.Focus = null;
            }
        }
        else
            noonFleetHidden = !shown;
        noonFleetDrawn = NOT_DRAWN;
        noonWait = 0.0f;
        Engine.WindowManager.RequestFrames();
    }

    /// <summary>
    /// A chat picked (or a new one begun) from wherever: the fleet opened over a narrow window gives the middle back, the
    /// same chat picked again too, which nothing changing about the chat on screen would never have told.
    /// </summary>
    private void FleetPicked()
    {
        if (noonFleetOpened)
            ShowFleet(false);
    }

    /// <summary>The fleet's key, putting it away when it's on screen and bringing it when it isn't, whatever the width.</summary>
    private void ToggleFleet() => ShowFleet(!noonFleet.Visible);

    // How wide the chat's column is at most, centred in the middle
    private const float NOON_COLUMN = 920.0f;

    private StackPanel noonView = null!, noonFleet = null!, noonCenter = null!, noonContext = null!;
    private StackPanel noonFleetRows = null!, noonContextRows = null!;
    private ScrollPanel noonFleetScroll = null!, noonContextScroll = null!;

    // How full a context is before Details offers to compact it, and how wide the button for it is
    private const float COMPACT_OFFERED = 0.95f, COMPACT_BUTTON = 110.0f;

    // The line at the foot of the fleet of what the harness saved, and how tall it is
    private Button? noonHarness;
    private const float NOON_HARNESS = 22.0f;
    private Label noonFleetSummary = null!;

    /// <summary>The face on screen of the editor's two, Dawn or Noon.</summary>
    internal HexFace Face { get; private set; } = HexFace.Dawn;

    // What the fleet and the context were last drawn from, they're drawn again when it moves
    private long noonFleetDrawn = NOT_DRAWN;
    private (long Shape, long Version, long At) noonContextDrawn = CONTEXT_NOT_DRAWN;
    private static readonly (long Shape, long Version, long At) CONTEXT_NOT_DRAWN = (-1, -1, 0);

    // How often at the most Details is made again for a chat that only went on, a running one changing every word
    private const long CONTEXT_REDRAW_MS = 1000;
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
        // Put away for the chat to have the room, the tabs over the middle bring it back
        head.Add(new Button
        {
            Icon = "layout-sidebar-left-off", Style = "button_flat", Size = new Vector2(24, 22), Anchor = Origin.Right, Pivot = Origin.Right, Position = new Vector2(-28, 0),
            Animated = false, Tooltip = Said("noon.fleet_hide", ("key", FLEET_KEY)), OnPressed = () => ShowFleet(false)
        });
        noonFleetSummary = noonFleet.Add(new Label { Text = string.Empty, TextScale = 0.125f, Dim = true, Align = Origin.Left, Fill = UIFill.Horizontal });
        // Narrowed to the chats whose name, agent system or number has what's typed, for a fleet longer than the column
        noonFleetFilter = noonFleet.Add(new TextBox { Fill = UIFill.Horizontal, Size = new Vector2(0, NOON_FILTER), Placeholder = Said("noon.fleet_filter"), MaxLength = 80 });
        noonFleetFilter.OnChanged = _ =>
        {
            noonFleetDrawn = NOT_DRAWN;
            noonWait = 0.0f;
        };
        // No room kept at its right, the cards line up with the head's edge as they do with its left
        noonFleetScroll = noonFleet.Add(new ScrollPanel { Fill = UIFill.Horizontal, Padding = new UIEdges(0, 2, 0, 6) });
        noonFleetRows = noonFleetScroll.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 2 });
        // The agent systems at the foot of the fleet, what each is and where it stands, the fleet's list making room
        noonSystems = MakeSystemsPanel(noonFleet, open: true, bottomUp: true);
        // Under it all, what Hex's harness saved the agents this run, opening the window of it
        noonHarness = noonFleet.Add(new Button
        {
            Style = "button_flat", Fill = UIFill.Horizontal, Size = new Vector2(0, NOON_HARNESS), Align = Origin.Left, LabelScale = 0.13f, Animated = false,
            Tooltip = Said("agents_panel.harness_tip"), OnPressed = ShowHarnessSaved
        });
        agentsHarnessShown = (-1, -1);

        // The chat in the middle, the Agents view is put in it as Noon comes up
        // The wheel turned over the margins round the chat's column scrolls the chat, as it would over the column itself
        noonCenter = noonView.Add(new HexWheelStack { Anchor = Origin.Top, Pivot = Origin.Top, Padding = new UIEdges(10, 10, 10, 8), Spacing = 0, Wheeled = NoonWheeled });
        // The tabs over it and the stages other than the chat, the chat's view going in after them as Agentic comes up
        BuildNoonStage();

        // What the agent picked is and does
        noonContext = noonView.Add(new StackPanel { Anchor = Origin.Top, Pivot = Origin.Top, Padding = new UIEdges(10, 10, 10, 10), Spacing = 6 });
        var contextHead = noonContext.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 22), Color = Vector4.Zero });
        contextHead.Add(new Label { Text = Said("noon.context"), TextScale = 0.15f, Bold = true, Anchor = Origin.Left, Pivot = Origin.Left });
        // A few pixels at its left, an icon that moves is drawn still once its clip cuts it, and the icons of the rows sat
        // right on that edge (the running one never turned)
        noonContextScroll = noonContext.Add(new ScrollPanel { Fill = UIFill.Horizontal, Padding = new UIEdges(3, 2, 6, 6) });
        noonContextRows = noonContextScroll.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 4 });

        DressChrome();
        commands.Add("Agents/open", Said("noon.command"), () => ShowFace(Face == HexFace.Noon ? HexFace.Dawn : HexFace.Noon), "Ctrl+Alt+Shift+N", "robot", UIKeyUse.BoundWhileTyping);
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

        // No Agentic with No AI on, it was never made. A workspace left in it, --face noon or a call asking for it
        // stays in Dawn
        if (face == HexFace.Noon && HexAi.Off)
        {
            Say(Said("no_ai.no_agentic"));
            return;
        }

        Face = face;
        // The fleet opened over a narrow window is for one pick, Noon comes back with the chat
        noonFleetOpened = false;
        bool noon = face == HexFace.Noon;
        body.Visible = !noon;
        noonView.Visible = noon;
        if (noon)
        {
            agentsViewIndex = IndexIn(left, agentsView);
            noonCenter.Add(agentsView);
            agentsView.Visible = true;
            agentsInput.Focus();
            // The stage it was left on, the chat unless another was picked
            ApplyStage();
            noonStageDrawn = NOT_DRAWN;
            // And the server off, what switching it on saves, once a session
            HintMcp();
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
        agentsListDrawn = NOT_DRAWN;
        noonFleetDrawn = NOT_DRAWN;
        noonContextDrawn = CONTEXT_NOT_DRAWN;
        HexModeSwitch.Select(modeToggle, face, PickFace);
        UseFaceTheme();
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
        UpdateFamilyWindow();
        if (Face != HexFace.Noon)
            return;

        FitNoon();
        if ((noonWait -= dt) > 0.0f)
            return;
        noonWait = 0.25f;
        DrawNoonFleet();
        DrawNoonContext();
        DrawNoonStage();
    }

    // The three columns over the body's room, the context giving way on a narrow window
    private void FitNoon()
    {
        UIRect screen = chrome.Root.Bounds;
        if (screen.IsEmpty || top.Bounds.IsEmpty)
            return;

        float width = screen.Width - root.Padding.Total.X;
        float height = screen.Height - root.Padding.Total.Y - top.Bounds.Height - statusBar.Bounds.Height;
        // With no chats Details has nothing to say, and the welcome in the middle takes its room
        bool context = width >= NOON_CONTEXT_FROM && agentChats.Count > 0;
        noonContext.Visible = context;
        // The fleet away when it's been put away, or the window is too narrow for it beside a chat worth reading, the
        // tabs' button bringing it back. Brought back on a narrow window it takes the whole room in the chat's place
        // rather than squeezing the chat, until a chat is picked or it's put away
        bool narrow = width < NOON_FLEET_FROM;
        if (narrow != noonNarrow)
        {
            noonNarrow = narrow;
            noonFleetOpened = false;
            noonFleetDrawn = NOT_DRAWN;
        }
        bool over = narrow && noonFleetOpened;
        bool fleet = narrow ? over : !noonFleetHidden;
        noonFleet.Visible = fleet;
        noonCenter.Visible = !over;
        noonFleetBack.Visible = !fleet;
        float fleetWidth = over ? width : NOON_FLEET;
        noonView.Size = new Vector2(width, height);

        noonFleet.Size = new Vector2(fleetWidth, height);
        float systems = noonSystems is not null && SystemsTall(noonSystems) is > 0 and var tall ? tall + noonFleet.Spacing : 0.0f;
        noonFleetScroll.Size = new Vector2(fleetWidth - noonFleet.Padding.Total.X, MathF.Max(40, height - noonFleet.Padding.Total.Y - 52 - NOON_FILTER - noonFleet.Spacing - systems - NOON_HARNESS - noonFleet.Spacing));
        if (over)
            return;
        // The chat a column of a reading width down the middle of its card, the way a long line is hard to follow
        float middle = width - (fleet ? fleetWidth : 0.0f) - (context ? NOON_CONTEXT : 0.0f);
        float column = MathF.Min(middle - 20.0f, NOON_COLUMN);
        float side = MathF.Floor((middle - column) * 0.5f);
        if (MathF.Abs(noonCenter.Padding.Left - side) > 0.5f)
            noonCenter.Padding = new UIEdges(side, 10, side, 8);
        noonCenter.Size = new Vector2(middle, height);
        // The stage under its tabs, the chat and the others the same room
        var stage = new Vector2(middle - noonCenter.Padding.Total.X, height - noonCenter.Padding.Total.Y - NOON_TABS);
        agentsView.Size = stage;
        noonInbox.Size = noonChanges.Size = noonPlan.Size = stage;
        FitNoonTabs(stage.X - (noonFleetBack.Visible ? noonFleetBack.Size.X + noonTabs.Spacing : 0.0f));
        noonContext.Size = new Vector2(NOON_CONTEXT, height);
        noonContextScroll.Size = new Vector2(NOON_CONTEXT - noonContext.Padding.Total.X, MathF.Max(40, height - noonContext.Padding.Total.Y - 28));
    }

    // Every agent of the project, those started by agents under the one that started them
    private void DrawNoonFleet()
    {
        // The chats gone through by their places, LINQ and foreach over the hub's list made garbage four times a second
        int running = 0, mail = 0;
        double cost = 0;
        for (int i = 0; i < agentChats.Count; i++)
        {
            HexAgentChat chat = agentChats[i];
            chat.Tick();
            running += chat.Running ? 1 : 0;
            cost += chat.Cost;
            mail += chat.UnreadMailCount();
        }

        // The cards made again only when the agents or how they're doing changed, their words put right in place otherwise
        // Narrowed by the filter, a new list only while something's typed in it
        float width = noonFleet.Size.X - noonFleet.Padding.Total.X - 6;
        // What the cards are made from, cheaply: the chats' versions, which one is open and what's typed in the filter. The
        // lists of the fleet's order and groups were made four times a second to find nothing had changed, most of what an
        // idle Agentic allocated
        long cheap = 17 + (agentChat?.Id ?? 0) * 7919L + string.GetHashCode(noonFleetFilter.Text, StringComparison.Ordinal);
        for (int i = 0; i < agentChats.Count; i++)
            cheap = cheap * 31 + agentChats[i].Id * 131L + agentChats[i].Version + (agentChats[i].Running ? 1 : 0) + (agentChats[i].Unread ? 2 : 0);
        if (cheap != noonFleetCheap || noonFleetDrawn == NOT_DRAWN)
        {
            string filter = noonFleetFilter.Text.Trim();
            List<(HexAgentChat Chat, int Step)> order = FleetShown();
            long said = AgentCardsKey(order) * 31 + string.GetHashCode(filter, StringComparison.Ordinal);
            foreach (FleetGroup group in FamilyGroups(order))
                said = said * 3 + (int)group;
            // Not made again under a held button, a card made again between the press and the letting go lost the click, the
            // same as Details and the tabs wait for it
            if (said != noonFleetDrawn && !Engine.Input.Mouse.IsDown(Horizon.Input.MouseButton.Left))
            {
                noonFleetDrawn = said;
                ClearRows(noonFleetRows);
                noonCards.Clear();
                // No agents at all, what to do about it, a chat to start or a system to install
                if (agentChats.Count == 0)
                {
                    noonFleetRows.Add(new Label { Text = Said("noon.fleet_empty_title"), TextScale = 0.145f, Bold = true, Align = Origin.Left, Fill = UIFill.Horizontal });
                    noonFleetRows.Add(new Label { Text = Said("noon.fleet_empty"), TextScale = 0.13f, Dim = true, Wrap = true, Fill = UIFill.Horizontal, Align = Origin.Left });
                    noonFleetRows.Add(new Button { Label = Said("noon.fleet_empty_new"), Icon = "add", Primary = true, Fill = UIFill.Horizontal, Size = new Vector2(0, 28), LabelScale = 0.13f, Animated = false, OnPressed = NewAgentChat });
                    noonFleetRows.Add(new Button { Label = Said("noon.fleet_empty_systems"), Icon = "extensions", Style = "button_flat", Fill = UIFill.Horizontal, Size = new Vector2(0, 26), LabelScale = 0.13f, Animated = false, OnPressed = BrowseAgentExtensions });
                }
                else if (order.Count == 0)
                {
                    noonFleetRows.Add(new Label { Text = Said("noon.fleet_none_fit"), TextScale = 0.13f, Dim = true, Wrap = true, Fill = UIFill.Horizontal, Align = Origin.Left });
                    // The way back to every chat right there, rather than finding the box and emptying it
                    noonFleetRows.Add(new Button
                    {
                        Label = Said("noon.fleet_clear"), Icon = "clear-all", Style = "button_flat", Fill = UIFill.Horizontal, Size = new Vector2(0, 26), LabelScale = 0.13f, Animated = false,
                        OnPressed = () =>
                        {
                            noonFleetFilter.Text = string.Empty;
                            noonFleetDrawn = NOT_DRAWN;
                        }
                    });
                }
                // A heading over each group once there's more than one, the cards of a family under the group of its most urgent
                FleetGroup[] groups = FamilyGroups(order);
                bool headed = groups.Distinct().Count() > 1;
                for (int i = 0; i < order.Count; i++)
                {
                    var (chat, step) = order[i];
                    AgentCard card = AgentCardOf(chat, step, closable: false);
                    // The heading goes in with the group's first card, one row for each card still, which the keys and the
                    // clicks find by its place
                    if (headed && (i == 0 || groups[i] != groups[i - 1]))
                    {
                        var with = new StackPanel { Fill = UIFill.Horizontal, Spacing = 0 };
                        with.Add(new Label
                        {
                            Text = Said(groups[i] switch { FleetGroup.NeedsYou => "noon.fleet_needs_you", FleetGroup.Working => "noon.fleet_working", _ => "noon.fleet_rest" }).ToUpperInvariant(),
                            TextScale = 0.125f, Dim = true, Bold = true, Align = Origin.Left, Fill = UIFill.Horizontal, Size = new Vector2(0, i == 0 ? 18 : 26), Padding = new UIEdges(10, 0, 0, 2), Anchor = Origin.BottomLeft, Pivot = Origin.BottomLeft
                        });
                        with.Add(card.Root);
                        card = card with { Root = with };
                    }
                    noonFleetRows.Add(card.Root);
                    noonCards.Add(card.Refresh);
                }
            }

            // Taken as drawn only once the cards are what it says, a rebuild put off under a held button is tried again
            if (said == noonFleetDrawn)
                noonFleetCheap = cheap;
        }

        foreach (Action<float> refresh in noonCards)
            refresh(width);
        if (noonSystems is not null)
            DrawSystems(noonSystems, width);

        // The totals written again only when one of them moved, to the cent
        var totals = (agentChats.Count, running, (long)Math.Round(cost * 100), mail);
        if (totals == noonSummaryDrawn)
            return;
        noonSummaryDrawn = totals;
        noonFleetSummary.Text = UIRichText.Literal(Said(agentChats.Count == 1 ? "noon.summary_one" : "noon.summary",
            ("agents", agentChats.Count.ToString()), ("running", running.ToString()),
            ("cost", cost.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)), ("mail", mail.ToString())));
    }

    /// <summary>
    /// The cards the fleet shows, top to bottom: every chat in the list's order, narrowed by what's typed in the fleet's
    /// filter. The fleet draws these and its keys go round them, so the next is always the card under this one.
    /// </summary>
    internal List<(HexAgentChat Chat, int Step)> FleetShown()
    {
        List<(HexAgentChat Chat, int Step)> order = FleetGrouped();
        string filter = noonFleetFilter?.Text.Trim() ?? string.Empty;
        return filter.Length == 0 ? order : order.FindAll(each => FleetFits(each.Chat, filter));
    }

    /// <summary>
    /// The groups of the fleet, those that need the person first, then those working, then the rest. Each family (a chat
    /// nobody started and the ones under it) stays together, in the group of its most urgent chat, and in the list's own
    /// order within its group.
    /// </summary>
    private enum FleetGroup { NeedsYou, Working, Rest }

    private static FleetGroup GroupOf(HexAgentChat chat) =>
        chat.HasPending || (chat.Failed && chat.Unread) || (chat.UnreadMailCount() > 0 && !chat.Running) ? FleetGroup.NeedsYou
        : chat.Running ? FleetGroup.Working
        : FleetGroup.Rest;

    // The group of each card of a list in the fleet's order, its family's, the family's first card being one nobody started
    private static FleetGroup[] FamilyGroups(List<(HexAgentChat Chat, int Step)> order)
    {
        var groups = new FleetGroup[order.Count];
        for (int start = 0; start < order.Count;)
        {
            int end = start + 1;
            while (end < order.Count && order[end].Step > 0)
                end++;
            FleetGroup group = FleetGroup.Rest;
            for (int i = start; i < end; i++)
                group = (FleetGroup)Math.Min((int)group, (int)GroupOf(order[i].Chat));
            for (int i = start; i < end; i++)
                groups[i] = group;
            start = end;
        }

        return groups;
    }

    // The list's order with its families sorted into their groups, stable within each
    private List<(HexAgentChat Chat, int Step)> FleetGrouped()
    {
        List<(HexAgentChat Chat, int Step)> order = ChatsInOrder();
        var families = new List<(FleetGroup Group, int At, List<(HexAgentChat Chat, int Step)> Chats)>();
        foreach (var each in order)
        {
            if (each.Step == 0 || families.Count == 0)
                families.Add((FleetGroup.Rest, families.Count, []));
            families[^1].Chats.Add(each);
        }
        for (int i = 0; i < families.Count; i++)
            families[i] = families[i] with { Group = families[i].Chats.Min(each => GroupOf(each.Chat)) };
        return [.. families.OrderBy(family => family.Group).ThenBy(family => family.At).SelectMany(family => family.Chats)];
    }

    // Whether a chat's name, agent system, number or model has what's typed in the fleet's filter
    private static bool FleetFits(HexAgentChat chat, string filter) =>
        chat.Shown.Contains(filter, StringComparison.OrdinalIgnoreCase) || chat.Agent.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || $"#{chat.Id}".Contains(filter, StringComparison.Ordinal) || (HexAgentModels.Of(chat) ?? string.Empty).Contains(filter, StringComparison.OrdinalIgnoreCase);

    // What the fleet's cards were last made from, see DrawNoonFleet
    private long noonFleetCheap = NOT_DRAWN;

    // The fleet's totals as they were last written
    private (int Agents, int Running, long Cents, int Mail) noonSummaryDrawn = (-1, -1, -1, -1);

    // What of the chat in Details waits on the person, as of which version of which chat
    private (int Chat, long Version, int Hash) noonWaitingOf = (-1, -1, 0);

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

            shape = hash.ToHashCode() | 1L << 40;
            version = chat.Version;
        }

        // What it's made of moving draws it at once. The chat only having gone on (every word it streams) draws it a second
        // later at the most, and not under a held button, made again between the press and the letting go every click was lost
        long now = System.Environment.TickCount64;
        bool moved = shape != noonContextDrawn.Shape
            || (version != noonContextDrawn.Version && now - noonContextDrawn.At >= CONTEXT_REDRAW_MS && !Engine.Input.Mouse.IsDown(Horizon.Input.MouseButton.Left));
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
                    Panel bar = row.Add(ContextBar(share, NOON_CONTEXT - noonContext.Padding.Total.X - 10 - COMPACT_BUTTON - 8));
                    bar.Fill = UIFill.None;
                    bar.Anchor = Origin.Left;
                    bar.Pivot = Origin.Left;
                    bar.Size = new Vector2(NOON_CONTEXT - noonContext.Padding.Total.X - 10 - COMPACT_BUTTON - 8, 6);
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
                    Style = "button_flat", Align = Origin.Left, Fill = UIFill.Horizontal, Tooltip = path,
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
        float width = across ?? NOON_CONTEXT - noonContext.Padding.Total.X - 10;
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
