using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// What the link tests stand on: a connection's two ends in memory, an editor's services made of a real HexAgentHub
/// and a temp folder, and two Hexes linked over them. The lock <see cref="LinkServices.Editor"/> stands in for the
/// editor's thread, the tests change chats inside it and the services read them inside it.
/// </summary>
internal static class HexLinkTestKit
{
    public const int WAIT_MS = 15_000;

    /// <summary>One direction of a connection, what's written read on the other side in the same pieces.</summary>
    public sealed class Wire
    {
        public Channel<byte[]> Bytes { get; } = Channel.CreateUnbounded<byte[]>();
    }

    /// <summary>One end of a connection in memory, reading one wire and writing the other.</summary>
    public sealed class End(Wire input, Wire output) : Stream
    {
        private byte[] left = [];
        private int at;
        private int disposed;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
        {
            if (at >= left.Length)
            {
                try
                {
                    left = await input.Bytes.Reader.ReadAsync(cancel);
                    at = 0;
                }
                catch (ChannelClosedException)
                {
                    return 0;
                }
            }

            int count = Math.Min(buffer.Length, left.Length - at);
            left.AsSpan(at, count).CopyTo(buffer.Span);
            at += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancel) => ReadAsync(buffer.AsMemory(offset, count), cancel).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (disposed != 0 || !output.Bytes.Writer.TryWrite(buffer.ToArray()))
                throw new IOException("The other end is gone.");
        }

        public override void WriteByte(byte value) => Write([value]);

        /// <summary>Raw bytes written as if the other end sent them, for garbage.</summary>
        public void Inject(byte[] bytes) => input.Bytes.Writer.TryWrite(bytes);

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                output.Bytes.Writer.TryComplete();
                input.Bytes.Writer.TryComplete();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>The two ends of a connection.</summary>
    public static (End A, End B) Pair()
    {
        Wire toA = new(), toB = new();
        return (new End(toA, toB), new End(toB, toA));
    }

    public static HexLinkDevice DeviceA { get; } = new(new string('A', 64), "Desk", "windows", "x64", "3.3.0");
    public static HexLinkDevice DeviceB { get; } = new(new string('B', 64), "Laptop", "macos", "arm64", "3.3.0");
    public static HexLinkDevice DeviceC { get; } = new(new string('C', 64), "Tablet", "linux", "arm64", "3.3.0");

    /// <summary>Two sessions over one connection in memory, A's end and B's, not started.</summary>
    public static (HexLinkSession A, HexLinkSession B) Sessions(Func<HexLinkRights>? rightsOfB = null, Func<HexLinkRights>? rightsOfA = null)
    {
        var (a, b) = Pair();
        // A's end of the connection is to B: what B may do on A
        return (new HexLinkSession(new HexLinkConnection(a, DeviceB, "memory", true, rightsOfB ?? (() => HexLinkRights.View))),
            new HexLinkSession(new HexLinkConnection(b, DeviceA, "memory", false, rightsOfA ?? (() => HexLinkRights.View))));
    }

    /// <summary>Waits until something is so, false when it never was.</summary>
    public static bool Eventually(Func<bool> so, int ms = WAIT_MS)
    {
        long until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            if (so())
                return true;
            Thread.Sleep(10);
        }

        return so();
    }

    /// <summary>The error code an ask failed with, null when it didn't.</summary>
    public static async Task<int?> CodeOf(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (HexLinkException e)
        {
            return e.Code;
        }
    }
}

/// <summary>Tools standing in for the editor's: every tool on the allow list and some that never are, each saying what it was called with.</summary>
internal sealed class LinkTools : IMcpTools
{
    public ConcurrentQueue<(string Name, JsonObject Arguments)> Called { get; } = new();

    public IReadOnlyList<McpTool> Tools { get; } =
    [
        .. HexLinkAllowed.Names.Select(name => new McpTool(name, name, $"The stand in for {name}.",
            name == "hex_read" ? McpSchema.Object([("path", McpSchema.String("The file."))], "path")
            : McpSchema.Object([("id", McpSchema.Integer("A chat.")), ("text", McpSchema.String("Text.")), ("allow", McpSchema.Flag("Allow.")), ("request", McpSchema.Integer("Which.")),
                ("path", McpSchema.String("A path."))]))),
        new McpTool("hex_terminal_run", "Terminal", "Runs a command.", McpSchema.Object([("command", McpSchema.String("What."))], "command")),
        new McpTool("hex_screenshot", "Screenshot", "Takes one.", McpSchema.Empty())
    ];

    public Task<McpToolResult> Call(string name, JsonObject arguments, CancellationToken cancel)
    {
        Called.Enqueue((name, arguments));
        return Task.FromResult(McpToolResult.Text($"{name} {arguments.ToJsonString()}"));
    }
}

