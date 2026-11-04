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
            grip = chrome.ToWorld(new Vector2(agentsView.Bounds.Max.X, agentsView.Bounds.Center.Y));
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
        test.Click(() => chrome.ToWorld(Descendants(agentsRows).OfType<Label>()
            .First(label => label.Text == "$(chevron-right)").Bounds.Center));
        test.Settle(0.3f);
        test.Check("agents layout: opening a tool exposes its result", () =>
            tool?.Open == true && Descendants(agentsRows).OfType<Label>().Any(label => label.Text.Contains("Found the settings loader")));
        Capture("agents-transcript");
        test.Run(() =>
        {
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
