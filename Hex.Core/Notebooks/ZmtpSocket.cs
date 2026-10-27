using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Hex.Core.Notebooks;

/// <summary>Which end of ZeroMQ a socket plays, the ones a Jupyter client talks to a kernel with.</summary>
internal enum ZmtpKind
{
    /// <summary>Asks and gets answers, any number in flight (the kernel's shell, control and stdin channels).</summary>
    Dealer,

    /// <summary>Takes in whatever is published that it's subscribed to (the kernel's iopub channel).</summary>
    Sub,

    /// <summary>One question then its answer (the heartbeat).</summary>
    Req
}

/// <summary>
/// One ZeroMQ socket on the client end of a TCP connection, spoken natively. ZMTP 3.0 with the NULL mechanism, which is
/// all a kernel on this machine asks for (its messages are signed with HMAC on top). A greeting each way, a READY command
/// saying what kind of socket each is, then messages of frames, each frame a flag byte (more to come, long, command) and
/// its length. No libzmq, no NetMQ, nothing to ship next to Hex.
/// <para>
/// A Dealer sends a message as it is, a Req puts an empty frame in front and takes it off the answer, a Sub sends its
/// subscriptions as messages starting with a 1, the way ZMTP 3.0 has them (libzmq 4 takes those from a 3.0 peer).
/// </para>
/// </summary>
internal sealed class ZmtpSocket : IAsyncDisposable
{
    // The flags of a frame, more of the message to come, its length in eight bytes rather than one, a command
    private const byte MORE = 0x01, LONG = 0x02, COMMAND = 0x04;

    private readonly ZmtpKind kind;
    private readonly byte[] identity;
    private readonly TcpClient client = new() { NoDelay = true };
    private NetworkStream? stream;
    private readonly SemaphoreSlim sending = new(1, 1);

    /// <summary>
    /// A socket of a kind, with an identity a ROUTER at the other end knows it by. A kernel asks for input on stdin of
    /// whoever has the identity the code was sent from on shell, so the two have to have the same one. Without, the
    /// kernel gave each a made up one and input() waited for good.
    /// </summary>
    public ZmtpSocket(ZmtpKind kind, byte[]? identity = null)
    {
        this.kind = kind;
        this.identity = identity ?? [];
    }

    /// <summary>Whether it's connected and greeted, ready for messages.</summary>
    public bool Ready { get; private set; }

    /// <summary>Connects to a kernel's port and goes through the greeting, throws when nothing answers or it says no.</summary>
    public async Task ConnectAsync(string host, int port, CancellationToken cancel)
    {
        await client.ConnectAsync(host, port, cancel).ConfigureAwait(false);
        stream = client.GetStream();

        await stream.WriteAsync(Greeting(), cancel).ConfigureAwait(false);
        byte[] theirs = new byte[64];
        await stream.ReadExactlyAsync(theirs, cancel).ConfigureAwait(false);
        if (theirs[0] != 0xFF || theirs[9] != 0x7F || theirs[10] < 3)
            throw new IOException("the kernel didn't answer as ZeroMQ 3 does");

        await WriteFrameAsync(ReadyCommand(kind, identity), COMMAND, cancel).ConfigureAwait(false);
        await stream.FlushAsync(cancel).ConfigureAwait(false);

        // Their READY (or an ERROR), what's in it doesn't matter to a client
        var (body, flags) = await ReadFrameAsync(cancel).ConfigureAwait(false);
        if ((flags & COMMAND) == 0 || !StartsWithName(body, "READY"))
            throw new IOException("the kernel didn't say it was ready: " + Encoding.ASCII.GetString(body.AsSpan(0, Math.Min(body.Length, 32))));

        Ready = true;
    }

    /// <summary>Subscribes to everything published with a topic starting so, an empty one for all of it. Sub only.</summary>
    public Task SubscribeAsync(string topic, CancellationToken cancel)
    {
        byte[] wanted = Encoding.UTF8.GetBytes(topic);
        byte[] message = new byte[wanted.Length + 1];
        message[0] = 1;
        wanted.CopyTo(message, 1);
        return SendAsync([message], cancel, raw: true);
    }

