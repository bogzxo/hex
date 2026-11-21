using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

using Hex.Core;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

/// <summary>
/// Predictions over a link (docs/hex-link-plan.md, 5.6): A is the desktop with the model, B the laptop that borrows it.
/// The text streams in order and done ends it, a cancel reaches A's model at once, the Predict right and A's share
/// setting both have to be so (and nothing else is given by the right), A's limits hold, a model that fails says how,
/// the link closing ends the stream, B's ServesPredictions follows A's rights and setting as they change, and a slow
/// request in flight doesn't hold a prediction up.
/// </summary>
public sealed class HexLinkPredictTests
{
    /// <summary>
    /// A's services with a model: everything else the kit's, ServesPredictions what <see cref="Serving"/> says and a
    /// prediction what <see cref="Model"/> makes. Tools can be made slow, to stand for a slow request in flight.
    /// </summary>
    private sealed class PredictingServices(LinkServices inner) : IHexLinkServices
    {
        public LinkServices Inner { get; } = inner;

        public volatile bool Serving = true;

        public Func<HexLinkPrediction, CancellationToken, IAsyncEnumerable<string>> Model { get; set; } = (_, cancel) => Pieces(cancel, "a");

        public SlowTools Slow { get; } = new(inner.Tools);

        public HexLinkDevice Device => Inner.Device;
        public HexLinkWorkspace Workspace => Inner.Workspace;
        public HexHubSnapshot Fleet => Inner.Fleet;
        public IReadOnlyDictionary<int, HexLinkChatExtra> FleetExtras => Inner.FleetExtras;
        public Task<JsonObject?> Chat(int id, Func<HexAgentChat, JsonObject?> read) => Inner.Chat(id, read);
        public IMcpTools Tools => Slow;
        public Task<bool> Answer(int chat, string answer) => Inner.Answer(chat, answer);
        public void Presence(HexLinkDevice device, HexLinkPresence? presence) => Inner.Presence(device, presence);
        public Task<string> PatchOffered(HexLinkDevice device, string title, string diff) => Inner.PatchOffered(device, title, diff);
        public void Audit(string what, HexLinkDevice who, string detail) => Inner.Audit(what, who, detail);

        public bool ServesPredictions => Serving;

        public IAsyncEnumerable<string> Predict(HexLinkPrediction request, CancellationToken cancel) => Model(request, cancel);
    }

    /// <summary>Tools whose calls wait <see cref="Wait"/> first, a slow request.</summary>
    private sealed class SlowTools(IMcpTools inner) : IMcpTools
    {
        public TimeSpan Wait { get; set; }

        public IReadOnlyList<McpTool> Tools => inner.Tools;

        public async Task<McpToolResult> Call(string name, JsonObject arguments, CancellationToken cancel)
        {
            await Task.Delay(Wait, cancel);
            return await inner.Call(name, arguments, cancel);
        }
    }

    /// <summary>A desktop and a laptop linked in memory, what the laptop may do on the desktop settable.</summary>
    private sealed class Linked : IDisposable
    {
        public PredictingServices Desktop { get; } = new(new LinkServices(DeviceA));
        public LinkServices Laptop { get; } = new(DeviceB);
        public HexLink DesktopLink { get; }
        public HexLink LaptopLink { get; }
        public HexLinkRights LaptopMay { get; set; }

        /// <summary>The desktop as the laptop sees it, what it asks for predictions.</summary>
        public HexLinkPeer Desk { get; private set; } = null!;

        private Linked(HexLinkRights laptopMay)
        {
            LaptopMay = laptopMay;
            DesktopLink = new HexLink(Desktop);
            LaptopLink = new HexLink(Laptop);
        }

