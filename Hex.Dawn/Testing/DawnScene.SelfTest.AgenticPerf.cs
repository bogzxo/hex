using System.Diagnostics;
using System.Text.Json.Nodes;

using Hex.Shell;

namespace Hex.Dawn;

// How Agentic holds up with a fleet of long chats, measured rather than checked by eye. A dozen chats of a few hundred
// rows each (asked, said, groups of tool calls with what they were given and said back) are put in, then Agentic is
// measured standing still, going from chat to chat, scrolled up and down, with one agent streaming into its chat, and
// with groups opened and folded. Each phase says its frames (the median and the 95th percentile, in milliseconds), what
// was allocated a frame and the process's CPU, printed with the checks and written to agentic-perf.json in the test's
// folder. The checks only catch what's badly wrong, the numbers are for comparing builds (the NativeAOT release one
// against the dev one).
internal sealed partial class DawnScene
{
    // The frames as they came while a phase is measured, their lengths in seconds, null while nothing's measured
    private List<float>? perfFrames;

    // How many chats are put in and how many turns each has
    private const int PERF_CHATS = 12, PERF_TURNS = 40;

    private void TestAgenticPerf(HexPointerScript test)
    {
        var chats = new List<HexAgentChat>();
        var results = new JsonObject();
        (string Name, long Allocated, TimeSpan Cpu, Stopwatch Clock, int From, (long Turns, long Bytes) Render, (long Turns, long Bytes) Logic)? phase = null;

        // The engine's own loops, drawing and updating, which say how long their turns take and what they allocate
        Horizon.Core.Threading.LoopStatistics? Loop(string name) =>
            Engine.WindowManager.Loops.FirstOrDefault(loop => string.Equals(loop.Name, name, StringComparison.OrdinalIgnoreCase));
        HexAgentChat? streaming = null;

        void Start(string name) => test.Run(() =>
        {
            perfFrames ??= [];
            Engine.WindowManager.RequestFrames(30);
            using Process self = Process.GetCurrentProcess();
            phase = (name, GC.GetTotalAllocatedBytes(false), self.TotalProcessorTime, Stopwatch.StartNew(), perfFrames.Count,
                Loop("Render")?.AllocationTotals ?? default, Loop("Logic")?.AllocationTotals ?? default);
        });

        void End() => test.Run(() =>
        {
            if (phase is not { } measured || perfFrames is null)
                return;
            measured.Clock.Stop();
            using Process self = Process.GetCurrentProcess();
            float[] frames = [.. perfFrames.Skip(measured.From).Select(dt => dt * 1000.0f).Order()];
            double seconds = Math.Max(0.001, measured.Clock.Elapsed.TotalSeconds);
            long allocated = GC.GetTotalAllocatedBytes(false) - measured.Allocated;
            double cpu = (self.TotalProcessorTime - measured.Cpu).TotalSeconds / seconds * 100.0;
            float Percentile(double share) => frames.Length == 0 ? 0 : frames[Math.Clamp((int)Math.Ceiling(share * frames.Length) - 1, 0, frames.Length - 1)];
            var render = Loop("Render");
            var logic = Loop("Logic");
            (long Turns, long Bytes) rendered = render is null ? default : (render.AllocationTotals.Turns - measured.Render.Turns, render.AllocationTotals.Bytes - measured.Render.Bytes);
            (long Turns, long Bytes) updated = logic is null ? default : (logic.AllocationTotals.Turns - measured.Logic.Turns, logic.AllocationTotals.Bytes - measured.Logic.Bytes);
            var said = new JsonObject
            {
                // The time between updates, which is the window's pace (it sleeps between steps of a test) as much as work
                ["updates"] = frames.Length,
                ["seconds"] = Math.Round(seconds, 2),
                ["update_gap_median_ms"] = Math.Round(Percentile(0.5), 2),
                ["update_gap_p95_ms"] = Math.Round(Percentile(0.95), 2),
                // What a frame costs, drawing it and updating the scene, as the engine's loops measure their turns
                ["frames_drawn_per_second"] = Math.Round(rendered.Turns / seconds, 1),
                ["render_work_ms"] = Math.Round(render?.WorkMs ?? 0, 2),
                ["render_peak_ms"] = Math.Round(render?.PeakWorkMs ?? 0, 2),
                ["render_allocated_kb_per_frame"] = Math.Round(rendered.Bytes / 1024.0 / Math.Max(1, rendered.Turns), 1),
                ["update_work_ms"] = Math.Round(logic?.WorkMs ?? 0, 2),
                ["update_peak_ms"] = Math.Round(logic?.PeakWorkMs ?? 0, 2),
                ["update_allocated_kb_per_turn"] = Math.Round(updated.Bytes / 1024.0 / Math.Max(1, updated.Turns), 1),
                ["allocated_kb_per_update"] = Math.Round(allocated / 1024.0 / Math.Max(1, frames.Length), 1),
                ["cpu_percent_of_a_core"] = Math.Round(cpu, 1),
                ["working_set_mb"] = Math.Round(self.WorkingSet64 / 1048576.0, 1),
                ["managed_heap_mb"] = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1)
            };
            results[measured.Name] = said;
            Console.WriteLine($"[Hex self-test] agentic perf {measured.Name}: {said.ToJsonString()}");
            phase = null;
        });

