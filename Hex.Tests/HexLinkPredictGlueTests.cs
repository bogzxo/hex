using System.Text.Json.Nodes;

using Hex.Core;
using Hex.Dawn;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

/// <summary>
/// Edit predictions over Hex Link end to end, in memory: the laptop's LinkProvider sends its prompt through the relay's
/// Remote, which is the desktop over a link (LinkPredictionGlue.Remote); the desktop's link serves it through the
/// relay's Serve (LinkPredictionGlue.Serve) from a scripted provider. Pieces arrive in order, a cancel reaches the
/// desktop's provider at once, a device without the Predict right is refused, a desktop that doesn't share serves
/// nothing, and a provider that fails surfaces on the laptop as a PredictionException with its status. The relay is
/// static, so these share the relay tests' collection and run one at a time.
/// </summary>
[Collection(PredictionRelayTests.NAME)]
public sealed class HexLinkPredictGlueTests : IDisposable
{
    private static readonly PredictionWireRequest ASKED = new("fn main() {", "\n}", 64, ["\n\n"], "qwen");

    public void Dispose()
    {
        HexPredictionRelay.Remote = null;
        HexPredictionRelay.RemoteName = null;
        HexPredictionRelay.ProviderOverride = null;
        HexPredictionRelay.Configure(() => null, () => false);
    }

    /// <summary>The desktop's services: the kit's, with the relay bound the way DawnScene.BindLinkPredictions binds it.</summary>
    private sealed class RelayServices(LinkServices inner) : IHexLinkServices
    {
        public LinkServices Inner { get; } = inner;
        public HexLinkDevice Device => Inner.Device;
        public HexLinkWorkspace Workspace => Inner.Workspace;
        public HexHubSnapshot Fleet => Inner.Fleet;
        public IReadOnlyDictionary<int, HexLinkChatExtra> FleetExtras => Inner.FleetExtras;
        public Task<JsonObject?> Chat(int id, Func<HexAgentChat, JsonObject?> read) => Inner.Chat(id, read);
        public IMcpTools Tools => Inner.Tools;
        public Task<bool> Answer(int chat, string answer) => Inner.Answer(chat, answer);
        public void Presence(HexLinkDevice device, HexLinkPresence? presence) => Inner.Presence(device, presence);
        public Task<string> PatchOffered(HexLinkDevice device, string title, string diff) => Inner.PatchOffered(device, title, diff);
        public void Audit(string what, HexLinkDevice who, string detail) => Inner.Audit(what, who, detail);
        public bool ServesPredictions => HexPredictionRelay.Serving;
        public IAsyncEnumerable<string> Predict(HexLinkPrediction request, CancellationToken cancel) => LinkPredictionGlue.Serve(request, cancel);
    }

    /// <summary>A desktop serving a laptop, linked in memory, the laptop's relay pointed at the desktop.</summary>
    private sealed class Linked : IDisposable
    {
        private readonly RelayServices desktop = new(new LinkServices(DeviceA));
        private readonly LinkServices laptop = new(DeviceB);
        private readonly HexLink desktopLink, laptopLink;

        public HexLinkPeer Desk { get; private set; } = null!;

        private Linked()
        {
            desktopLink = new HexLink(desktop);
            laptopLink = new HexLink(laptop);
        }

        public static async Task<Linked> Up(HexLinkRights laptopMay)
        {
            var linked = new Linked();
            var (a, b) = Pair();
            Task<HexLinkPeer> there = linked.desktopLink.Add(new HexLinkConnection(a, DeviceB, "memory", true, () => laptopMay));
            Task<HexLinkPeer> desk = linked.laptopLink.Add(new HexLinkConnection(b, DeviceA, "memory", false, () => HexLinkRights.None));
            await there.WaitAsync(TimeSpan.FromMilliseconds(WAIT_MS));
            linked.Desk = await desk.WaitAsync(TimeSpan.FromMilliseconds(WAIT_MS));
            HexPredictionRelay.Remote = LinkPredictionGlue.Remote(linked.Desk);
            HexPredictionRelay.RemoteName = linked.Desk.Device.Name;
            return linked;
        }

        public void Dispose()
        {
            desktopLink.Dispose();
            laptopLink.Dispose();
            desktop.Inner.Dispose();
            laptop.Dispose();
        }
    }