        public static async Task<Linked> Up(HexLinkRights laptopMay, bool serving = true)
        {
            var linked = new Linked(laptopMay);
            linked.Desktop.Serving = serving;
            var (a, b) = Pair();
            Task<HexLinkPeer> laptop = linked.DesktopLink.Add(new HexLinkConnection(a, DeviceB, "memory", true, () => linked.LaptopMay));
            Task<HexLinkPeer> desk = linked.LaptopLink.Add(new HexLinkConnection(b, DeviceA, "memory", false, () => HexLinkRights.None));
            await laptop.WaitAsync(TimeSpan.FromMilliseconds(WAIT_MS));
            linked.Desk = await desk.WaitAsync(TimeSpan.FromMilliseconds(WAIT_MS));
            return linked;
        }

        public void Dispose()
        {
            DesktopLink.Dispose();
            LaptopLink.Dispose();
            Desktop.Inner.Dispose();
            Laptop.Dispose();
        }
    }

    private static readonly HexLinkPrediction ASKED = new("int Add(int a, int b) => ", "\n", 64, ["\n"], "fim");

    private static async IAsyncEnumerable<string> Pieces([EnumeratorCancellation] CancellationToken cancel, params string[] pieces)
    {
        foreach (string piece in pieces)
        {
            await Task.Yield();
            cancel.ThrowIfCancellationRequested();
            yield return piece;
        }
    }

    // A model that says one piece and then waits for its token, saying when it saw it cancelled and counting what it
    // would have said had it carried on
    private sealed class Waiting
    {
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SaidAfterCancel;

        public HexLinkPrediction? Asked { get; private set; }

        public async IAsyncEnumerable<string> Run(HexLinkPrediction request, [EnumeratorCancellation] CancellationToken cancel)
        {
            Asked = request;
            using CancellationTokenRegistration seen = cancel.Register(() => Cancelled.TrySetResult());
            Started.TrySetResult();
            yield return "first";
            try
            {
                await Task.Delay(Timeout.Infinite, cancel);
            }
            finally
            {
                if (cancel.IsCancellationRequested)
                    Cancelled.TrySetResult();
            }

            Interlocked.Increment(ref SaidAfterCancel);
            yield return "never";
        }
    }

    private static async Task<List<string>> All(IAsyncEnumerable<string> stream)
    {
        var said = new List<string>();
        await foreach (string piece in stream)
            said.Add(piece);
        return said;
    }

    private static async Task<HexLinkException> Refused(IAsyncEnumerable<string> stream) =>
        await Assert.ThrowsAsync<HexLinkException>(() => All(stream));

    [Fact]
    public async Task Pieces_stream_in_order_and_done_ends_them()
    {
        using Linked linked = await Linked.Up(HexLinkRights.Predict);
        HexLinkPrediction? asked = null;
        string[] pieces = [.. Enumerable.Range(0, 50).Select(i => $"piece{i} ")];
        linked.Desktop.Model = (request, cancel) =>
        {
            asked = request;
            return Pieces(cancel, pieces);
        };

        Assert.True(linked.Desk.ServesPredictions);
        List<string> said = await All(linked.Desk.Predict(ASKED)).WaitAsync(TimeSpan.FromMilliseconds(WAIT_MS));
        Assert.Equal(pieces, said);
        Assert.NotNull(asked);
        Assert.Equal((ASKED.Prompt, ASKED.Suffix, ASKED.MaxTokens, ASKED.Format), (asked!.Prompt, asked.Suffix, asked.MaxTokens, asked.Format));
        Assert.Equal(ASKED.Stop, asked.Stop);

        // A few more, and the audit log has one line for them all
        for (int i = 0; i < 5; i++)
            await All(linked.Desk.Predict(ASKED));
        Assert.Single(linked.Desktop.Inner.Audited, line => line.What == "predict");
    }

