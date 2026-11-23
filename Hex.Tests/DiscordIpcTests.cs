using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Hex.Core;

namespace Hex.Tests;

/// <summary>Discord's IPC client against a fake Discord: frames, the handshake, SET_ACTIVITY, pings, and the real transports.</summary>
public class DiscordIpcTests
{
    private static readonly DiscordActivity Sample = new("Editing a.cs", "in hex", 1_700_000_000, "cs", "C#", "hex", "Hex 3.4.7", "Get Hex", HexDiscord.Website);

    // The fake Discord's side of a connection: reads what the client sent, answers READY
    private sealed class FakeDiscord : IDiscordConnector
    {
        public readonly List<(int Opcode, string Json)> Received = [];
        public readonly SemaphoreSlim Got = new(0, int.MaxValue);
        public Stream? Server;
        public bool Refuse;
        public bool SendReady = true;
        public int Connections;

        public ValueTask<Stream?> ConnectAsync(CancellationToken cancel)
        {
            if (Refuse)
                return ValueTask.FromResult<Stream?>(null);

            Connections++;
            var (client, serverSide) = Duplex.Make();
            Server = serverSide;
            _ = Task.Run(() => Serve(serverSide));
            return ValueTask.FromResult<Stream?>(client);
        }

        private async Task Serve(Stream server)
        {
            try
            {
                while (await DiscordFrame.ReadAsync(server, CancellationToken.None) is { } frame)
                {
                    lock (Received)
                        Received.Add((frame.Opcode, Encoding.UTF8.GetString(frame.Payload)));
                    Got.Release();
                    if (frame.Opcode == DiscordFrame.HANDSHAKE && SendReady)
                    {
                        byte[] ready = DiscordFrame.Encode(DiscordFrame.FRAME, "{\"cmd\":\"DISPATCH\",\"evt\":\"READY\",\"data\":{}}");
                        await server.WriteAsync(ready);
                        await server.FlushAsync();
                    }
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }

        public async Task<(int Opcode, string Json)> Next(int index)
        {
            for (int tries = 0; tries < 200; tries++)
            {
                lock (Received)
                {
                    if (Received.Count > index)
                        return Received[index];
                }

                await Task.Delay(25);
            }

            throw new TimeoutException($"frame {index} never came");
        }

        public int Count
        {
            get { lock (Received) return Received.Count; }
        }
    }

    // Two ends of a byte pipe each way, in memory
    private sealed class Duplex(Stream read, Stream write) : Stream
    {
        public static (Stream Client, Stream Server) Make()
        {
            var up = new Channel();
            var down = new Channel();
            return (new Duplex(down, up), new Duplex(up, down));
        }

        private sealed class Channel : Stream
        {
            private readonly System.Threading.Channels.Channel<byte[]> chunks = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
            private byte[] current = [];
            private int at;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => chunks.Writer.TryWrite(buffer.AsSpan(offset, count).ToArray());

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
            {
                while (at >= current.Length)
                {
                    try
                    {
                        current = await chunks.Reader.ReadAsync(cancel);
                        at = 0;
                    }
                    catch (System.Threading.Channels.ChannelClosedException)
                    {
                        return 0;
                    }
                }

                int take = Math.Min(buffer.Length, current.Length - at);
                current.AsMemory(at, take).CopyTo(buffer);
                at += take;
                return take;
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancel = default)
            {
                chunks.Writer.TryWrite(buffer.ToArray());
                return ValueTask.CompletedTask;
            }

            protected override void Dispose(bool disposing)
            {
                chunks.Writer.TryComplete();
                base.Dispose(disposing);
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default) => read.ReadAsync(buffer, cancel);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancel = default) => write.WriteAsync(buffer, cancel);

        protected override void Dispose(bool disposing)
        {
            write.Dispose();
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task A_frame_is_an_opcode_a_length_and_the_json_little_endian()
    {
        byte[] frame = DiscordFrame.Encode(1, "{\"a\":1}");
        Assert.Equal([1, 0, 0, 0, 7, 0, 0, 0], frame[..8]);
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(frame[8..]));

        var read = await DiscordFrame.ReadAsync(new MemoryStream(frame), CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(1, read!.Value.Opcode);
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(read.Value.Payload));
    }

    [Fact]
    public async Task A_stream_that_ends_or_lies_about_its_length_reads_as_no_frame()
    {
        Assert.Null(await DiscordFrame.ReadAsync(new MemoryStream([]), CancellationToken.None));
        Assert.Null(await DiscordFrame.ReadAsync(new MemoryStream([1, 0, 0, 0, 9, 0, 0, 0, 65]), CancellationToken.None));
        byte[] huge = [1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x7F];
        Assert.Null(await DiscordFrame.ReadAsync(new MemoryStream(huge), CancellationToken.None));
    }

    [Fact]
    public void The_handshake_and_the_activity_are_the_json_discord_wants()
    {
        using JsonDocument hello = JsonDocument.Parse(DiscordJson.Handshake("123"));
        Assert.Equal(1, hello.RootElement.GetProperty("v").GetInt32());
        Assert.Equal("123", hello.RootElement.GetProperty("client_id").GetString());

        using JsonDocument set = JsonDocument.Parse(DiscordJson.SetActivity(42, Sample, "n-1"));
        JsonElement root = set.RootElement;
        Assert.Equal("SET_ACTIVITY", root.GetProperty("cmd").GetString());
        Assert.Equal("n-1", root.GetProperty("nonce").GetString());
        Assert.Equal(42, root.GetProperty("args").GetProperty("pid").GetInt32());
        JsonElement activity = root.GetProperty("args").GetProperty("activity");
        Assert.Equal("Editing a.cs", activity.GetProperty("details").GetString());
        Assert.Equal("in hex", activity.GetProperty("state").GetString());
        Assert.Equal(1_700_000_000, activity.GetProperty("timestamps").GetProperty("start").GetInt64());
        Assert.Equal("cs", activity.GetProperty("assets").GetProperty("large_image").GetString());
        Assert.Equal("C#", activity.GetProperty("assets").GetProperty("large_text").GetString());
        Assert.Equal("hex", activity.GetProperty("assets").GetProperty("small_image").GetString());
        Assert.Equal("Get Hex", activity.GetProperty("buttons")[0].GetProperty("label").GetString());

        using JsonDocument clear = JsonDocument.Parse(DiscordJson.SetActivity(42, null, "n-2"));
        Assert.Equal(JsonValueKind.Null, clear.RootElement.GetProperty("args").GetProperty("activity").ValueKind);
    }

    [Fact]
    public async Task The_client_shakes_hands_sets_the_activity_answers_pings_and_clears()
    {
        var fake = new FakeDiscord();
        await using DiscordIpcClient? client = await DiscordIpcClient.ConnectAsync(fake, "999", TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.NotNull(client);

        var (opcode, json) = await fake.Next(0);
        Assert.Equal(DiscordFrame.HANDSHAKE, opcode);
        Assert.Contains("\"client_id\":\"999\"", json);

        await client!.SetActivityAsync(Sample, CancellationToken.None);
        (opcode, json) = await fake.Next(1);
        Assert.Equal(DiscordFrame.FRAME, opcode);
        Assert.Contains("\"details\":\"Editing a.cs\"", json);

        await fake.Server!.WriteAsync(DiscordFrame.Encode(DiscordFrame.PING, "{\"p\":1}"));
        (opcode, json) = await fake.Next(2);
        Assert.Equal(DiscordFrame.PONG, opcode);
        Assert.Equal("{\"p\":1}", json);

        await client.SetActivityAsync(null, CancellationToken.None);
        (_, json) = await fake.Next(3);
        Assert.Contains("\"activity\":null", json);
    }

    [Fact]
    public async Task No_discord_or_one_that_never_says_ready_is_no_client()
    {
        Assert.Null(await DiscordIpcClient.ConnectAsync(new FakeDiscord { Refuse = true }, "1", TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Null(await DiscordIpcClient.ConnectAsync(new FakeDiscord { SendReady = false }, "1", TimeSpan.FromMilliseconds(200), CancellationToken.None));
    }

    [Fact]
    public async Task The_service_connects_sends_straight_away_then_keeps_only_the_latest_and_clears_on_dispose()
    {
        var fake = new FakeDiscord();
        DiscordActivity Make(string details) => Sample with { Details = details };
        var service = new DiscordPresenceService("5", fake, new DiscordPacing(firstRetry: 50, longestRetry: 200, sendEvery: 300));

        service.Update(Make("one"));
        Assert.Equal(DiscordFrame.HANDSHAKE, (await fake.Next(0)).Opcode);
        Assert.Contains("one", (await fake.Next(1)).Json);

        // Three more inside the pause, only the last of them goes out when it's over
        service.Update(Make("two"));
        service.Update(Make("three"));
        service.Update(Make("four"));
        Assert.Contains("four", (await fake.Next(2)).Json);
        await Task.Delay(100);
        Assert.Equal(3, fake.Count);

        await service.DisposeAsync();
        Assert.Contains("\"activity\":null", (await fake.Next(3)).Json);
    }

    [Fact]
    public async Task The_service_keeps_trying_until_discord_is_there()
    {
        var fake = new FakeDiscord { Refuse = true };
        var service = new DiscordPresenceService("5", fake, new DiscordPacing(firstRetry: 30, longestRetry: 60, sendEvery: 10));
        service.Update(Sample);
        await Task.Delay(150);
        Assert.Equal(0, fake.Count);

        fake.Refuse = false;
        Assert.Equal(DiscordFrame.HANDSHAKE, (await fake.Next(0)).Opcode);
        Assert.Contains("Editing a.cs", (await fake.Next(1)).Json);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task Switching_the_presence_off_while_discord_is_not_there_connects_to_nothing()
    {
        var fake = new FakeDiscord { Refuse = true };
        var service = new DiscordPresenceService("5", fake, new DiscordPacing(firstRetry: 20, longestRetry: 40, sendEvery: 10));
        service.Update(Sample);
        service.Update(null);
        fake.Refuse = false;
        await Task.Delay(200);
        Assert.Equal(0, fake.Connections);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task The_real_transport_speaks_to_a_listener()
    {
        // A named pipe on Windows, a unix socket elsewhere, the same code the system connector opens
        string name = "hex-test-" + Guid.NewGuid().ToString("N")[..10];
        if (OperatingSystem.IsWindows())
        {
            await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            Task accepted = server.WaitForConnectionAsync();
            await using Stream? client = await DiscordSystemConnector.OpenAsync(name, CancellationToken.None);
            Assert.NotNull(client);
            await accepted;
            // A pipe has no buffer here, a write waits for the read
            var reading = DiscordFrame.ReadAsync(server, CancellationToken.None).AsTask();
            await client!.WriteAsync(DiscordFrame.Encode(DiscordFrame.HANDSHAKE, "{\"v\":1}"));
            var frame = await reading;
            Assert.Equal(DiscordFrame.HANDSHAKE, frame!.Value.Opcode);
        }
        else if (!OperatingSystem.IsBrowser())
        {
            string path = Path.Combine(Path.GetTempPath(), name + ".sock");
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            try
            {
                Task<Socket> accepted = listener.AcceptAsync();
                await using Stream? client = await DiscordSystemConnector.OpenAsync(path, CancellationToken.None);
                Assert.NotNull(client);
                using Socket peer = await accepted;
                await client!.WriteAsync(DiscordFrame.Encode(DiscordFrame.HANDSHAKE, "{\"v\":1}"));
                var frame = await DiscordFrame.ReadAsync(new NetworkStream(peer), CancellationToken.None);
                Assert.Equal(DiscordFrame.HANDSHAKE, frame!.Value.Opcode);
            }
            finally
            {
                File.Delete(path);
            }
        }

        Assert.Null(await DiscordSystemConnector.OpenAsync(OperatingSystem.IsWindows() ? "hex-no-such-pipe-" + name : "/nonexistent/" + name, CancellationToken.None));
    }

    [Fact]
    public void Where_discord_listens_is_by_system_and_environment()
    {
        string[] windows = [.. DiscordSystemConnector.Candidates(true, _ => null, _ => false)];
        Assert.Equal(10, windows.Length);
        Assert.Equal("discord-ipc-0", windows[0]);

        var env = new Dictionary<string, string> { ["XDG_RUNTIME_DIR"] = "/run/user/1000", ["TMPDIR"] = "/var/tmp/" };
        string[] unix = [.. DiscordSystemConnector.Candidates(false, key => env.GetValueOrDefault(key),
            folder => folder is "/run/user/1000/" or "/run/user/1000/app/com.discordapp.Discord/" or "/var/tmp/" or "/tmp/")];
        Assert.Equal("/run/user/1000/discord-ipc-0", unix[0]);
        Assert.Contains("/run/user/1000/app/com.discordapp.Discord/discord-ipc-3", unix);
        Assert.Contains("/var/tmp/discord-ipc-9", unix);
        Assert.Equal("/tmp/discord-ipc-0", unix[^10]);
        Assert.DoesNotContain(unix, path => path.Contains("snap.discord"));
    }

    [Fact]
    public void The_application_id_is_the_environments_when_it_has_one()
    {
        Assert.Equal("abc", HexDiscord.EffectiveApplicationId(key => key == "HEX_DISCORD_APP_ID" ? " abc " : null));
        Assert.Equal(HexDiscord.ApplicationId, HexDiscord.EffectiveApplicationId(_ => null));
    }
}
