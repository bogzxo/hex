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
    // How wide the fleet and the context are until they're dragged (DawnScene.Noon.Columns), and the width under which the
    // context gives its room to the chat
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
        // And under that, how much the agents' data takes on the disk, opening its folders and tidying it (DawnScene.AgentData)
        BuildAgentData();

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
        GripNoon();

        DressChrome();
        commands.Add("Agents/open", Said("noon.command"), () => SwitchFace(Face == HexFace.Noon ? HexFace.Dawn : HexFace.Noon), "Ctrl+Alt+Shift+N", "robot", UIKeyUse.BoundWhileTyping);
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
        compositor.TraceTag = face;
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
            // Its columns fitted and drawn on the first tick it's up, not up to a quarter of a second later
            noonWait = 0.0f;
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
        WarmFaces();
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
        var (sideFleet, sideContext) = NoonWidths(width, fleet && !over, context);
        float fleetWidth = over ? width : sideFleet;
        noonContextWide = sideContext > 0.0f ? sideContext : noonContextWide;
        noonView.Size = new Vector2(width, height);

        noonFleet.Size = new Vector2(fleetWidth, height);
        float systems = noonSystems is not null && SystemsTall(noonSystems) is > 0 and var tall ? tall + noonFleet.Spacing : 0.0f;
        noonFleetScroll.Size = new Vector2(fleetWidth - noonFleet.Padding.Total.X, MathF.Max(40, height - noonFleet.Padding.Total.Y - 52 - NOON_FILTER - noonFleet.Spacing - systems - 2 * (NOON_HARNESS + noonFleet.Spacing)));
        if (over)
            return;
        // The chat a column of a reading width down the middle of its card, the way a long line is hard to follow
        float middle = width - (fleet ? fleetWidth : 0.0f) - (context ? noonContextWide : 0.0f);
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
        noonContext.Size = new Vector2(noonContextWide, height);
        noonContextScroll.Size = new Vector2(noonContextWide - noonContext.Padding.Total.X, MathF.Max(40, height - noonContext.Padding.Total.Y - 28));
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
        // And the order's own key, which moves as a finished chat's few minutes run out with nothing else about it moving
        ChatsInOrder();
        cheap = (cheap * 31 + chatsOrderedFor) * 31 + (FinishedShown ? 1 : 0);
        cheap = cheap * 31 + LinkFleetKey();
        if (cheap != noonFleetCheap || noonFleetDrawn == NOT_DRAWN)
        {
            string filter = noonFleetFilter.Text.Trim();
            List<(HexAgentChat Chat, int Step)> order = FleetShown();
            // The order's own key for the bands (a finished chat's few minutes running out) and the finished group's
            long said = AgentCardsKey(order) * 31 + string.GetHashCode(filter, StringComparison.Ordinal);
            said = (said * 31 + chatsOrderedFor) * 31 + (FinishedShown ? 1 : 0) + chatsFinished.Count * 7L;
            said = said * 31 + LinkFleetKey();
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
                else if (order.Count == 0 && filter.Length > 0)
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
                // A heading over each band once there's more than one, a family's cards all under its most pressing chat's.
                // The finished group's head is a button that folds it, with the first finished card while it's open and a
                // row of its own under the rest while it's folded
                HexChatBand[] bands = FleetBands(order);
                // Headings over the working, waiting and recent only when there's more than one of them, a single group's
                // heading said nothing its cards didn't. The finished always have their head, it's how they're opened
                int main = 0;
                for (int i = 0; i < bands.Length; i++)
                    main |= bands[i] == HexChatBand.Finished ? 0 : 1 << (int)bands[i];
                bool headed = System.Numerics.BitOperations.PopCount((uint)main) > 1;
                bool any = main != 0;
                float depth = 0.0f;
                for (int i = 0; i < order.Count; i++)
                {
                    var (chat, step) = order[i];
                    bool finished = bands[i] == HexChatBand.Finished;
                    AgentCard card = AgentCardOf(chat, step, closable: false, quiet: finished && chat != agentChat, rail: bands[i] == HexChatBand.NeedsYou && NeedsYou(chat));
                    UIComponent inner = card.Root;
                    float above = 0.0f;
                    // The heading goes in with the band's first card, one row for each card still, which the keys and the
                    // clicks find by its place
                    if ((headed || finished) && (i == 0 || bands[i] != bands[i - 1]))
                    {
                        var with = new StackPanel { Fill = UIFill.Horizontal, Spacing = 0 };
                        if (finished)
                        {
                            with.Add(FinishedHead(alone: !any && filter.Length == 0, inset: 0.0f));
                            above = FinishedTall(!any && filter.Length == 0);
                        }
                        else
                        {
                            above = i == 0 ? 18 : 26;
                            with.Add(new Label
                            {
                                Text = Said(bands[i] switch { HexChatBand.NeedsYou => "noon.fleet_needs_you", HexChatBand.Working => "noon.fleet_working", _ => "noon.fleet_rest" }),
                                TextScale = 0.125f, Dim = true, Bold = true, Align = Origin.Left, Fill = UIFill.Horizontal, Size = new Vector2(0, above), Padding = new UIEdges(10, 0, 0, 2), Anchor = Origin.BottomLeft, Pivot = Origin.BottomLeft
                            });
                        }
                        with.Add(card.Root);
                        card = card with { Root = with };
                    }
                    noonFleetRows.Add(card.Root);
                    noonCards.Add(card.Refresh);
                    cardsPlaced.Add((chat.Id, inner, depth + above));
                    depth += above + AGENT_CARD + noonFleetRows.Spacing;
                }

                // The finished folded away, their head under the rest
                if (!FinishedShown && chatsFinished.Count > 0 && filter.Length == 0)
                    noonFleetRows.Add(FinishedHead(alone: order.Count == 0, inset: 0.0f));
                // Then a section for each linked device's agents (DawnScene.LinkFleet)
                LinkFleetSections(noonFleetRows, noonCards);
                SlideCards(noonFleetPlaces, cardsPlaced);
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
    /// The cards the fleet shows, top to bottom: the chats on screen in the list's order (ChatsShown, the finished left
    /// out while their group is folded), narrowed by what's typed in the fleet's filter, which looks through the finished
    /// too. The fleet draws these and its keys go round them, so the next is always the card under this one.
    /// </summary>
    internal List<(HexAgentChat Chat, int Step)> FleetShown()
    {
        List<(HexAgentChat Chat, int Step)> order = ChatsShown();
        string filter = noonFleetFilter?.Text.Trim() ?? string.Empty;
        return filter.Length == 0 ? order : order.FindAll(each => FleetFits(each.Chat, filter));
    }

    // The band of each card of a list in the fleet's order, its family's, as ChatsInOrder worked it out
    private HexChatBand[] FleetBands(List<(HexAgentChat Chat, int Step)> order)
    {
        List<(HexAgentChat Chat, int Step)> all = ChatsInOrder();
        var bands = new HexChatBand[order.Count];
        for (int i = 0, at = 0; i < order.Count; i++)
        {
            while (at < all.Count && all[at].Chat != order[i].Chat)
                at++;
            bands[i] = BandAt(at);
        }

        return bands;
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

}