    [Fact]
    public async Task A_cancel_reaches_the_model_at_once_and_nothing_more_arrives()
    {
        using Linked linked = await Linked.Up(HexLinkRights.Predict);
        var model = new Waiting();
        linked.Desktop.Model = model.Run;

        using var typing = new CancellationTokenSource();
        var said = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (string piece in linked.Desk.Predict(ASKED, typing.Token))
            {
                said.Add(piece);
                stopwatch.Restart();
                typing.Cancel();
            }
        });

        await model.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stopwatch.ElapsedMilliseconds < 5000);
        Assert.Equal(["first"], said);
        await Task.Delay(200);
        Assert.Equal(0, model.SaidAfterCancel);

        // Letting go of the stream early is a cancel as well
        var second = new Waiting();
        linked.Desktop.Model = second.Run;
        await foreach (string piece in linked.Desk.Predict(ASKED))
            break;
        await second.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Eventually(() => linked.Desk.ServesPredictions));
    }

    [Fact]
    public async Task Without_the_predict_right_it_is_refused_whatever_else_is_granted()
    {
        foreach (HexLinkRights rights in new[] { HexLinkRights.None, HexLinkRights.View, HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit })
        {
            using Linked linked = await Linked.Up(rights);
            bool asked = false;
            linked.Desktop.Model = (_, cancel) =>
            {
                asked = true;
                return Pieces(cancel, "no");
            };

            Assert.False(linked.Desk.ServesPredictions);
            HexLinkException refused = await Refused(linked.Desk.Predict(ASKED));
            Assert.Equal(HexLinkErrors.NOT_ALLOWED, refused.Code);
            Assert.Equal("predict", HexLinkData.String(refused.Detail as JsonObject, "needs", 20));
            Assert.False(asked);
        }
    }

    [Fact]
    public async Task The_predict_right_alone_gives_predictions_and_nothing_else()
    {
        using Linked linked = await Linked.Up(HexLinkRights.Predict);
        Assert.Equal(HexLinkRights.Predict, linked.Desk.Granted);
        Assert.Empty(linked.Desk.Workspace.Folders);
        Assert.Equal(["a"], await All(linked.Desk.Predict(ASKED)));

        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(linked.Desk.WatchFleet()));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(linked.Desk.ListTools()));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(linked.Desk.Call("hex_read", new JsonObject { ["path"] = "x" })));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(linked.Desk.Session.Ask("fs/read", new JsonObject { ["path"] = "work/x" })));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(linked.Desk.Session.Ask("fs/write", new JsonObject { ["path"] = "work/x", ["content"] = "eA==" })));
    }

    [Fact]
    public async Task A_desktop_that_does_not_share_refuses()
    {
        using Linked linked = await Linked.Up(HexLinkRights.Predict, serving: false);
        Assert.False(linked.Desk.ServesPredictions);
        HexLinkException refused = await Refused(linked.Desk.Predict(ASKED));
        Assert.Equal(HexLinkErrors.PREDICTION_FAILED, refused.Code);
        Assert.Equal(HexLinkPrediction.REFUSED, HexLinkPrediction.StatusOf(refused));
    }

    [Fact]
    public async Task The_desktops_limits_hold()
    {
        using Linked linked = await Linked.Up(HexLinkRights.Predict);

        // Too big a prompt
        HexLinkException big = await Refused(linked.Desk.Predict(ASKED with { Prompt = new string('x', HexLinkPrediction.MOST_TEXT - 10), Suffix = new string('y', 20) }));
        Assert.Equal(HexLinkErrors.TOO_BIG, big.Code);
        // Multi-byte characters count as their bytes
        HexLinkException wide = await Refused(linked.Desk.Predict(ASKED with { Prompt = new string('é', HexLinkPrediction.MOST_TEXT / 2 + 1), Suffix = "" }));
        Assert.Equal(HexLinkErrors.TOO_BIG, wide.Code);

        // Tokens clamped, stops cut
        var clamped = new Waiting();
        linked.Desktop.Model = clamped.Run;
        await foreach (string _ in linked.Desk.Predict(ASKED with { MaxTokens = 99_999, Stop = [.. Enumerable.Range(0, 30).Select(i => new string('s', 100) + i)] }))
            break;
        Assert.Equal(HexLinkPrediction.MOST_TOKENS, clamped.Asked!.MaxTokens);
        Assert.Equal(HexLinkPrediction.MOST_STOPS, clamped.Asked.Stop.Count);
        Assert.All(clamped.Asked.Stop, stop => Assert.Equal(HexLinkPrediction.LONGEST_STOP, stop.Length));
        await foreach (string _ in linked.Desk.Predict(ASKED with { MaxTokens = -5 }))
            break;
        Assert.Equal(HexLinkPrediction.FEWEST_TOKENS, clamped.Asked.MaxTokens);

        // A third at once cancels the oldest, the other two run on
        Waiting first = new(), second = new(), third = new();
        Waiting[] models = [first, second, third];
        int next = 0;
        linked.Desktop.Model = (request, cancel) => models[Interlocked.Increment(ref next) - 1].Run(request, cancel);
        await using IAsyncEnumerator<string> one = linked.Desk.Predict(ASKED).GetAsyncEnumerator();
        await using IAsyncEnumerator<string> two = linked.Desk.Predict(ASKED).GetAsyncEnumerator();
        Assert.True(await one.MoveNextAsync());
        Assert.True(await two.MoveNextAsync());
        await using IAsyncEnumerator<string> three = linked.Desk.Predict(ASKED).GetAsyncEnumerator();
        Assert.True(await three.MoveNextAsync());

        await first.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await one.MoveNextAsync());
        await Task.Delay(100);
        Assert.False(second.Cancelled.Task.IsCompleted);
        Assert.False(third.Cancelled.Task.IsCompleted);
    }

    [Fact]
    public async Task A_model_that_fails_says_how()
    {
        using Linked linked = await Linked.Up(HexLinkRights.Predict);

        // Failing as it runs
        linked.Desktop.Model = (_, cancel) => Failing(cancel);
        HexLinkException failed = await Refused(linked.Desk.Predict(ASKED));
        Assert.Equal(HexLinkErrors.PREDICTION_FAILED, failed.Code);
        Assert.Equal("unreachable", HexLinkPrediction.StatusOf(failed));
        Assert.Contains("model is down", failed.Message);

        // Failing before it starts, answered as the start's error
        linked.Desktop.Model = (_, _) => throw HexLinkPrediction.Failed("no such model", "nosuchmodel");
        HexLinkException refused = await Refused(linked.Desk.Predict(ASKED));
        Assert.Equal(HexLinkErrors.PREDICTION_FAILED, refused.Code);
        Assert.Equal("nosuchmodel", HexLinkPrediction.StatusOf(refused));

        // Anything else it throws is a failure without a status of its own
        linked.Desktop.Model = (_, _) => throw new InvalidOperationException("broken");
        HexLinkException broken = await Refused(linked.Desk.Predict(ASKED));
        Assert.Equal(HexLinkPrediction.FAILED, HexLinkPrediction.StatusOf(broken));

        // And the link goes on
        linked.Desktop.Model = (_, cancel) => Pieces(cancel, "fine");
        Assert.Equal(["fine"], await All(linked.Desk.Predict(ASKED)));
    }

    private static async IAsyncEnumerable<string> Failing([EnumeratorCancellation] CancellationToken cancel)
    {
        yield return "half";
        await Task.Yield();
        throw HexLinkPrediction.Failed("The model is down.", "unreachable");
    }

    [Fact]
    public async Task The_link_closing_ends_the_stream()
    {
        using Linked linked = await Linked.Up(HexLinkRights.Predict);
        var model = new Waiting();
        linked.Desktop.Model = model.Run;

        await using IAsyncEnumerator<string> stream = linked.Desk.Predict(ASKED).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        linked.DesktopLink.Remove(DeviceB.Id, "it was cut for the test");

        HexLinkException closed = await Assert.ThrowsAsync<HexLinkException>(async () => await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(WAIT_MS)));
        Assert.Equal(HexLinkErrors.CLOSED, closed.Code);
        await model.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(linked.Desk.ServesPredictions);
    }

    [Fact]
    public async Task Serves_predictions_follows_the_right_and_the_setting()
    {
        using Linked linked = await Linked.Up(HexLinkRights.View | HexLinkRights.Predict);
        Assert.True(linked.Desk.ServesPredictions);

        // The setting off, said at once; on again
        linked.Desktop.Serving = false;
        linked.DesktopLink.PredictionsMoved();
        Assert.True(Eventually(() => !linked.Desk.ServesPredictions));
        linked.Desktop.Serving = true;
        linked.DesktopLink.PredictionsMoved();
        Assert.True(Eventually(() => linked.Desk.ServesPredictions));

        // The right taken away, what was running cancelled with it
        var model = new Waiting();
        linked.Desktop.Model = model.Run;
        await using IAsyncEnumerator<string> stream = linked.Desk.Predict(ASKED).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        linked.LaptopMay = HexLinkRights.View;
        linked.DesktopLink.RightsMoved(DeviceB.Id);
        Assert.True(Eventually(() => !linked.Desk.ServesPredictions && linked.Desk.Granted == HexLinkRights.View));
        await model.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.MoveNextAsync());

        // And given back
        linked.LaptopMay = HexLinkRights.Predict;
        linked.DesktopLink.RightsMoved(DeviceB.Id);
        Assert.True(Eventually(() => linked.Desk.ServesPredictions));
    }

    [Fact]
    public async Task Turning_off_prediction_sharing_cancels_an_active_model()
    {
        using Linked linked = await Linked.Up(HexLinkRights.Predict);
        var model = new Waiting();
        linked.Desktop.Model = model.Run;
        await using IAsyncEnumerator<string> stream = linked.Desk.Predict(ASKED).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        linked.Desktop.Serving = false;
        linked.DesktopLink.PredictionsMoved();
        await model.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.MoveNextAsync());
    }

    [Fact]
    public async Task A_prediction_that_outpaces_the_reader_is_cancelled_instead_of_buffering_unlimited_text()
    {
        using Linked linked = await Linked.Up(HexLinkRights.Predict);
        var model = new Waiting();
        linked.Desktop.Model = model.Run;
        await using IAsyncEnumerator<string> stream = linked.Desk.Predict(ASKED).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        HexLinkSession sender = Assert.Single(linked.DesktopLink.Peers).Session;
        for (int i = 0; i < 80; i++)
            Assert.True(sender.Tell("predict/chunk", new JsonObject { ["id"] = "p1", ["text"] = "extra" }));
        await model.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var failure = await Assert.ThrowsAsync<HexLinkException>(async () =>
        {
            int count = 0;
            while (await stream.MoveNextAsync())
                Assert.InRange(++count, 1, 64);
        });
        Assert.Contains("receive buffer", failure.Message, StringComparison.Ordinal);
    }

    // A link is one stream: a big answer being written holds the next line until it's out, so a prediction's first
    // piece can wait behind an 8 MB file read on a slow network. What's tested is that a slow request (one the host is
    // still working on) never holds a prediction up, each request being answered on a task of its own.
    [Fact]
    public async Task A_slow_request_in_flight_does_not_hold_up_a_prediction()
    {
        using Linked linked = await Linked.Up(HexLinkRights.View | HexLinkRights.Predict);
        linked.Desktop.Slow.Wait = TimeSpan.FromSeconds(3);
        Task<McpToolResult> slow = linked.Desk.Call("hex_read", new JsonObject { ["path"] = "work/x" });
        await Task.Delay(50);

        var stopwatch = Stopwatch.StartNew();
        await using IAsyncEnumerator<string> stream = linked.Desk.Predict(ASKED).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.True(stopwatch.ElapsedMilliseconds < 1500, $"The first piece took {stopwatch.ElapsedMilliseconds} ms");
        Assert.False(slow.IsCompleted);
        await slow.WaitAsync(TimeSpan.FromMilliseconds(WAIT_MS));
    }
}
