using System.Net;
using Hex.Core;
using Hex.Core.Modules;
using Xunit.Abstractions;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

/// <summary>
/// Allocation budgets for the paths that run on the editor's thread all the time: a keystroke with the prediction engine
/// and its history attached, an update tick with Link running and idle, with Local AI idle. Each counts what the calling
/// thread allocated (<see cref="GC.GetAllocatedBytesForCurrentThread"/>) over many repeats, after a warm-up, with no clock
/// and no sleep, so a result is the same on a slow machine. The budgets are a few times what the code allocates when this
/// was written; a change that goes past one made a hot path allocate, which on a 240 Hz idle loop is megabytes a second.
/// </summary>
public sealed class HexPerfBudgetTests(ITestOutputHelper output)
{
    private static readonly HexLinkRights EDIT = HexLinkRights.View | HexLinkRights.Edit;

    /// <summary>The bytes the calling thread allocated per repeat of a step, after a warm-up of the same step.</summary>
    private static double PerStep(int repeats, Action<int> step, int warm = 64)
    {
        for (int i = 0; i < warm; i++)
            step(i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < repeats; i++)
            step(warm + i);
        return (GC.GetAllocatedBytesForCurrentThread() - before) / (double)repeats;
    }

    private static string[] Document(int lineCount)
    {
        var lines = new string[lineCount];
        for (int i = 0; i < lines.Length; i++)
            lines[i] = "    var value" + i + " = Compute(" + i + ", \"text " + i + "\");";
        return lines;
    }

    /// <summary>A line as it is after each of a thousand typed characters, made before anything is measured.</summary>
    private static string[] Typed(string start, int count)
    {
        var typed = new string[count];
        string line = start;
        for (int i = 0; i < count; i++)
            typed[i] = line += (char)('a' + i % 26);
        return typed;
    }

    private PredictionEngine Engine(string[] lines, int version, object document)
    {
        // The settings hand the engine the same options every tick, so the test's own copy must not allocate one each time
        PredictionOptions options = PredictionTestKit.Options(debounce: 200);
        var engine = new PredictionEngine(new HandClock())
        {
            Options = () => options,
            ProviderOverride = new ScriptedProvider("x"),
        };
        engine.Snapshot = asked => new PredictionSnapshot(asked, version, "src/a.cs", "csharp", lines, 2500, lines[2500].Length);
        return engine;
    }

    [Fact]
    public void A_keystroke_allocates_next_to_nothing_with_the_prediction_engine_attached()
    {
        // 1,000 characters typed into the middle of a 5,000 line document: the engine told of each edit and ticked
        // between keys, the clock still, so no request is made, as at speed typing
        string[] lines = Document(5000);
        string[] typed = Typed(lines[2500], 1064);
        var document = new object();
        PredictionEngine engine = Engine(lines, 1, document);

        double perKey = PerStep(1000, i =>
        {
            engine.Edited(document, i + 2, typedThrough: false);
            engine.Tick();
            _ = engine.Take();
        });

        output.WriteLine($"Edited + Tick + Take: {perKey:0.0} B a keystroke");
        Assert.Equal(0, engine.Requests);
        Assert.True(perKey <= 128, $"a keystroke through the engine allocated {perKey:0.0} B, the budget is 128");
    }

    [Fact]
    public async Task A_keystroke_noted_in_the_history_costs_a_small_record_not_the_document()
    {
        // The glue notes every edit with NoteSoon: the lines are the glue's own array, what the history adds is its record
        string[] lines = Document(5000);
        string[] typed = Typed(lines[2500], 1064);
        var engine = Engine(lines, 1, new object());
        engine.History.Seed("src/a.cs", lines);

        double perKey = PerStep(1000, i =>
        {
            engine.History.NoteSoon("src/a.cs", i + 2, lines, 2500);
        });
        await engine.History.Drained;

        output.WriteLine($"History.NoteSoon: {perKey:0.0} B a keystroke");
        Assert.True(perKey <= 1024, $"noting a keystroke allocated {perKey:0.0} B, the budget is 1,024");
    }

    [Fact]
    public void Diffing_a_one_line_change_in_5000_lines_allocates_in_proportion_to_the_text_and_not_more()
    {
        // The noting itself, run on the history's own thread in the app: a budget on its work per change so it stays
        // linear in the text (joined once, diffed once) and never grows with the number of changes already kept
        string[] lines = Document(5000);
        string[] typed = Typed(lines[2500], 264);
        var history = new PredictionHistory();
        history.Note("src/a.cs", string.Join('\n', lines));
        int textBytes = lines.Sum(l => l.Length + 1) * sizeof(char);

        double perNote = PerStep(200, i =>
        {
            lines[2500] = typed[i];
            history.Note("src/a.cs", string.Join('\n', lines), 2500);
        }, warm: 64);

        double text = textBytes;
        output.WriteLine($"History.Note: {perNote / 1024:0.0} KB a note over {textBytes / 1024} KB of text ({perNote / text:0.0}x)");
        // 11x when this was written (the join, the old text kept, the split lines of both, the diff); 16x is the ceiling
        Assert.True(perNote <= 16 * text, $"a note allocated {perNote / text:0.0}x the text, the budget is 16x");
    }

    [Fact]
    public async Task Idle_link_with_a_peer_allocates_next_to_nothing_a_tick()
    {
        using LinkedPair pair = await LinkedPair.Up(EDIT, EDIT);

        // The editor's tick as the editor does it: the hub pumped under the editor's lock, then every link's chats patched
        double perTick = PerStep(2000, _ =>
        {
            pair.A.OnEditor(() => { });
            pair.LinkA.Pump();
            pair.B.OnEditor(() => { });
            pair.LinkB.Pump();
        });

        output.WriteLine($"Link tick with a peer, A and B: {perTick:0.0} B a tick");
        Assert.True(perTick <= 256, $"an idle Link tick allocated {perTick:0.0} B, the budget is 256");
    }