/// <summary>An editor's services in memory: a hub, a temp workspace, tools standing in, and what the link told it.</summary>
internal sealed class LinkServices : IHexLinkServices, IDisposable
{
    public LinkServices(HexLinkDevice device)
    {
        Device = device;
        Root = Path.Combine(Path.GetTempPath(), "hexlink-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "work"));
        Workspace = new HexLinkWorkspace("Test", [new HexLinkFolder("work", Path.Combine(Root, "work"))], []);
    }

    /// <summary>The lock standing in for the editor's thread.</summary>
    public Lock Editor { get; } = new();

    public HexAgentHub Hub { get; } = new();

    public string Root { get; }

    public string Work => Workspace.Folders[0].Path;

    public HexLinkDevice Device { get; }

    public HexLinkWorkspace Workspace { get; set; }

    public HexLinkDocuments Documents { get; } = new();

    public HexHubSnapshot Fleet => Hub.Snapshot;

    public IReadOnlyDictionary<int, HexLinkChatExtra> FleetExtras { get; set; } = new Dictionary<int, HexLinkChatExtra>();

    public LinkTools LinkTools { get; } = new();

    public IMcpTools Tools => LinkTools;

    public ConcurrentQueue<(string What, HexLinkDevice Who, string Detail)> Audited { get; } = new();

    public ConcurrentQueue<(HexLinkDevice Who, HexLinkPresence? Where)> Presences { get; } = new();

    public ConcurrentQueue<(int Chat, string Answer)> Answers { get; } = new();

    public ConcurrentQueue<(string Title, string Diff)> Offered { get; } = new();

    public string PatchAnswer { get; set; } = "kept";

    public Task<JsonObject?> Chat(int id, Func<HexAgentChat, JsonObject?> read) => Task.Run(() =>
    {
        lock (Editor)
            return Hub.ChatById(id) is { } chat ? read(chat) : null;
    });

    public Task<bool> Answer(int chat, string answer)
    {
        Answers.Enqueue((chat, answer));
        return Task.FromResult(true);
    }

    public void Presence(HexLinkDevice device, HexLinkPresence? presence) => Presences.Enqueue((device, presence));

    public Task<string> PatchOffered(HexLinkDevice device, string title, string diff)
    {
        Offered.Enqueue((title, diff));
        return Task.FromResult(PatchAnswer);
    }

    public void Audit(string what, HexLinkDevice who, string detail) => Audited.Enqueue((what, who, detail));

    /// <summary>Changes chats as the editor's thread would, then pumps the hub.</summary>
    public void OnEditor(Action change)
    {
        lock (Editor)
        {
            change();
            Hub.Pump(8000);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch (Exception) { }
    }
}

/// <summary>
/// Two Hexes linked in memory, A and B, each with its services. What B may do on A and A on B are set by
/// <see cref="BOnA"/> and <see cref="AOnB"/>, read afresh on every request.
/// </summary>
internal sealed class LinkedPair : IDisposable
{
    public LinkServices A { get; } = new(HexLinkTestKit.DeviceA);
    public LinkServices B { get; } = new(HexLinkTestKit.DeviceB);
    public HexLink LinkA { get; }
    public HexLink LinkB { get; }
    public HexLinkRights BOnA { get; set; }
    public HexLinkRights AOnB { get; set; }

    /// <summary>A's view of B (what A asks B), and B's view of A.</summary>
    public HexLinkPeer PeerB { get; private set; } = null!;
    public HexLinkPeer PeerA { get; private set; } = null!;

    public LinkedPair(HexLinkRights bOnA, HexLinkRights aOnB, TimeSpan? watchEvery = null)
    {
        BOnA = bOnA;
        AOnB = aOnB;
        LinkA = new HexLink(A) { WatchEvery = watchEvery ?? TimeSpan.FromMilliseconds(100) };
        LinkB = new HexLink(B) { WatchEvery = watchEvery ?? TimeSpan.FromMilliseconds(100) };
    }

    public static async Task<LinkedPair> Up(HexLinkRights bOnA, HexLinkRights aOnB = HexLinkRights.View)
    {
        var pair = new LinkedPair(bOnA, aOnB);
        var (a, b) = HexLinkTestKit.Pair();
        Task<HexLinkPeer> peerB = pair.LinkA.Add(new HexLinkConnection(a, HexLinkTestKit.DeviceB, "memory", true, () => pair.BOnA));
        Task<HexLinkPeer> peerA = pair.LinkB.Add(new HexLinkConnection(b, HexLinkTestKit.DeviceA, "memory", false, () => pair.AOnB));
        pair.PeerB = await peerB.WaitAsync(TimeSpan.FromMilliseconds(HexLinkTestKit.WAIT_MS));
        pair.PeerA = await peerA.WaitAsync(TimeSpan.FromMilliseconds(HexLinkTestKit.WAIT_MS));
        return pair;
    }

    public void Dispose()
    {
        LinkA.Dispose();
        LinkB.Dispose();
        A.Dispose();
        B.Dispose();
    }
}