    /// <summary>Sends a message of frames. Safe from more than one thread, one message goes whole before the next.</summary>
    public async Task SendAsync(IReadOnlyList<byte[]> frames, CancellationToken cancel, bool raw = false)
    {
        NetworkStream to = stream ?? throw new InvalidOperationException("not connected");
        await sending.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (kind == ZmtpKind.Req && !raw)
                await WriteFrameAsync([], MORE, cancel).ConfigureAwait(false);
            for (int i = 0; i < frames.Count; i++)
                await WriteFrameAsync(frames[i], i < frames.Count - 1 ? MORE : (byte)0, cancel).ConfigureAwait(false);
            await to.FlushAsync(cancel).ConfigureAwait(false);
        }
        finally
        {
            sending.Release();
        }
    }

    /// <summary>
    /// The next message, every frame of it, commands in between skipped (a PING, say). A Req's empty frame in front is
    /// taken off. Throws once the connection's gone.
    /// </summary>
    public async Task<List<byte[]>> ReceiveAsync(CancellationToken cancel)
    {
        var frames = new List<byte[]>();
        while (true)
        {
            var (body, flags) = await ReadFrameAsync(cancel).ConfigureAwait(false);
            if ((flags & COMMAND) != 0)
                continue;

            frames.Add(body);
            if ((flags & MORE) == 0)
                break;
        }

        if (kind == ZmtpKind.Req && frames.Count > 0 && frames[0].Length == 0)
            frames.RemoveAt(0);
        return frames;
    }

    // Helper method for the 64 bytes each end starts with, version 3.0, the NULL mechanism, as the client
    private static byte[] Greeting()
    {
        byte[] greeting = new byte[64];
        greeting[0] = 0xFF;
        greeting[9] = 0x7F;
        greeting[10] = 3;
        greeting[11] = 0;
        Encoding.ASCII.GetBytes("NULL").CopyTo(greeting, 12);
        return greeting;
    }

    // Helper method for the READY command, its name and the one property that matters, what kind of socket this is
    private static byte[] ReadyCommand(ZmtpKind kind, byte[] identity)
    {
        string type = kind switch { ZmtpKind.Sub => "SUB", ZmtpKind.Req => "REQ", _ => "DEALER" };
        using var body = new MemoryStream();
        body.WriteByte(5);
        body.Write("READY"u8);
        Property(body, "Socket-Type", Encoding.ASCII.GetBytes(type));
        if (kind != ZmtpKind.Sub)
            Property(body, "Identity", identity);
        return body.ToArray();
    }

    private static void Property(MemoryStream into, string name, byte[] value)
    {
        into.WriteByte((byte)name.Length);
        into.Write(Encoding.ASCII.GetBytes(name));
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
        into.Write(length);
        into.Write(value);
    }

    private static bool StartsWithName(byte[] command, string name) =>
        command.Length > name.Length && command[0] == name.Length && Encoding.ASCII.GetString(command, 1, name.Length) == name;

    private async Task WriteFrameAsync(byte[] body, byte flags, CancellationToken cancel)
    {
        NetworkStream to = stream!;
        byte[] head;
        if (body.Length > 255)
        {
            head = new byte[9];
            head[0] = (byte)(flags | LONG);
            BinaryPrimitives.WriteUInt64BigEndian(head.AsSpan(1), (ulong)body.Length);
        }
        else
        {
            head = [flags, (byte)body.Length];
        }

        await to.WriteAsync(head, cancel).ConfigureAwait(false);
        if (body.Length > 0)
            await to.WriteAsync(body, cancel).ConfigureAwait(false);
    }

    private async Task<(byte[] Body, byte Flags)> ReadFrameAsync(CancellationToken cancel)
    {
        NetworkStream from = stream ?? throw new InvalidOperationException("not connected");
        byte[] flag = new byte[1];
        await from.ReadExactlyAsync(flag, cancel).ConfigureAwait(false);
        long size;
        if ((flag[0] & LONG) != 0)
        {
            byte[] eight = new byte[8];
            await from.ReadExactlyAsync(eight, cancel).ConfigureAwait(false);
            size = (long)BinaryPrimitives.ReadUInt64BigEndian(eight);
        }
        else
        {
            byte[] one = new byte[1];
            await from.ReadExactlyAsync(one, cancel).ConfigureAwait(false);
            size = one[0];
        }

        if (size > int.MaxValue)
            throw new IOException("a frame too big to take in");
        byte[] body = new byte[size];
        if (size > 0)
            await from.ReadExactlyAsync(body, cancel).ConfigureAwait(false);
        return (body, flag[0]);
    }

    public async ValueTask DisposeAsync()
    {
        Ready = false;
        if (stream is not null)
            await stream.DisposeAsync().ConfigureAwait(false);
        client.Dispose();
        sending.Dispose();
    }
}
