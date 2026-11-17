using System.Numerics;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestAgentLayout(HexPointerScript test)
    {
        float width = 0;
        Vector2 grip = default;
        HexAgentProfile[] profiles = [];
        HexExtension? provider = null;
        HexAgentEntry? tool = null;
        float animation = settings.UiAnimation;
        void Capture(string name)
        {
            test.Run(() => { Shot(name); Engine.WindowManager.RequestFrames(0.2); Engine.WindowManager.Wake(); });
            test.WaitFor(() => ShotWritten(name), 8);
        }
        test.Run(() =>
        {
            string file = Path.Combine(testFolder, "agents-layout.txt");
            File.WriteAllText(file, "Agent layout fixture\n");
            Open(file);
            ShowAgentsPanel();
        });
        test.Settle(0.3f);
        test.Run(() =>
        {
            width = agentsView.Bounds.Width;
            // The side is dragged by its splitter on the line between it and the middle, from the half over the middle
            grip = chrome.ToWorld(new Vector2(left.Bounds.Max.X + 2, agentsView.Bounds.Center.Y));
        });
        test.Drag(() => grip, () => grip + new Vector2(80, 0));
        test.Settle(0.2f);
        test.Check("agents layout: visible chat sidebar resizes and keeps the shared sidebar width", () =>
            agentsView.Bounds.Width > width + 30 && MathF.Abs(agentsView.Size.X - treeScroll.Size.X) < 1);
        test.Run(() =>
        {
            profiles = [.. HexAgentProfiles.All];
            foreach (var profile in profiles) HexAgentProfiles.Forget(profile.Extension);
            agentsInput.Text = "Explain the settings loader";
        });
        test.Settle(0.3f);
        test.Check("agents layout: a draft without a provider offers setup and cannot send", () =>
            agentsInput.Enabled && !agentsSend.Enabled
            && Descendants(agentsRows).OfType<Button>().Any(button => button.Label == Said("agents_panel.browse")));
        Capture("agents-setup");
        TestAgentWelcomeResize(test);
        test.Click(() => chrome.ToWorld(Descendants(agentsRows).OfType<Button>().First().Bounds.Center));
        test.Settle(0.3f);
        test.Check("agents layout: setup opens available extensions", () => SettingsPage?.CurrentGroup == HexExtensions.AVAILABLE);
        test.Run(CloseSettings);
        test.Settle(0.3f);
        test.Run(() =>
        {
            string folder = Path.Combine(testFolder, "layout_agent");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, HexExtension.FILE), """
                let extension = { name: "Layout fixture", settings: {
                    effort: { title: "Thinking level", kind: "choice", choices: ["", "low", "high"], default: "" }
                } };
                func on_start(hex) {
                    hex.agent({ id: "layout_agent", name: "Workspace assistant", run: { program: hex.executable,
                        options: [{ id: "model", title: "Model", choices: [{ value: "default", label: "Default" }] },
                            { id: "effort", title: "Thinking level", choices: [{ value: "", label: "Auto" }, { value: "low", label: "Low" }, { value: "high", label: "High" }] }] } });
                }
                """);
            provider = new HexExtension(folder, builtIn: false) { Enabled = true };
            extensions.Add(provider);
            extensions.Start(provider);
            settings.LeftWidth = MIN_SIDE;
            ApplyPanelSizes();
        });
        test.Settle(0.3f);
        test.Check("agents layout: installing a provider enables the existing draft", () =>
            agentsSend.Enabled && agentsInput.Text == "Explain the settings loader");
        test.Check("agents layout: thinking level is directly available in the composer", () => agentsThinking is { Enabled: true });
        test.Run(() => agentsThinking!.OnPressed!());
        test.Wait();
        PressKeys(test, "End");
        PressKeys(test, "Enter");
        test.Settle(0.2f);
        test.Check("agents layout: thinking selection persists in the provider setting and updates its picker", () =>
            agentsThinking?.Tooltip.Contains("High", StringComparison.Ordinal) == true
            && Horizon.HIDL.Runtime.Values.Text(extensions.SettingOf(provider!, "effort")) == "high");
        test.Run(() => RequestAgent("explain"));
        test.Settle(0.3f);
        test.Check("agents layout: actions fit the minimum sidebar", () =>
            MathF.Abs(treeScroll.Size.X - MIN_SIDE) < 1
            && agentsChips.Bounds.Min.Y >= agentsHistory.Bounds.Max.Y
            && agentsHistory.Bounds.Max.X <= agentsNew.Bounds.Min.X
            && agentsOptions.Bounds.Max.X <= agentsSend.Bounds.Min.X
            && agentsSend.Bounds.Max.X <= agentsView.Bounds.Max.X + 1);
        test.Check("agents layout: minimum width identifies the selected provider without abbreviating it", () =>
        {
            Button selector = agentsChips.Children.OfType<Button>().Single();
            return selector.Label == "Workspace assistant $(chevron-down)"
                && selector.Bounds.Min.X >= agentsView.Bounds.Min.X
                && selector.Bounds.Max.X <= agentsView.Bounds.Max.X + 1
                && agentsMeta.Bounds.Max.X <= agentsHistory.Bounds.Min.X
                && agentsScroll.Bounds.Max.Y < agentsHistory.Bounds.Min.Y;
        });
        test.Check("agents layout: welcome helper stays readable and the composer stays rounded", () =>
            agentsRows.Children.OfType<Label>().Skip(1).First() is { Wrap: true, TextScale: >= 0.12f }
            && agentsCompose.Radius > 0);
        Capture("agents-narrow");
        float originalScale = compositor.Scale;
        test.Run(() => { settings.LeftWidth = 320; ApplyPanelSizes(); SetScale(2, remember: false); });
        test.Settle(0.3f);
        test.Check("agents layout: provider and actions fit the rendered sidebar after scaling", () =>
            agentsChips.Bounds.Max.X <= agentsView.Bounds.Max.X + 1 && agentsNew.Bounds.Max.X <= agentsView.Bounds.Max.X + 1
            && agentsHistory.Bounds.Min.X >= agentsView.Bounds.Min.X && agentsOptions.Bounds.Max.X <= agentsSend.Bounds.Min.X);
        test.Click(() => chrome.ToWorld(agentsNew.Bounds.Center));
        test.Wait();
        test.Check("agents layout: the compact New button clears the draft and focuses the composer", () =>
            agentsInput.Text.Length == 0 && compositor.Focus == agentsInput);
        test.Run(() => SetScale(originalScale, remember: false));
        TestAgentWelcomeResize(test);
        test.Run(() =>
        {
            settings.LeftWidth = width;
            ApplyPanelSizes();
            NewAgentChat();
        });
        test.Settle(0.3f);
        Capture("agents-welcome");
        test.Run(() =>
        {
            settings.UiAnimation = 2;
            agentChat = new HexAgentChat(++agentChatsMade, "layout_agent");
            agentChats.Add(agentChat);
            agentChat.Ask("Where does Hex load workspace settings?");
            tool = agentChat.Tool("1", "mcp__hex__hex_search", """{"query":"LoadSettings"}""");
        });
        test.Settle(0.3f);
        test.Check("agents layout: running tools use the configured animation speed", () =>
            agentsStop.Visible && AgentsMotion(0.4f) == 0.2f
            && Descendants(agentsRows).OfType<Button>().Any(button => button.Icon == "loading~spin"));
        test.Run(() => settings.UiAnimation = 0);
        test.Settle(0.3f);
        test.Check("agents layout: disabling motion updates the tool already on screen", () =>
            AgentsMotion(0.4f) == 0 && Descendants(agentsRows).OfType<Button>().Any(button => button.Icon == "loading"));
        test.Run(() =>
        {
            agentChat!.ToolDone("1", true, "Found the settings loader in Hex.Shell.");
            agentChat.Say("Hex reads the workspace settings when you open the folder.");
            agentChat.Done();
        });
        test.Settle(0.4f);
        // A call alone is a slim line of its own, pressed it opens to what it was given and said back
        test.Click(() => chrome.ToWorld(Descendants(agentsRows).OfType<Button>()
            .First(button => button.Tooltip?.StartsWith("mcp__hex__hex_search", StringComparison.Ordinal) == true).Bounds.Center));
        test.Settle(0.3f);
        test.Check("agents layout: opening a tool exposes its result", () =>
            tool?.Open == true && Descendants(agentsRows).OfType<Label>().Any(label => label.Text.Contains("Found the settings loader")));
        Capture("agents-transcript");

        // A long chat dragged wider is fitted to the width as it goes and not made again. Made again on every step it
        // stuttered, and made again only once the drag stopped it jumped
        UIComponent[] rowsBefore = [];
        float bubbleBefore = 0;
        Vector2 splitter = default;
        Panel Bubble() => Descendants(agentsRows).OfType<Panel>().First(panel => panel.Radius == 10 && panel.Anchor == Horizon.Rendering.Origin.Right);
        test.Run(() =>
        {
            settings.LeftWidth = 300;
            ApplyPanelSizes();
            for (int round = 0; round < 30; round++)
            {
                agentChat!.Ask($"Question {round}, long enough that its bubble is as wide as the side lets it be and then a little more besides.");
                agentChat.Tool($"t{round}", "mcp__hex__hex_search", $$"""{"query":"LoadSettings {{round}} and some more of what it was asked for"}""");
                agentChat.ToolDone($"t{round}", true, "Found it.");
                agentChat.Say($"Answer {round}, **some markdown** and a line long enough to wrap in a narrow side bar once or twice over.");
            }
            agentChat!.Done();
        });
        test.Settle(0.5f);
        test.Run(() =>
        {
            rowsBefore = [.. agentsRows.Children];
            bubbleBefore = Bubble().Size.X;
            splitter = chrome.ToWorld(new Vector2(left.Bounds.Max.X + 2, agentsView.Bounds.Center.Y));
        });
        test.Drag(() => splitter, () => splitter + new Vector2(140, 0));
        test.Settle(0.1f);
        test.Check("agents layout: a long chat dragged wider keeps its rows and fits them to the new width", () =>
            agentsRows.Children.Count == rowsBefore.Length && agentsRows.Children.Zip(rowsBefore).All(pair => ReferenceEquals(pair.First, pair.Second))
            && Bubble().Size.X > bubbleBefore + 60 && Bubble().Bounds.Max.X <= agentsView.Bounds.Max.X + 1);

        // Calls one after another are one row folded, its words said, and opened it stays where it was on screen
        float groupTop = 0;
        Button? groupHead = null;
        test.Run(() =>
        {
            agentChat!.Ask("Two calls in a row");
            agentChat.Tool("g1", "mcp__hex__hex_read", """{"path":"a.cs"}""");
            agentChat.ToolDone("g1", true, "a");
            agentChat.Tool("g2", "mcp__hex__hex_search", """{"query":"b"}""");
            agentChat.ToolDone("g2", true, "b");
            agentChat.Say("Done with both.");
            agentChat.Done();
        });
        test.Settle(0.4f);
        test.Run(() =>
        {
            groupHead = Descendants(agentsRows).OfType<Button>().LastOrDefault(button => button.Label.Contains("2 tool calls", StringComparison.Ordinal));
            groupTop = groupHead?.Bounds.Max.Y ?? 0;
            groupHead?.OnPressed!();
        });
        test.Settle(0.3f);
        test.Check("agents layout: calls one after another are one folded row with its words, and open where they are", () =>
            groupHead is not null && toolGroupsOpen.Count > 0
            && !Descendants(agentsRows).OfType<Button>().Any(button => button.Label.Contains("@agents_panel", StringComparison.Ordinal))
            && Descendants(agentsRows).OfType<Button>().LastOrDefault(button => button.Label.Contains("2 tool calls", StringComparison.Ordinal)) is { } after
            && MathF.Abs(after.Bounds.Max.Y - groupTop) < 2.0f);

        // Each chat keeps where it was read to. Scrolled up in the long one, away to another and back, it's where it was
        HexAgentChat? longChat = null, other = null;
        float readTo = 0;
        test.Run(() =>
        {
            longChat = agentChat;
            agentsScroll.Offset = readTo = MathF.Floor(agentsScroll.MaxOffset * 0.3f);
            other = new HexAgentChat(++agentChatsMade, "layout_agent");
            agentChats.Add(other);
            other.Ask("Something else");
            other.Say("A short answer.");
            other.Done();
            OpenAgentChat(other);
        });
        test.Settle(0.3f);
        test.Check("agents layout: the agent systems sit under the chats with the fixture's system in them", () =>
            agentsSystemsHolder.Visible && Descendants(agentsSystemsHolder).OfType<Label>().Any(label => label.Text.Contains("Workspace assistant", StringComparison.Ordinal))
            // Up is up on screen, so under the chats is below their list's bottom and above the chat's top
            && agentsSystemsHolder.Bounds.Max.Y <= agentsListScroll.Bounds.Min.Y + 1 && agentsSystemsHolder.Bounds.Min.Y >= agentsScroll.Bounds.Max.Y - 1);

        // Its head pressed, the panel opens within a few ticks, not when the side bar next looks, and the room follows
        float foldedTall = 0;
        test.Run(() =>
        {
            foldedTall = agentsSystemsHolder.Size.Y;
            Descendants(agentsSystems!.Root).OfType<Button>().First().OnPressed!();
        });
        test.Wait();
        test.Check("agents layout: the agent systems open within a few ticks of their head being pressed, the room growing with them", () =>
            agentsSystems!.Open && agentsSystemsHolder.Size.Y > foldedTall);
        test.Run(() => Descendants(agentsSystems!.Root).OfType<Button>().First().OnPressed!());
        test.Wait();

        // A part of the editor sees the sessions through its context, is told when one moves, and can bring one up
        int heard = 0;
        void Heard() => heard++;
        test.Run(() =>
        {
            Context.Agents.Changed += Heard;
            other!.Say("Another line.");
        });
        test.Settle(0.2f);
        test.Run(() => Context.Agents.Show(longChat!.Id));
        test.Settle(0.3f);
        test.Check("agents layout: the parts see the sessions through the context, told when one moves, and can bring one up", () =>
            Context.Agents.Sessions.Any(session => session.Chat == other) && heard > 0 && agentChat == longChat);
        test.Run(() => Context.Agents.Changed -= Heard);

        // Going from agent to agent, the fleet's order round from the end, and the next one that wants looking at
        HexAgentChat? stepped = null, back = null, needed = null;
        test.Run(() =>
        {
            List<HexAgentChat> order = FleetOrder();
            OpenAgentChat(order[^1]);
            StepAgent(1);
            stepped = agentChat;
            StepAgent(-1);
            back = agentChat;
            OpenAgentChat(longChat!);
            other!.Seen = other.Version - 1;
            NextNeedingMe();
            needed = agentChat;
            OpenAgentChat(longChat!);
        });
        test.Check("agents layout: the keys go round the fleet in its order and to the next agent that needs looking at", () =>
            stepped == FleetOrder()[0] && back == FleetOrder()[^1] && needed == other
            && FleetOrder().Count == agentChats.Count && FleetOrder().Distinct().Count() == agentChats.Count);

        // In Noon the Details say the family's conversation, who to whom and how it got there, and a filter shows one kind
        bool talkShown = false, filtered = false;
        test.Run(() =>
        {
            hub.MailOf(longChat!).Add(new HexAgentMail(hub.NextMailSeq(), null, "the person watching", "instruction", "look at the tests first", true) { To = longChat!.Id }, HexMailRoute.Inbox);
            hub.MailOf(longChat!).Add(new HexAgentMail(hub.NextMailSeq(), longChat!.Id, "#x", "finding", "the server starts in StartMcp", false) { To = HexMailLog.USER }, HexMailRoute.Inbox);
            ShowFace(HexFace.Noon);
            OpenAgentChat(longChat!);
            noonWait = 0;
        });
        test.Settle(0.3f);
        test.Run(() =>
        {
            Label[] said = [.. Descendants(noonContextRows).OfType<Label>()];
            talkShown = said.Any(label => label.Text.Contains("look at the tests first", StringComparison.Ordinal))
                && said.Any(label => label.Text.Contains("the server starts in StartMcp", StringComparison.Ordinal));
            Descendants(noonContextRows).OfType<Button>().First(button => button.Label.StartsWith(Said("noon.family_finding"), StringComparison.Ordinal)).OnPressed!();
        });
        test.Settle(0.3f);
        test.Run(() =>
        {
            Label[] said = [.. Descendants(noonContextRows).OfType<Label>()];
            filtered = said.Any(label => label.Text.Contains("the server starts in StartMcp", StringComparison.Ordinal))
                && !said.Any(label => label.Text.Contains("look at the tests first", StringComparison.Ordinal));
            noonTalkShown = string.Empty;
        });

        // The wheel over the margin beside the chat's column scrolls the chat, not only over the column
        float wheelFrom = 0;
        test.Run(() => agentsScroll.Offset = wheelFrom = MathF.Floor(agentsScroll.MaxOffset * 0.5f));
        test.Hover(() => chrome.ToWorld(new Vector2(noonCenter.Bounds.Min.X + 4, noonCenter.Bounds.Center.Y)));
        test.Run(() => compositor.Scroll(-2));
        test.Settle(0.3f);
        test.Check("agents layout: in Agentic the wheel over the margins round the chat scrolls it", () =>
            agentsScroll.Offset > wheelFrom + 1.0f);
        test.Check("agents layout: in Agentic no side's handle shows, there's no side there to drag", () =>
            Splitters.Left is not { Visible: true } && Splitters.Right is not { Visible: true });
        test.Run(() =>
        {
            // Back where the chat was read to, which a check further on holds it to
            agentsScroll.Offset = readTo;
            ShowFace(HexFace.Dawn);
        });
        test.Settle(0.2f);
        test.Check("agents layout: back in Dawn the side has one handle, the line's, the agents view none of its own", () =>
            agentsView.Resizable == UIResize.None && Splitters.Left is { Visible: true });
        test.Check("agents layout: Noon's Details say the family's conversation, and a filter shows only its kind", () => talkShown && filtered);

        // The status bar says the agents and how many want looking at, and follows one being looked at
        int wantingBefore = 0;
        test.Run(() =>
        {
            other!.Seen = other.Version - 1;
            PublishAgentStatuses();
            wantingBefore = AgentsWanting();
        });
        test.Check("agents layout: the status bar has the agents with how many want looking at", () =>
            agentsItem.IsVisible?.Invoke() == true && wantingBefore > 0 && agentsItem.Text.Contains($"$(bell-dot) {wantingBefore}", StringComparison.Ordinal));
        test.Run(() =>
        {
            other!.Seen = other.Version;
            PublishAgentStatuses();
        });
        test.Check("agents layout: and puts it right once the agent is looked at", () =>
            AgentsWanting() < wantingBefore && (AgentsWanting() == 0 ? !agentsItem.Text.Contains("bell-dot", StringComparison.Ordinal) : agentsItem.Text.Contains($"$(bell-dot) {AgentsWanting()}", StringComparison.Ordinal)));
        test.Check("agents layout: a chat gone back to is where it was read to, not at its end", () =>
            MathF.Abs(agentsScroll.Offset - readTo) < 2 && agentsScroll.Offset < agentsScroll.MaxOffset - 10);

        // A row right-clicked offers its text to copy or to pick out of in a window, and the whole chat as Markdown
        bool menuSays = false, windowSelects = false;
        test.Run(() =>
        {
            HexAgentEntry said = longChat!.Entries.First(entry => entry.Kind == HexAgentEntryKind.Text);
            UIComponent row = agentRowsShown.First(shown => ReferenceEquals(shown.Entry, said)).Row;
            Menu menu = AgentRowMenu(longChat!, said);
            menuSays = row.OnContextMenu is not null && menu.Items.Any(item => item.Label == Said("agents_panel.select_text"))
                && ChatAsMarkdown(longChat!).Contains(said.Text, StringComparison.Ordinal);
            ShowAgentText(said.Text, longChat!.Shown);
        });
        test.Settle(0.2f);
        test.Run(() =>
        {
            WindowPanel? window = chrome.Root.Children.OfType<WindowPanel>().LastOrDefault();
            windowSelects = window is not null && Descendants(window).OfType<CodeEditor>().FirstOrDefault() is { ReadOnly: true } shown
                && shown.Document.HasSelection && shown.Document.SelectedText.Length > 0;
            window?.Parent?.Remove(window);
        });
        test.Check("agents layout: a row right-clicked copies its text or opens it to pick any part out", () => menuSays && windowSelects);

        // A message held for a working agent goes back to the box to be changed, out of the chat until it's sent again
        bool offered = false, taken = false;
        int entriesBefore = 0;
        test.Run(() =>
        {
            entriesBefore = longChat!.Entries.Count;
            HexAgentEntry held = longChat.Steer("look at the tests first");
            heldSteers.Add((longChat, held, "{}", "look at the tests first"));
            offered = AgentRowMenu(longChat, held).Items.Any(item => item.Label == Said("agents_panel.edit_queued"));
            taken = EditHeld(held);
        });
        test.Settle(0.2f);
        test.Check("agents layout: a queued message still held goes back to the box to be changed, out of the chat", () =>
            offered && taken && heldSteers.Count == 0 && longChat!.Entries.Count == entriesBefore && agentsInput.Text == "look at the tests first");
        test.Run(() => agentsInput.Text = string.Empty);

        // The box's words are in the UI's font, whose narrow letters take less room than a digit. A line of them half
        // again as wide as the box wraps once, not three times as it did when it was counted in digits
        test.Run(() =>
        {
            float scale = agentsInput.TextScale;
            Horizon.UI.Drawing.UIFont font = compositor.Skin!.Font;
            string prose = string.Empty;
            while (font.Measure(prose, scale, markup: false).X < agentsInput.Bounds.Width * 1.5f)
                prose += "ill fill till ";
            agentsInput.Text = prose.TrimEnd();
        });
        test.Settle(0.2f);
        test.Run(() =>
        {
            if (agentsInput.RowCount != 2)
                Console.WriteLine($"[Hex self-test]   the box is {agentsInput.Bounds.Width:0} wide and its prose took {agentsInput.RowCount} rows");
        });
        test.Check("agents layout: the box wraps prose by how wide its letters are, filling each row first", () => agentsInput.RowCount == 2);
        test.Run(() => agentsInput.Text = string.Empty);

        // A picture pasted with no text beside it is a chip over the pickers, the card growing for it, and goes with the
        // message as its path. A paste of text is text as ever. The clipboard is a stand-in so the real one is left alone
        Func<byte[]?>? realClipboard = Horizon.Input.ClipboardPicture.Reader;
        float cardBefore = 0;
        bool textTaken = true;
        string prompt = "look", asked = "look";
        string[] sent = [];
        test.Run(() =>
        {
            cardBefore = agentsComposeTall;
            Horizon.Input.ClipboardPicture.Reader = () => Horizon.Graphics.ImageFile.EncodePng([255, 0, 0, 255], 1, 1);
            agentsInput.OnPasting!(string.Empty);
            textTaken = agentsInput.OnPasting!("some text");
        });
        test.Settle(0.2f);
        test.Check("agents layout: a pasted picture is a chip in the card, which grows for it, and pasted text is still text", () =>
            agentImages.Count == 1 && File.Exists(agentImages[0]) && agentsImages.Visible && agentsComposeTall > cardBefore && !textTaken
            && Descendants(agentsImages).OfType<Label>().Any(label => label.Text.Contains(Said("agents_panel.image", ("number", "1")), StringComparison.Ordinal)));
        test.Run(() => sent = TakeAgentImages(ref prompt, ref asked));
        test.Settle(0.2f);
        test.Check("agents layout: sent, the picture goes with the message by its path and the chip goes", () =>
            sent.Length == 1 && prompt.EndsWith($"[Pasted image: {sent[0]}]", StringComparison.Ordinal) && asked.StartsWith("look\n\n", StringComparison.Ordinal)
            && agentImages.Count == 0 && !agentsImages.Visible);
        test.Run(() =>
        {
            Horizon.Input.ClipboardPicture.Reader = realClipboard;
            foreach (string made in sent)
                File.Delete(made);
        });

        // Somebody's word to an agent sitting in a wait for its agents goes by its mail, which wakes the wait, where on its
        // stdin it would only be read once the wait was over
        test.Run(() =>
        {
            other!.Ask("Coordinate the others");
            OpenAgentChat(other);
            agentsWaiting[other.Id] = 1;
            agentsInput.Text = "Stop and look at the tests first";
            SendToAgent();
        });
        test.Settle(0.2f);
        test.Check("agents layout: what's typed for an agent in a long wait wakes it by its mail", () =>
            other!.UnreadMail() is [{ Directive: true, Kind: "instruction" } told] && told.Text.Contains("look at the tests", StringComparison.Ordinal)
            && other.Entries.Any(entry => entry.Kind == HexAgentEntryKind.User && !entry.Queued && entry.Text.Contains("look at the tests", StringComparison.Ordinal))
            && other.Entries[^1].Text == Said("agents_panel.told_waiting") && agentsInput.Text.Length == 0);
        test.Run(() =>
        {
            agentsWaiting.TryRemove(other!.Id, out _);
            other.Done();
        });

        test.Run(() =>
        {
            agentChats.Remove(other!);
            ClearAgentChat();
            settings.UiAnimation = animation;
            extensions.ForgetAgents(provider!);
            extensions.Remove(provider!);
            foreach (var profile in profiles) HexAgentProfiles.Set(profile);
        });
        test.Run(() => { running.OpenRunner(); running.runnerWindow!.Size = running.runnerWindow.MinSize; });
        test.Settle(0.2f);
        test.Check("runner layout: fields fit inside the minimum window width", () =>
            running.runnerView.Bounds.Max.X <= running.runnerWindow!.Bounds.Max.X - running.runnerWindow.Padding.Right + 1
            && running.runnerFunction.Bounds.Max.X <= running.runnerWindow.Bounds.Max.X - running.runnerWindow.Padding.Right + 1);
        test.Run(() => running.runnerWindow?.Close());
        test.Run(() => { ShowSide(HexSide.Explorer); CloseNow(); });
    }

    private void TestAgentWelcomeResize(HexPointerScript test)
    {
        foreach (bool narrowing in new[] { true, false })
        {
            int step = 0, minLines = int.MaxValue, maxLines = 0;
            float previous = 0, reversal = 0, dragFromWidth = 0;
            Vector2 start = default;
            test.Run(() => { settings.LeftWidth = narrowing ? 400 : MIN_SIDE; ApplyPanelSizes(); });
            test.Settle(0.3f);
            test.Run(() =>
            {
                start = agentsView.Bounds.Center;
                dragFromWidth = agentsView.Bounds.Width;
                agentsView.BeginResize(UIResize.Right, start);
            });
            test.WaitFor(() =>
            {
                // Sample every layout, including the first measure after the sidebar updates its rows.
                Label hint = agentsRows.Children.OfType<Label>().Skip(1).First();
                float below = agentsRows.Bounds.Max.Y - agentsRows.Children.OfType<Button>().First().Bounds.Max.Y;
                if (step > 1)
                    reversal = Math.Max(reversal, narrowing ? previous - below : below - previous);
                previous = below;
                minLines = Math.Min(minLines, hint.LineCount);
                maxLines = Math.Max(maxLines, hint.LineCount);
                float width = narrowing ? 400 - step * 2 : MIN_SIDE + step * 2;
                agentsView.DragResize(start + new Vector2(width - dragFromWidth, 0));
                return ++step > 110;
            }, 10);
            test.Run(agentsView.EndResize);
            test.Check($"agents layout: wrapped helper text reflows monotonically while {(narrowing ? "narrowing" : "widening")}", () =>
            {
                if (reversal > 1) Console.WriteLine($"[Hex self-test] helper crossed back by {reversal:0.0} px");
                return step == 111 && minLines < maxLines && reversal <= 1;
            });
        }
    }
}