    [Fact]
    public void Idle_link_with_no_peers_allocates_nothing_so_starts_no_task_a_tick()
    {
        // A Task is at least 60 bytes and a Process more: zero bytes on the calling thread means neither was made. (Link's
        // own code in Hex.Core starts no process; git is the editor's, not a tick's.)
        using var services = new LinkServices(DeviceA);
        using var link = new HexLink(services);

        double perTick = PerStep(10_000, _ =>
        {
            link.Pump();
            link.FleetMoved();
            link.PredictionsMoved();
        });

        output.WriteLine($"Link tick with no peers: {perTick:0.00} B a tick");
        Assert.True(perTick <= 8, $"an idle Link with no peers allocated {perTick:0.00} B a tick, the budget is 8");
    }

    [Fact]
    public void Idle_prediction_engine_allocates_nothing_a_tick()
    {
        // Local AI idle: the engine ticked every frame with nothing typed and nothing asked
        PredictionEngine engine = Engine(Document(10), 1, new object());

        double perTick = PerStep(10_000, _ => engine.Tick());

        output.WriteLine($"Idle engine tick: {perTick:0.00} B a tick");
        Assert.True(perTick <= 8, $"an idle engine tick allocated {perTick:0.00} B, the budget is 8");
        Assert.Equal(0, engine.Requests);
    }

    [Fact]
    public async Task Idle_local_ai_server_asked_to_start_again_makes_no_request_and_allocates_little()
    {
        // The editor keeps asking for the config it has; a server already adopted must answer from what it knows
        var config = new LlamaCppServerConfig("llama-server", "m.gguf", "local", 8080, 32768, "auto", "");
        var router = FakeServer.Answering(HttpStatusCode.OK, "{\"data\":[{\"id\":\"local\"}]}");
        using var llama = new LlamaCppServer(new HttpClient(router));
        Assert.True(await llama.Start(config, TimeSpan.FromSeconds(5)));
        Assert.True(llama.Adopted);
        int sent = router.Sent.Count;

        double perAsk = PerStep(2000, tick =>
        {
            Task<bool> asked = llama.Start(config);
            Assert.True(asked.IsCompletedSuccessfully && asked.Result);
            _ = llama.State;
            _ = llama.LogVersion;
        });

        output.WriteLine($"Start(config) again with the router adopted: {perAsk:0.0} B an ask");
        Assert.Equal(sent, router.Sent.Count);
        Assert.True(perAsk <= 512, $"asking an adopted server to start allocated {perAsk:0.0} B, the budget is 512");
    }

    [Fact]
    public void Module_gate_hot_path_allocates_nothing()
    {
        // HexModules.IsEnabled is checked on simulation-thread tick paths and UI builders, so it must allocate 0 bytes
        HexModules.SetEnabled(HexModuleId.Link, true);
        HexModules.SetEnabled(HexModuleId.Agentic, true);
        HexModules.SetEnabled(HexModuleId.Notebooks, true);

        double perCheck = PerStep(10_000, _i =>
        {
            _ = HexModules.IsEnabled(HexModuleId.Link);
            _ = HexModules.IsEnabled(HexModuleId.LocalAi);
            _ = HexModules.IsEnabled(HexModuleId.Agentic);
            _ = HexModules.IsEnabled(HexModuleId.Notebooks);
            _ = HexModules.IsEnabled(HexModuleId.Designer);
        });

        output.WriteLine($"HexModules.IsEnabled (5 checks): {perCheck:0.00} B");
        Assert.True(perCheck <= 0.01, $"HexModules.IsEnabled allocated {perCheck:0.00} B per 5 checks, expected 0 B");
    }

    [Fact]
    public void Agent_chat_streaming_append_and_tool_calls_stay_within_budget()
    {
        // Streaming appends must stay amortized O(1) via StringBuilder instead of O(N^2) string concatenation
        var chat = new HexAgentChat(1, "claude_code");
        chat.Ask("Stream something long");
        chat.Say("Start\n");
        const string chunk = "Streaming line with **some** words.\n";

        double perAppend = PerStep(1000, _ => chat.Say(chunk, append: true), warm: 32);

        output.WriteLine($"HexAgentChat.Say(append: true): {perAppend:0.0} B a chunk");
        Assert.True(perAppend <= 256, $"streaming append allocated {perAppend:0.0} B a chunk, the budget is 256 B");

        // Tool call start + completion hot path (pre-built inputs so only chat bookkeeping is measured)
        const int toolCount = 160;
        var ids = new string[toolCount + 32];
        for (int i = 0; i < ids.Length; i++)
            ids[i] = "tool-" + i;
        const string input = "{\"path\":\"Hex.Core/Agents/HexAgentChat.cs\"}";
        const string result = "ok";

        var toolChat = new HexAgentChat(2, "claude_code");
        toolChat.Ask("Run tools");
        double perTool = PerStep(toolCount, i =>
        {
            string id = ids[i];
            toolChat.Tool(id, "mcp__hex__hex_read", input);
            toolChat.ToolDone(id, true, result);
        }, warm: 32);

        output.WriteLine($"HexAgentChat.Tool + ToolDone: {perTool:0.0} B a call");
        Assert.True(perTool <= 1024, $"tool call recording allocated {perTool:0.0} B a call, the budget is 1,024 B");
    }
}
