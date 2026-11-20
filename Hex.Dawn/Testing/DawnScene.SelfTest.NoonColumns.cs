using System.Numerics;

using Hex.Shell;

using Horizon.UI.Components;

namespace Hex.Dawn;

// Agentic's fleet and Details dragged by the lines between them and the chat: wider and narrower, held between their
// least and their most, the chat never under its least, kept in the settings, put back by a double click, the fleet put
// away when dragged on past its least, and a window too narrow for both taking from both in step.
internal sealed partial class DawnScene
{
    private void TestNoonColumns(HexPointerScript test)
    {
        HexAgentChat? chat = null;
        float sizeBefore = 1.0f, fleetLine = 0.0f, contextLine = 0.0f, middleBefore = 0.0f;
        bool hiddenBefore = false;

        Func<Vector2> InChrome(Func<Vector2> point) => () => chrome.ToWorld(point());
        float Middle() => noonCenter.Size.X;
        // Where each line is, read once before a drag, the layout follows the pointer and a target read as it moves runs away
        void ReadLines()
        {
            fleetLine = noonFleet.Bounds.Max.X;
            contextLine = noonContext.Bounds.Min.X;
            middleBefore = Middle();
        }

        float Down() => noonFleet.Bounds.Center.Y;

        test.Run(() =>
        {
            sizeBefore = compositor.Scale;
            hiddenBefore = noonFleetHidden;
            // 100% so the window is wide enough for the fleet, the chat and Details at once
            SetInterfaceSize(1.0f, remember: false);
            settings.NoonFleetWidth = settings.NoonContextWidth = 0.0f;
            noonFleetHidden = false;
            chat = new HexAgentChat(++agentChatsMade, "noon_agent") { Name = "ColumnsTag" };
            chat.Ask("columns");
            chat.Say("dragged about");
            chat.Done();
            agentChats.Add(chat);
            ShowFace(HexFace.Noon);
            OpenAgentChat(chat);
            noonWait = 0;
        });
        test.Settle(0.5f);
        test.Check("noon columns: the fleet and Details at Agentic's own widths, a handle on each line", () =>
            noonFleet.Visible && noonContext.Visible && Near(noonFleet.Size.X, NOON_FLEET) && Near(noonContext.Size.X, NOON_CONTEXT)
            && NoonSplitters is { Fleet: { Visible: true, Target: var fleetHeld }, Context: { Visible: true, Target: var contextHeld } }
            && fleetHeld == noonFleet && contextHeld == noonContext);
        test.Check("noon columns: and the columns hold on to no edge of their own, the handles are the only ones", () =>
            noonFleet.Resizable == UIResize.None && noonContext.Resizable == UIResize.None);

        test.Run(ReadLines);
        test.Hover(InChrome(() => new Vector2(fleetLine + 2, Down())));
        test.Check("noon columns: the fleet's line lights under the pointer, Details' doesn't", () =>
            NoonSplitters is { Fleet.Lit: true, Context.Lit: false });
        test.Run(() => Shot("noon-columns-hover"));
        test.Settle(0.2f);

        // The fleet wider by its line
        test.Drag(InChrome(() => new Vector2(fleetLine + 1, Down())), InChrome(() => new Vector2(fleetLine + 1 + 80, Down())));
        test.Wait();
        test.Check("noon columns: dragging the fleet's line makes it wider and the chat make do, kept in the settings", () =>
            Near(noonFleet.Size.X, NOON_FLEET + 80, 2) && Near(settings.NoonFleetWidth, noonFleet.Size.X, 1) && Middle() < middleBefore - 70
            && !noonFleet.IsResizing && NoonSplitters.Fleet is { Dragging: false });

        // Details wider by its line, which is on its left
        test.Run(ReadLines);
        test.Drag(InChrome(() => new Vector2(contextLine - 1, Down())), InChrome(() => new Vector2(contextLine - 1 - 60, Down())));
        test.Wait();
        test.Check("noon columns: dragging Details' line to the left makes Details wider, kept in the settings", () =>
            Near(noonContext.Size.X, NOON_CONTEXT + 60, 2) && Near(settings.NoonContextWidth, noonContext.Size.X, 1) && Middle() < middleBefore - 50);
        test.Settle(0.4f);
        test.Check("noon columns: Details' rows made again at its new width once it's let go of", () => Near(noonContextDrawnWide, noonContextWide));

        // Kept: what's written for them read back by settings of their own
        test.Check("noon columns: the widths are written to the settings' file and read back from it", () =>
        {
            string file = Path.Combine(Path.GetTempPath(), $"hex-noon-columns-{Environment.ProcessId}.hor");
            try
            {
                var written = new HexSettings(file) { NoonFleetWidth = settings.NoonFleetWidth, NoonContextWidth = settings.NoonContextWidth };
                if (written.Save() is not null)
                    return false;
                var read = new HexSettings(file);
                return read.NoonFleetWidth == settings.NoonFleetWidth && read.NoonContextWidth == settings.NoonContextWidth && read.NoonFleetWidth > 0;
            }
            finally
            {
                File.Delete(file);
            }
        });

        // Far past their most: the fleet stops at its most or where the chat would be under its least
        test.Run(ReadLines);
        test.Drag(InChrome(() => new Vector2(fleetLine + 1, Down())), InChrome(() => new Vector2(fleetLine + 1 + 900, Down())));
        test.Wait();
        test.Check("noon columns: the fleet dragged far stops at its most, the chat left its least", () =>
            noonFleet.Size.X <= MAX_NOON_FLEET + 0.5f && Middle() >= NOON_MIDDLE_LEAST - 1 && settings.NoonFleetWidth <= MAX_NOON_FLEET);
        test.Run(ReadLines);
        test.Drag(InChrome(() => new Vector2(contextLine - 1, Down())), InChrome(() => new Vector2(MathF.Min(contextLine - 1 + 400, noonView.Bounds.Max.X - 4), Down())));
        test.Wait();
        test.Check("noon columns: Details dragged far the other way stops at its least, it isn't put away", () =>
            Near(noonContext.Size.X, MIN_NOON_CONTEXT, 1) && noonContext.Visible && Near(settings.NoonContextWidth, MIN_NOON_CONTEXT, 1));
        test.Run(() => Shot("noon-columns-dragged"));
        test.Settle(0.2f);

        // A double click on each line puts it back
        test.Run(ReadLines);
        test.Click(InChrome(() => new Vector2(fleetLine + 1, Down())));
        test.Click(InChrome(() => new Vector2(fleetLine + 1, Down())));
        test.Wait();
        test.Check("noon columns: a double click on the fleet's line puts it back to Agentic's own width", () =>
            settings.NoonFleetWidth == 0.0f && Near(noonFleet.Size.X, NOON_FLEET) && noonFleet.Visible);
        test.Run(ReadLines);
        test.Click(InChrome(() => new Vector2(contextLine - 1, Down())));
        test.Click(InChrome(() => new Vector2(contextLine - 1, Down())));
        test.Wait();
        test.Check("noon columns: and on Details' line puts Details back", () => settings.NoonContextWidth == 0.0f && Near(noonContext.Size.X, NOON_CONTEXT));

        // The fleet dragged on past its least is put away, its width kept for when it comes back
        test.Run(ReadLines);
        test.Drag(InChrome(() => new Vector2(fleetLine + 1, Down())), InChrome(() => new Vector2(fleetLine + 1 - (NOON_FLEET - MIN_NOON_FLEET * 0.5f) - 20, Down())));
        test.Wait();
        test.Check("noon columns: the fleet dragged past its least is put away as it's let go of, the tabs offering it back", () =>
            !noonFleet.Visible && noonFleetHidden && noonFleetBack.Visible && NoonSplitters.Fleet is { Visible: false } && settings.NoonFleetWidth is 0.0f or >= MIN_NOON_FLEET);
        test.Run(() => ShowFleet(true));
        test.Wait();
        test.Check("noon columns: brought back it's as wide as it was kept", () =>
            noonFleet.Visible && Near(noonFleet.Size.X, PreferredNoon(settings.NoonFleetWidth, NOON_FLEET, MIN_NOON_FLEET, MAX_NOON_FLEET)));

        // A window too narrow for both at their widths: both give way in step, the chat keeps its least, and they come back
        test.Check("noon columns: on a narrower window both give way in step and the chat keeps its least", () =>
        {
            settings.NoonFleetWidth = settings.NoonContextWidth = 500.0f;
            var (fleet, context) = NoonWidths(NOON_CONTEXT_FROM + 20, true, true);
            var (wideFleet, wideContext) = NoonWidths(3000, true, true);
            settings.NoonFleetWidth = settings.NoonContextWidth = 0.0f;
            return fleet + context <= NOON_CONTEXT_FROM + 20 - NOON_MIDDLE_LEAST + 1 && fleet >= MIN_NOON_FLEET && context >= MIN_NOON_CONTEXT
                && Near(fleet - MIN_NOON_FLEET, (context - MIN_NOON_CONTEXT) * (500 - MIN_NOON_FLEET) / (500 - MIN_NOON_CONTEXT), 2)
                && wideFleet == 500 && wideContext == 500;
        });

        // Dawn has none of them
        test.Run(() => ShowFace(HexFace.Dawn));
        test.Wait();
        test.Check("noon columns: back in Dawn the handles of Agentic's lines are gone", () =>
            NoonSplitters is { Fleet.Visible: false, Context.Visible: false });

        // Dawn's sides are put back the same way, a double click on their lines
        test.Check("noon columns: a double click on a line of Dawn's sides puts that side back too", () =>
        {
            settings.LeftWidth = settings.RightWidth = 400.0f;
            Splitters.Left?.OnReset?.Invoke();
            Splitters.Right?.OnReset?.Invoke();
            return settings.LeftWidth == 0.0f && settings.RightWidth == 0.0f && Near(treeScroll.Size.X, PreferredSide(0.0f, usualLeft));
        });

        test.Run(() =>
        {
            agentChats.Remove(chat!);
            settings.NoonFleetWidth = settings.NoonContextWidth = 0.0f;
            noonFleetHidden = hiddenBefore;
            SetInterfaceSize(sizeBefore, remember: false);
            SaveSettings();
        });
        test.Wait();
    }
}