    private static void Share(IPredictionProvider provider, bool share = true)
    {
        HexPredictionRelay.ProviderOverride = provider;
        HexPredictionRelay.Configure(() => PredictionTestKit.Options(), () => share);
    }

    private static async Task<List<string>> Read(CancellationToken cancel = default)
    {
        var pieces = new List<string>();
        await foreach (string piece in new LinkProvider().Complete(ASKED, cancel))
            pieces.Add(piece);
        return pieces;
    }

    [Fact]
    public async Task Pieces_from_the_desktops_model_arrive_in_order()
    {
        string[] said = [.. Enumerable.Range(0, 40).Select(i => $"t{i}")];
        var provider = new ScriptedProvider(said);
        Share(provider);
        using Linked linked = await Linked.Up(HexLinkRights.Predict);

        Assert.True(linked.Desk.ServesPredictions);
        Assert.Equal(said, await Read().WaitAsync(TimeSpan.FromMilliseconds(WAIT_MS)));
        Assert.Equal((ASKED.Prompt, ASKED.Suffix, ASKED.MaxTokens, ASKED.Format), (provider.Last!.Prompt, provider.Last.Suffix, provider.Last.MaxTokens, provider.Last.Format));
        Assert.Equal(ASKED.Stop, provider.Last.Stop);
        Assert.Equal("Desk", HexPredictionRelay.RemoteName);
    }

    [Fact]
    public async Task A_cancel_on_the_laptop_reaches_the_desktops_provider_at_once()
    {
        var provider = new ScriptedProvider((_, cancel) => ScriptedProvider.Pieces(["a"], true, cancel));
        Share(provider);
        using Linked linked = await Linked.Up(HexLinkRights.Predict);

        using var typing = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (string _ in new LinkProvider().Complete(ASKED, typing.Token))
                typing.Cancel();
        });

        Assert.True(Eventually(() => provider.Cancelled == 1, 5000));
        Assert.Equal(0, provider.Closed);
    }

    [Fact]
    public async Task Without_the_predict_right_the_laptop_is_refused()
    {
        var provider = new ScriptedProvider("a");
        Share(provider);
        using Linked linked = await Linked.Up(HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit);

        Assert.False(linked.Desk.ServesPredictions);
        PredictionException refused = await Assert.ThrowsAsync<PredictionException>(() => Read());
        Assert.Equal(PredictionStatus.Refused, refused.Status);
        Assert.Equal(0, provider.Asked);
    }

    [Fact]
    public async Task A_desktop_that_does_not_share_serves_nothing()
    {
        var provider = new ScriptedProvider("a");
        Share(provider, share: false);
        using Linked linked = await Linked.Up(HexLinkRights.Predict);

        Assert.False(linked.Desk.ServesPredictions);
        PredictionException refused = await Assert.ThrowsAsync<PredictionException>(() => Read());
        Assert.Equal(PredictionStatus.Refused, refused.Status);
        Assert.Equal(0, provider.Asked);
    }

    [Fact]
    public async Task A_provider_that_fails_surfaces_on_the_laptop_with_its_status()
    {
        Share(new ScriptedProvider((_, cancel) => Failing(cancel)));
        using Linked linked = await Linked.Up(HexLinkRights.Predict);

        PredictionException failed = await Assert.ThrowsAsync<PredictionException>(() => Read());
        Assert.Equal(PredictionStatus.Unreachable, failed.Status);
        Assert.Contains("The model's server is down.", failed.Message);
        Assert.Contains("Desk", failed.Message);
    }

    private static async IAsyncEnumerable<string> Failing([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancel)
    {
        await Task.Yield();
        yield return "half";
        throw new PredictionException(PredictionStatus.Unreachable, "The model's server is down.");
    }

    [Fact]
    public void Statuses_go_over_the_wire_and_come_back()
    {
        foreach (PredictionStatus status in Enum.GetValues<PredictionStatus>())
            Assert.Equal(status, LinkPredictionGlue.StatusOf(LinkPredictionGlue.Word(status)));
        Assert.Equal(PredictionStatus.BadAnswer, LinkPredictionGlue.StatusOf("failed"));
        Assert.Equal(PredictionStatus.BadAnswer, LinkPredictionGlue.StatusOf("3"));
    }
}
