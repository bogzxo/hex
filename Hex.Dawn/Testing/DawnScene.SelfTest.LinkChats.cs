using System.Diagnostics;
using System.Globalization;
using System.Numerics;

using Horizon.Input;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestLinkChats(HexPointerScript test)
    {
        const string deskId = "link-chats-desk", laptopId = "link-chats-laptop";
        const string longDesk = "Desk with an intentionally long device name for the narrow sidebar";
        const string longChat = "A remote chat title long enough to need an ellipsis in the Agents panel";
        const string refusal = "#2 wasn't started by this device. It needs Manage agents.";
        const HexLinkRights rights = HexLinkRights.View | HexLinkRights.Steer;
        string deskRun = HexLinkChatGroups.SectionKey(deskId, HexLinkChatSection.Running);
        string deskDone = HexLinkChatGroups.SectionKey(deskId, HexLinkChatSection.Finished);
        string laptopDone = HexLinkChatGroups.SectionKey(laptopId, HexLinkChatSection.Finished);
        string beforeFolds = settings.AgentsListFolds;
        string beforeSkin = settings.EditorSkin, beforeTheme = settings.EditorTheme;
        float beforeWidth = settings.LeftWidth;
        bool beforeOpen = agentsListOpen;
        bool leftFolded = false, rightOpened = false, downReachedRow = false, upReachedHead = false;
        bool narrowFits = false, darkContrast = false, lightContrast = false;
        var seenAt = new DateTime(2026, 11, 21, 14, 30, 0, DateTimeKind.Utc);

        HexLinkRow Row(int id, string name, string state, bool waiting = false) =>
            new(id, name, "codex", "gpt-6.2-sol", null, 0, state, state == "done" ? "Complete" : string.Empty,
                42, 0, 1, waiting, null);
        List<HexLinkRow> deskRows = [Row(1, "Review build", "running"), Row(2, longChat, "running", waiting: true) with { StartedByYou = true }];
        deskRows.AddRange(Enumerable.Range(3, 7).Select(id => Row(id, $"Finished chat {id}", "done")));
        List<HexLinkRow> laptopRows = [Row(1, "Wait for the user", "running", waiting: true)];
        laptopRows.AddRange(Enumerable.Range(2, 3).Select(id => Row(id, $"Finished chat {id}", "done")));
        var desk = new LinkChatDevice(deskId, "Desk", true, rights, deskRows, 1, seenAt, "LAN", "Direct over the local network.")
        {
            Act = (_, _, _) => Task.FromResult((false, refusal))
        };
        var laptop = new LinkChatDevice(laptopId, "Laptop", false, rights, laptopRows, 1, seenAt);

        ChatListHeadButton? Head(string key) =>
            Descendants(agentsListRows).OfType<ChatListHeadButton>().FirstOrDefault(button => button.Key == key);
        AgentCardButton? Card(string device, int id) =>
            Descendants(agentsListRows).OfType<AgentCardButton>()
                .FirstOrDefault(card => LinkRemoteOf(card.Chat) is { } remote && remote.DeviceId == device && remote.Id == id);
        IEnumerable<Label> Labels(string key) => Head(key)?.Parent?.Children.OfType<Label>() ?? [];
        bool BadgeSays(string key, string text) => Head(key)?.Parent?.Children.OfType<Badge>().Any(badge => badge.Visible && badge.Text == text) == true;
        Vector2 Centre(UIComponent? target) => target is null ? Vector2.Zero : chrome.ToWorld(target.Bounds.Center);
        void Press(Key key) => UIKeyboard.Press(key, false, false, false, false);
        void Redraw()
        {
            agentsListDrawn = noonFleetDrawn = NOT_DRAWN;
            DrawAgentsList();
        }
        void Resize(int width) => test.Run(() =>
        {
            settings.LeftWidth = width;
            agentsListScroll.ScrollTo(0);
            Engine.WindowManager.RequestFrames(0.5);
        });
        void Capture(string file)
        {
            string? folder = System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS");
            if (string.IsNullOrEmpty(folder))
                folder = System.Environment.GetEnvironmentVariable("DAWN_SELFTEST_SHOTS");
            if (string.IsNullOrEmpty(folder))
                return;

            string path = Path.Combine(folder, file + ".png");
            DateTime previous = default;
            test.Run(() =>
            {
                previous = File.GetLastWriteTimeUtc(path);
                Shot(file);
                Engine.WindowManager.RequestFrames(0.5);
            });
            test.WaitFor(() => ShotWritten(file) && File.GetLastWriteTimeUtc(path) > previous, 5);
        }

        test.Run(() =>
        {
            StopLink();
            string file = Path.Combine(testFolder, "link-chats.txt");
            File.WriteAllText(file, "Linked chats self-test fixture.\n");
            Open(file);
            ShowFace(HexFace.Dawn);
            ShowAgentsPanel();
            UseEditorTheme("horizon", save: false);
            settings.LeftWidth = 360;
            agentsListOpen = true;
            settings.AgentsListFolds = string.Empty;
            linkChatFolds = null;
            linkChatUsualSeen.Clear();
            linkChatLastRefusal = null;
            linkChatFakes = [desk, laptop];
            Redraw();
        });
        test.WaitFor(() => Head(deskId) is { IsShownInHierarchy: true }, 4);

        test.Check("link-chats: device headers show activity counts, route and needs-you", () =>
            Labels(deskId).Any(label => label.Text.Contains("Desk", StringComparison.Ordinal))
            && Labels(laptopId).Any(label => label.Text.Contains("Laptop", StringComparison.Ordinal))
            && Labels(deskId).Any(label => label.Text == "2 running · 7 finished")
            && BadgeSays(deskId, "Connected") && BadgeSays(deskId, "LAN") && BadgeSays(laptopId, "1 need you"));
        test.Check("link-chats: seven finished fold by default and three finished stay open", () =>
            Head(deskDone) is { Open: false } && Card(deskId, 3) is null
            && Head(laptopDone) is { Open: true } && Card(laptopId, 2) is not null);

        test.Click(() => Centre(Head(deskId)));
        test.WaitFor(() => Head(deskId) is { Open: false }, 3);
        bool wasFolded = false;
        test.Run(() =>
        {
            wasFolded = Head(deskId) is { Open: false } && Head(deskRun) is null;
            linkChatFolds = null;
            linkChatUsualSeen.Clear();
            Redraw();
        });
        test.Check("link-chats: device fold survives reloading settings and keeps counts and needs-you", () =>
            wasFolded && Head(deskId) is { Open: false } && Card(deskId, 2) is null
            && settings.AgentsListFolds.Contains(deskId + "=0", StringComparison.Ordinal)
            && Labels(deskId).Any(label => label.Text == "2 running · 7 finished") && BadgeSays(deskId, "1 need you"));

        test.Run(() =>
        {
            var moved = new List<HexLinkRow>(deskRows);
            moved[0] = moved[0] with { Approvals = [new HexAgentApprovalStatus(1, "Run the build", "A build approval.")], Version = 2 };
            desk = desk with { Rows = moved, Version = 2 };
            linkChatFakes![0] = desk;
            DrawAgentsList();
        });
        test.Check("link-chats: an approval updates a folded group without opening it", () =>
            Head(deskId) is { Open: false } && Card(deskId, 1) is null && BadgeSays(deskId, "2 need you"));

        test.Click(() => Centre(Head(deskId)));
        test.WaitFor(() => Head(deskRun) is { Open: true }, 3);
        test.Click(() => Centre(Head(deskRun)));
        test.WaitFor(() => Head(deskRun) is { Open: false }, 3);
        bool sectionFolded = false;
        test.Run(() => sectionFolded = Head(deskId) is { Open: true } && Card(deskId, 2) is null && Head(deskDone) is { Open: false });
        test.Click(() => Centre(Head(deskRun)));
        test.WaitFor(() => Card(deskId, 2) is not null, 3);
        test.Check("link-chats: Running folds and opens independently", () =>
            sectionFolded && Head(deskRun) is { Open: true } && Card(deskId, 2) is not null);

        test.Run(() =>
        {
            Head(deskId)?.Focus();
            Press(Key.Left);
        });
        test.WaitFor(() => Head(deskId) is { Open: false, IsFocused: true }, 3);
        test.Run(() =>
        {
            leftFolded = Head(deskId) is { Open: false, IsFocused: true };
            Press(Key.Right);
        });
        test.WaitFor(() => Head(deskId) is { Open: true, IsFocused: true }, 3);
        test.Run(() =>
        {
            rightOpened = Head(deskId) is { Open: true, IsFocused: true };
            Press(Key.Down);
        });
        test.WaitFor(() => Head(deskRun) is { IsFocused: true }, 3);
        test.Run(() => Press(Key.Down));
        test.WaitFor(() => Card(deskId, 2) is { IsFocused: true }, 3);
        test.Run(() => { downReachedRow = Card(deskId, 2) is { IsFocused: true }; Press(Key.Up); });
        test.WaitFor(() => Head(deskRun) is { IsFocused: true }, 3);
        test.Run(() => { upReachedHead = Head(deskRun) is { IsFocused: true }; Press(Key.Down); });
        test.WaitFor(() => Card(deskId, 2) is { IsFocused: true }, 3);
        test.Run(() => Press(Key.Enter));
        test.WaitFor(() => LinkRemoteOf(agentChat) is { DeviceId: deskId, Id: 2 }, 3);
        test.Check("link-chats: Left, Right, Down, Up and Enter fold, navigate and open the remote chat", () =>
        {
            bool open = LinkRemoteOf(agentChat) is { DeviceId: deskId, Id: 2 };
            if (!leftFolded || !rightOpened || !downReachedRow || !upReachedHead || !open)
                Console.WriteLine($"[Hex self-test] link-chats: keyboard left {leftFolded}, right {rightOpened}, down {downReachedRow}, up {upReachedHead}, enter {open}, focus {compositor.Focus?.GetType().Name}.");
            return leftFolded && rightOpened && downReachedRow && upReachedHead && open;
        });
        test.Run(() =>
        {
            if (Head(deskId) is { Open: false })
            {
                SetLinkChatOpen(deskId, true);
                Redraw();
            }
        });

        test.Check("link-chats: offline cards are dim and their menu explains the disabled actions", () =>
        {
            Menu offline = LinkRemoteMenu(new LinkRemoteCard(laptopId, laptop.Name, 1));
            return BadgeSays(laptopId, "Offline")
                && Labels(laptopId).Any(label => label.Text.StartsWith("Offline: last seen", StringComparison.Ordinal))
                && Card(laptopId, 1) is { Title.Dim: true } pick && pick.Tooltip.Contains("is offline", StringComparison.Ordinal)
                && offline.Items.First().Label.Contains("is offline", StringComparison.Ordinal)
                && offline.Items.Where(item => !item.IsSeparator).All(item => item.IsEnabled?.Invoke() == false);
        });

        string[] menuKeys = ["agents_panel.chat_open", "link.chat_rename", "link.chat_stop", "link.chat_restart", "link.chat_close", "link.chat_delete"];
        bool menuPresent = false;
        test.Run(() =>
        {
            Menu online = LinkRemoteMenu(new LinkRemoteCard(deskId, desk.Name, 2));
            menuPresent = online.Items.Where(item => !item.IsSeparator).Select(item => item.Label).SequenceEqual(menuKeys.Select(key => Said(key)));
            online.Items.First(item => item.Label == Said("link.chat_stop")).OnPicked?.Invoke();
        });
        test.WaitFor(() => linkChatLastRefusal == refusal, 3);
        test.Check("link-chats: six row actions are present and refusal displays the device message", () =>
            menuPresent && linkChatLastRefusal == refusal && messageItem.Text.Contains("Manage agents.", StringComparison.Ordinal));

        test.Check("link-chats: remote cards display their own ID and the started-here mark", () =>
        {
            Label[] words = Card(deskId, 2)?.Parent?.Children.OfType<Label>().ToArray() ?? [];
            return words.Length > 0 && words[0].Text.StartsWith("#2 ·", StringComparison.Ordinal)
                && words.Any(label => label.Text.Contains("started here", StringComparison.Ordinal));
        });

        test.Run(() =>
        {
            CloseRemoteMirror();
            ShowDraftOf(null);
            Say(string.Empty);
            desk = desk with { Name = longDesk, Version = desk.Version + 1 };
            linkChatFakes![0] = desk;
            agentsListScroll.ScrollTo(0);
        });

        bool Fits(int expectedWidth)
        {
            var parents = new List<UIComponent>();
            foreach (var pick in Descendants(agentsListRows).OfType<Button>())
            {
                if ((pick is ChatListHeadButton || pick is AgentCardButton card && LinkRemoteOf(card.Chat) is not null)
                    && pick.IsShownInHierarchy && pick.Parent is { } parent)
                    parents.Add(parent);
            }

            bool good = MathF.Abs(agentsView.Bounds.Width - expectedWidth) < 1.0f && parents.Count >= 5;
            foreach (UIComponent parent in parents)
            {
                UIComponent[] words = parent.Children.Where(child => child is Label or Badge && child.Visible).ToArray();
                for (int i = 0; i < words.Length; i++)
                for (int j = i + 1; j < words.Length; j++)
                {
                    var overlap = words[i].Bounds.Intersect(words[j].Bounds);
                    if (overlap.Width <= 0.5f || overlap.Height <= 0.5f)
                        continue;
                    Console.WriteLine($"[Hex self-test] link-chats: at {expectedWidth}px, {Word(words[i])} overlaps {Word(words[j])} by {overlap.Width:F1} × {overlap.Height:F1}px.");
                    good = false;
                }
            }
            bool titleFits = Labels(deskId).Any(label => label.Text.EndsWith("...", StringComparison.Ordinal) && label.Tooltip.Contains(longDesk, StringComparison.Ordinal))
                && Card(deskId, 2) is { Title: { } title } && title.Text.EndsWith("...", StringComparison.Ordinal) && title.Tooltip.Contains(longChat, StringComparison.Ordinal);
            if (!good || !titleFits)
                Console.WriteLine($"[Hex self-test] link-chats: wanted {expectedWidth}px, panel is {agentsView.Bounds.Width:F1}px, checking {parents.Count} rows. Ellipsis with tooltip: {titleFits}.");
            return good && titleFits;
        }

        static string Word(UIComponent component) => component is Label label ? label.Text : ((Badge)component).Text;

        bool Contrast()
        {
            if (compositor.Skin is not { } skin)
                return false;
            Vector4 panel = Over(skin.Palette.Panel, new Vector4(0, 0, 0, 1));
            UIComponent[] words = Descendants(agentsListRows)
                .Where(component => (component is Badge or Label) && component.IsShownInHierarchy)
                .ToArray();
            float min = words.Length == 0 ? 0 : words.Min(component =>
            {
                if (component is Badge chip)
                {
                    Vector4 colour = chip.ColourIn(skin);
                    return UIPalette.Contrast(colour, Over(colour with { W = colour.W * 0.16f }, panel));
                }
                var label = (Label)component;
                Vector4 ink = label.Color ?? (label.Dim ? skin.DimTextColor : skin.TextColor);
                return UIPalette.Contrast(ink, panel);
            });
            Console.WriteLine($"[Hex self-test] link-chats: {skin.Theme} list text and chip contrast {min.ToString("0.00", CultureInfo.InvariantCulture)}:1.");
            return min >= 4.5f;
        }

        test.Hover(() => chrome.ToWorld(code.Bounds.Center));
        Resize(360);
        test.Settle(0.35f);
        test.Run(() => { narrowFits = Fits(360); darkContrast = Skin?.Palette.LightField == false && Contrast(); });
        Capture("link_chats_dark_wide");
        Resize(280);
        test.Settle(0.35f);
        test.Run(() => narrowFits = Fits(280) && narrowFits);
        Capture("link_chats_dark_narrow");

        test.Run(() => UseEditorTheme("light", save: false));
        test.WaitFor(() => compositor.Skin?.Palette.LightField == true, 5);
        test.Settle(0.25f);
        test.Run(() => { lightContrast = Contrast(); narrowFits = Fits(280) && narrowFits; });
        Capture("link_chats_light_narrow");
        Resize(360);
        test.Settle(0.35f);
        test.Run(() => { lightContrast = Contrast() && lightContrast; narrowFits = Fits(360) && narrowFits; });
        Capture("link_chats_light_wide");
        test.Check("link-chats: 280px and 360px heads and rows fit with full name tooltips", () => narrowFits);
        test.Check("link-chats: list text and chips contrast at least 4.5:1 in both themes", () => darkContrast && lightContrast);

        TestLinkChatFrames(test, Row);
        test.Run(() =>
        {
            CloseRemoteMirror();
            ShowDraftOf(agentChats.LastOrDefault());
            foreach (LinkChatDevice fake in linkChatFakes ?? [])
                RemoveLinkStandIns(fake.Id);
            RemoveLinkStandIns(deskId);
            RemoveLinkStandIns(laptopId);
            linkChatFakes = null;
            linkRowStates.Clear();
            linkFailedUnseen.Clear();
            linkChatUsualSeen.Clear();
            linkChatFolds = null;
            settings.AgentsListFolds = beforeFolds;
            settings.LeftWidth = beforeWidth;
            SaveSettings();
            agentsListOpen = beforeOpen;
            UseEditorSkin(beforeSkin, beforeTheme, save: false);
            Redraw();
        });
    }

    private void TestLinkChatFrames(HexPointerScript test, Func<int, string, string, bool, HexLinkRow> makeRow)
    {
        const int samples = 24;
        var rebuild = new List<double>();
        var incremental = new List<double>();
        var gaps = new List<double>();
        var incrementalGaps = new List<double>();
        var measured = new List<LinkChatGroupView>();
        bool reused = true;

        test.Run(() =>
        {
            CloseRemoteMirror();
            ShowDraftOf(null);
            linkChatFakes = [.. Enumerable.Range(1, 3).Select(device =>
                new LinkChatDevice($"link-chat-perf-{device}", $"Worker {device}", true, HexLinkRights.View,
                    [.. Enumerable.Range(1, 100).Select(row => makeRow(row, $"Job {row}", "running", false))], 1, default))];
            DrawAgentsList();
            Engine.WindowManager.RequestFrames(10);
        });
        test.Settle(0.3f);

        void Phase(List<double> draws, List<double> frameGaps, bool reuse)
        {
            test.Run(() =>
            {
                perfFrames = [];
                measured.Clear();
                measured.AddRange(agentsLinkChatGroups.Values);
            });
            for (int i = 0; i < samples; i++)
            {
                test.Run(() =>
                {
                    LinkChatDevice changed = linkChatFakes![0];
                    linkChatFakes[0] = changed with { Version = changed.Version + 1 };
                    if (!reuse)
                        agentsLinkChatGroups.Clear();
                    long start = Stopwatch.GetTimestamp();
                    DrawAgentsList();
                    draws.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    if (reuse)
                        reused &= measured.Where(view => view.State.Device.Id != changed.Id).All(view =>
                            agentsLinkChatGroups.TryGetValue(view.State.Device.Id, out var retained) && ReferenceEquals(retained, view));
                });
            }
            test.Run(() =>
            {
                frameGaps.AddRange(perfFrames!.Select(gap => (double)gap * 1000.0));
                perfFrames = null;
            });
        }

        Phase(rebuild, gaps, reuse: false);
        Phase(incremental, incrementalGaps, reuse: true);
        test.Check("link-chats: 300 chat timing samples reuse the other two device groups", () =>
        {
            double Median(List<double> ticks) => ticks.Count > 0 ? ticks.Order().ElementAt(ticks.Count / 2) : 0;
            Console.WriteLine(FormattableString.Invariant(
                $"[Hex self-test] link-chats: 3 × 100 rows, {samples} changes per phase, median DrawAgentsList rebuild {Median(rebuild):F2} ms, cached {Median(incremental):F2} ms, median frame gap {Median(gaps):F2} ms and {Median(incrementalGaps):F2} ms."));
            return rebuild.Count == samples && incremental.Count == samples && gaps.Count > 0 && incrementalGaps.Count > 0
                && reused && agentsLinkChatGroups.Count == 3
                && agentsLinkChatGroups.Values.All(group => group.State.Device.Rows.Count == 100);
        });
    }
}
