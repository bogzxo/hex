using Hex.Shell;

namespace Hex.Dawn;

// Exercise the compact fleet with pointer selection, keyboard focus and transitions to and from the wide layout.
internal sealed partial class DawnScene
{
    private void TestNoonNarrowFleet(HexPointerScript test, Func<HexAgentChat> chatOf)
    {
        HexAgentChat? chat = null, picked = null;

        float scaleBefore = 1;
        void Narrow()
        {
            float pixels = chrome.Root.Bounds.Width * compositor.Scale;
            float scale = HexFrame.Scales.FirstOrDefault(value => pixels / value < NOON_FLEET_FROM - 20);
            SetScale(scale > 0 ? scale : HexFrame.Scales[^1], remember: false);
            // Even 200% can leave a large display in the wide layout.
            if (scale == 0)
                compositor.Scale = stage.Scale = pixels / 800.0f;
            noonWait = 0;
        }

        void Wide()
        {
            SetScale(scaleBefore, remember: false);
            noonWait = 0;
        }

        // The card of a chat in the fleet as it's drawn, worked out as the step runs
        System.Numerics.Vector2 CardAt(HexAgentChat of) => chrome.ToWorld(noonFleetRows.Children[FleetShown().FindIndex(each => each.Chat == of)].Bounds.Center);

        test.Run(() =>
        {
            chat = chatOf();
            picked = new HexAgentChat(++agentChatsMade, "noon_agent") { Name = "NarrowPickTag" };
            picked.Ask("picked from the narrow fleet");
            picked.Done();
            agentChats.Add(picked);
            OpenAgentChat(chat);
            ShowStage(NoonStage.Chat);
            noonFleetFilter.Text = string.Empty;
            scaleBefore = compositor.Scale;
            Narrow();
        });
        test.Settle(0.5f);
        test.Check("noon narrow: under the fleet's width it's away by itself, the chat's tabs offering it", () =>
            noonNarrow && !noonFleet.Visible && noonCenter.Visible && noonFleetBack.Visible && !noonFleetHidden);

        // The button, then the chat already open picked again
        test.Click(() => chrome.ToWorld(noonFleetBack.Bounds.Center));
        test.Settle(0.5f);
        test.Check("noon narrow: the show-fleet button opens the fleet in the chat's place, the whole width, its filter given the keys", () =>
            noonFleet.Visible && !noonCenter.Visible && noonFleet.Size.X >= noonView.Size.X - 1 && compositor.Focus == noonFleetFilter
            && noonFleetRows.Children.Count == FleetShown().Count && FleetShown().Any(each => each.Chat == picked));
        test.Run(() => Shot("noon-narrow-fleet"));
        test.Settle(0.3f);
        test.Click(() => CardAt(chat!));
        test.Settle(0.4f);
        test.Check("noon narrow: picking the chat already open gives the chat back too, with the box's keyboard", () =>
            agentChat == chat && !noonFleetOpened && !noonFleet.Visible && noonCenter.Visible && compositor.Focus == agentsInput);

        // Another chat picked
        test.Click(() => chrome.ToWorld(noonFleetBack.Bounds.Center));
        test.Settle(0.5f);
        // Under a group's heading at 200% it can be past the fleet's foot, where a person scrolls to it
        test.Run(() => noonFleetScroll.ScrollIntoView(noonFleetRows.Children[FleetShown().FindIndex(each => each.Chat == picked)]));
        test.Settle(0.4f);
        test.Click(() => CardAt(picked!));
        test.Settle(0.4f);
        test.Check("noon narrow: picking another chat gives the chat back, that one in it with the box's keyboard", () =>
            agentChat == picked && !noonFleet.Visible && noonCenter.Visible && noonFleetBack.Visible && compositor.Focus == agentsInput);

        // A fleet of one, narrowed by its filter, Alt+Page Down coming round to the same chat still gives the chat back
        PressKeys(test, "Ctrl+Alt+B");
        test.Settle(0.4f);
        test.Run(() =>
        {
            noonFleetFilter.Text = "NarrowPickTag";
            noonWait = 0;
        });
        test.Settle(0.4f);
        test.Check("noon narrow: the filter leaves a fleet of one", () => noonFleet.Visible && FleetShown().Count == 1 && noonFleetRows.Children.Count == 1);
        PressKeys(test, "Alt+PageDown");
        test.Settle(0.4f);
        test.Check("noon narrow: in a fleet of one Alt+Page Down, round to the chat already open, gives the chat back", () =>
            agentChat == picked && !noonFleet.Visible && noonCenter.Visible && compositor.Focus == agentsInput);
        test.Run(() =>
        {
            noonFleetFilter.Text = string.Empty;
            noonWait = 0;
        });

        PressKeys(test, "Ctrl+Alt+B");
        test.Settle(0.4f);
        test.Check("noon narrow: Ctrl+Alt+B opens the fleet the same way", () => noonFleet.Visible && !noonCenter.Visible);
        PressKeys(test, "Ctrl+Alt+B");
        test.Settle(0.4f);
        test.Check("noon narrow: and puts it away again, the chat as it was", () =>
            !noonFleet.Visible && noonCenter.Visible && agentChat == picked && !noonFleetHidden && compositor.Focus == agentsInput);

        // From the inbox the box isn't on screen, so it doesn't get the keys, put away or picked
        PressKeys(test, "Alt+2");
        test.Settle(0.3f);
        PressKeys(test, "Ctrl+Alt+B");
        test.Settle(0.4f);
        PressKeys(test, "Ctrl+Alt+B");
        test.Settle(0.4f);
        test.Check("noon narrow: put away over the inbox, the inbox is back and the hidden chat's box hasn't the keys", () =>
            noonStage == NoonStage.Inbox && noonInbox.Visible && noonCenter.Visible && !noonFleet.Visible
            && compositor.Focus != agentsInput && compositor.Focus != noonFleetFilter);
        PressKeys(test, "Ctrl+Alt+B");
        test.Settle(0.4f);
        test.Click(() => CardAt(chat!));
        test.Settle(0.4f);
        test.Check("noon narrow: picked over the inbox, the inbox is back and the hidden chat's box hasn't the keys", () =>
            agentChat == chat && noonStage == NoonStage.Inbox && noonInbox.Visible && noonCenter.Visible && !noonFleet.Visible
            && compositor.Focus != agentsInput && compositor.Focus != noonFleetFilter);
        PressKeys(test, "Alt+1");
        test.Settle(0.3f);

        // Resized while it's open: wide, the fleet beside the chat; narrow again, the chat, not the fleet
        PressKeys(test, "Ctrl+Alt+B");
        test.Settle(0.4f);
        test.Run(Wide);
        test.Settle(0.5f);
        test.Check("noon narrow: widened while it's open, the fleet is beside the chat as the wide window had it", () =>
            !noonNarrow && !noonFleetOpened && noonFleet.Visible && noonCenter.Visible && !noonFleetBack.Visible);
        test.Run(Narrow);
        test.Settle(0.5f);
        test.Check("noon narrow: narrowed again, the chat is on screen and the fleet away", () =>
            noonNarrow && !noonFleet.Visible && noonCenter.Visible && noonFleetBack.Visible);

        // Put away on the wide window, opened and shut on the narrow one, still away once it's wide again
        test.Run(Wide);
        test.Settle(0.5f);
        PressKeys(test, "Ctrl+Alt+B");
        test.Settle(0.3f);
        test.Run(Narrow);
        test.Settle(0.5f);
        PressKeys(test, "Ctrl+Alt+B");
        test.Settle(0.4f);
        test.Check("noon narrow: the fleet opens on a narrow window though the wide one put it away", () => noonFleet.Visible && !noonCenter.Visible && noonFleetHidden);
        PressKeys(test, "Ctrl+Alt+B");
        test.Settle(0.3f);
        test.Run(Wide);
        test.Settle(0.5f);
        test.Check("noon narrow: widened, the fleet the wide window put away stays away", () => !noonFleet.Visible && noonFleetBack.Visible && noonCenter.Visible);
        PressKeys(test, "Ctrl+Alt+B");
        test.Settle(0.3f);
        test.Check("noon narrow: and Ctrl+Alt+B brings it back on the wide window", () => noonFleet.Visible && !noonFleetBack.Visible);

        test.Run(() =>
        {
            Wide();
            noonFleetFilter.Text = string.Empty;
            if (picked is not null)
                agentChats.Remove(picked);
            if (chat is not null)
                OpenAgentChat(chat);
            ShowStage(NoonStage.Chat);
            noonWait = 0;
        });
        test.Settle(0.3f);
    }
}