        // The fleet, each chat a few hundred rows, past what a chat keeps in memory so its oldest go to its file
        test.Run(() =>
        {
            for (int c = 0; c < PERF_CHATS; c++)
            {
                var chat = new HexAgentChat(++agentChatsMade, "perf_agent") { Name = $"Perf chat {c + 1}", Model = "claude-opus-5-5" };
                agentChats.Add(chat);
                chats.Add(chat);
                for (int turn = 0; turn < PERF_TURNS; turn++)
                {
                    chat.Ask($"Turn {turn} of chat {c}, look at the loader and say what changes");
                    chat.Say($"Looking at **the loader** for turn {turn}.\n\n- one point\n- another with `code`\n\nThen the files.");
                    for (int call = 0; call < 6; call++)
                    {
                        string id = $"t{turn}-{call}";
                        chat.Tool(id, call % 2 == 0 ? "mcp__hex__hex_read" : "mcp__hex__hex_search",
                            $"{{\"path\":\"Hex.Core/Agents/File{call}.cs\",\"symbol\":\"Load{turn}\",\"start_line\":{call * 10}}}");
                        chat.ToolDone(id, call != 3 || turn % 5 != 0, string.Join('\n', Enumerable.Range(0, 12).Select(line => $"{line + 1}  var value{line} = Load(\"{turn}-{call}\");")));
                    }
                    chat.Say($"Turn {turn} done, the loader reads the settings once and keeps them.");
                    chat.Done();
                }
            }

            ShowFace(HexFace.Noon);
            OpenAgentChat(chats[0]);
        });
        test.Settle(1.5f);
        test.Check("agentic perf: the chats are in, their oldest rows gone to their files", () =>
            chats.All(chat => chat.Dropped > 0 && chat.Entries.Count <= HexAgentChat.MOST_ENTRIES + HexAgentChat.SPILL_BATCH));

        // Standing still, a chat on screen and the fleet beside it, what each stretch and step of the update allocates
        // weighed and said, the most first, for what's left to bring down
        int idleFrom = 0;
        test.Run(() =>
        {
            HexTicks.Weighing = true;
            foreach (HexTicks ticks in (HexTicks[])[workTicks, lateTicks, panelTicks])
                ticks.ResetWeighed();
            Array.Clear(weighedStretches);
            idleFrom = perfFrames?.Count ?? 0;
        });
        Start("idle");
        test.Settle(2.0f);
        End();
        test.Run(() =>
        {
            HexTicks.Weighing = false;
            int updates = Math.Max(1, (perfFrames?.Count ?? 0) - idleFrom);
            var heavy = WEIGHED_STRETCHES.Select((name, at) => (Step: name, Bytes: weighedStretches[at]))
                .Concat(((HexTicks[])[workTicks, lateTicks, panelTicks]).SelectMany(ticks => ticks.Weighed))
                .Where(each => each.Bytes > 0).OrderByDescending(each => each.Bytes).Take(14);
            foreach (var (step, bytes) in heavy)
                Console.WriteLine($"[Hex self-test]   idle allocates in {step}: {bytes / (double)updates:0} bytes an update");
        });

        // Going from chat to chat, each drawn from its start
        Start("switching");
        for (int i = 0; i < PERF_CHATS * 2; i++)
        {
            int at = i % PERF_CHATS;
            test.Run(() => OpenAgentChat(chats[at]));
            test.Wait();
        }
        End();

        // Scrolled up through the chat and back down
        Start("scrolling");
        for (int i = 0; i <= 40; i++)
        {
            int step = i;
            test.Run(() => agentsScroll.Offset = agentsScroll.MaxOffset * MathF.Abs(1.0f - step / 20.0f));
            test.Wait();
        }
        End();

        // An agent streaming into the chat on screen, a line or a call a frame
        test.Run(() =>
        {
            streaming = chats[0];
            OpenAgentChat(streaming);
            agentsScroll.ScrollToEnd();
            streaming.Ask("Stream something long");
        });
        test.Settle(0.3f);
        Start("streaming");
        for (int i = 0; i < 120; i++)
        {
            int step = i;
            test.Run(() =>
            {
                if (step % 4 == 3)
                {
                    streaming!.Tool($"s{step}", "mcp__hex__hex_search", $"{{\"query\":\"thing {step}\"}}");
                    streaming.ToolDone($"s{step}", true, $"{step} places in 3 files");
                }
                else
                    streaming!.Say($"Streaming line {step}, with **some** words that wrap as the column is narrow enough.");
            });
        }
        test.Run(() => streaming?.Done());
        End();

        // Groups opened and folded, in place
        Start("groups");
        for (int i = 0; i < 20; i++)
        {
            test.Run(() =>
            {
                if (Descendants(agentsScroll).OfType<Horizon.UI.Components.Button>().FirstOrDefault(button => button.Label.Contains("tool calls", StringComparison.Ordinal)) is { OnPressed: { } press })
                    press();
            });
            test.Wait();
        }
        End();

        test.Run(() =>
        {
            perfFrames = null;
            string kind = (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported ? "jit" : "nativeaot")
#if DEBUG
                + "-debug";
#else
                + "-release";
#endif
            results["build"] = $"{HexAbout.Version} {kind}";
            // In the test's folder, and by the kind of build in the temp folder too, where a run of the dev build and one of
            // the release build sit side by side to be compared
            string json = results.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(testFolder, "agentic-perf.json"), json);
            string compared = Path.Combine(Path.GetTempPath(), $"hex-agentic-perf-{kind}.json");
            File.WriteAllText(compared, json);
            Console.WriteLine($"[Hex self-test] agentic perf written to {compared}");
        });
        test.Check("agentic perf: every phase was measured", () => results.Count >= 5);
        test.Check("agentic perf: no phase's update or drawing took a quarter of a second at its worst", () =>
            results.Where(pair => pair.Value is JsonObject).All(pair => (double)pair.Value!["render_peak_ms"]! < 250.0 && (double)pair.Value!["update_peak_ms"]! < 250.0));

        test.Run(() =>
        {
            foreach (HexAgentChat chat in chats)
                CloseAgentChat(chat);
            ShowFace(HexFace.Dawn);
        });
        test.Settle(0.3f);
    }
}
